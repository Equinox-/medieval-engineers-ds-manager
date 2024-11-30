using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.RateLimiting;
using System.Threading.Tasks;
using Meds.Dist;
using Meds.Shared;
using Meds.Watchdog.Steam;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SteamKit2.Internal;
using ZLogger;

namespace Meds.Watchdog
{
    public sealed class Updater
    {
        private readonly InstallConfiguration _installConfig;
        private readonly Refreshable<Configuration> _runtimeConfig;
        private readonly ILogger<Updater> _log;
        private readonly IServiceProvider _svc;
        private const int MaxPermits = 32;
        public const uint MedievalDsAppId = 367970;
        public const uint MedievalDsDepotId = 367971;
        public const uint MedievalGameAppId = 333950;
        private const uint SteamRedistDepotId = 1004;


        public Updater(ILogger<Updater> log,
            InstallConfiguration installConfig,
            Refreshable<Configuration> runtimeConfig,
            IServiceProvider svc)
        {
            _log = log;
            _installConfig = installConfig;
            _runtimeConfig = runtimeConfig;
            _svc = svc;
        }

        public class UpdaterToken
        {
            private readonly Updater _updater;
            private readonly SteamDownloader _downloader;
            private readonly ConcurrencyLimiter _limiter;

            public UpdaterToken(Updater updater, SteamDownloader downloader)
            {
                _updater = updater;
                _downloader = downloader;
                _limiter = new ConcurrencyLimiter(new ConcurrencyLimiterOptions
                    { PermitLimit = MaxPermits, QueueProcessingOrder = QueueProcessingOrder.OldestFirst });
            }

            public Task<Dictionary<ulong, PublishedFileDetails>> LoadModDetails(IEnumerable<ulong> mods)
            {
                return Run(() => _downloader.LoadModDetails(MedievalGameAppId, mods));
            }

            public Task<List<CPublishedFile_GetChangeHistory_Response.ChangeLog>> LoadModChangeHistory(ulong modId, uint sinceTime)
            {
                return Run(() => _downloader.LoadModChangeHistory(modId, sinceTime));
            }

            public Task Update(CancellationToken cancellationToken)
            {
                return UpdateInternal(cancellationToken);
            }

            private async Task UpdateInternal(CancellationToken cancellationToken)
            {
                var installPath = _updater._installConfig.InstallDirectory;
                var overlays = await _updater.LoadOverlays(installPath);

                // Clean deleted overlay files
                await Task.WhenAll(overlays.Select(overlay => Task.Run(overlay.CleanDeleted, cancellationToken)));

                var overlayFiles = new HashSet<string>(overlays.SelectMany(overlay =>
                    overlay.Remote.Files.Select(remoteFile => Path.Combine(overlay.Spec.Path, remoteFile.Path))));

                _updater._log.ZLogInformation("Updating Steam SDK Redist");
                var redistFiles = await Run(() => _downloader.InstallAppAsync(MedievalDsAppId, SteamRedistDepotId, "public",
                    installPath, 4,
                    path => !overlayFiles.Contains(path), "steam-redist",
                    installPrefix: "DedicatedServer64"), cancellationToken);

                _updater._log.ZLogInformation("Updating Medieval Engineers");
                await Run(() => _downloader.InstallAppAsync(MedievalDsAppId, MedievalDsDepotId,
                        _updater._runtimeConfig.Current.Steam.Branch, installPath, 4,
                        branchPassword: _updater._runtimeConfig.Current.Steam.BranchPassword,
                        installFilter: path => !overlayFiles.Contains(path) && !redistFiles.InstalledFiles.Contains(path),
                        debugName: "medieval-ds"),
                    cancellationToken);

                // Apply overlays
                foreach (var overlay in overlays)
                    await overlay.ApplyOverlay();
            }

            private async Task<T> Run<T>(Func<Task<T>> call, CancellationToken cancellationToken = default)
            {
                var attempt = 0;
                while (true)
                {
                    if (cancellationToken.IsCancellationRequested)
                        throw new TaskCanceledException();
                    attempt++;
                    try
                    {
                        var reAuth = attempt > 2;
                        using var lease = await _limiter.AcquireAsync(reAuth ? MaxPermits : 1, cancellationToken);
                        if (reAuth)
                        {
                            await _downloader.LogoutAsync();
                            await _downloader.LoginAsync();
                        }

                        return await call();
                    }
                    catch
                    {
                        if (attempt >= 5) throw;
                    }
                }
            }
        }

        public async Task Run(Func<UpdaterToken, Task> callback)
        {
            await Run(async tok =>
            {
                await callback(tok);
                return 0;
            });
        }

        public async Task<T> Run<T>(Func<UpdaterToken, Task<T>> callback)
        {
            using var scope = _svc.CreateScope();
            var downloader = scope.ServiceProvider.GetRequiredService<SteamDownloader>();
            await downloader.LoginAsync();
            try
            {
                return await callback(new UpdaterToken(this, downloader));
            }
            finally
            {
                await downloader.LogoutAsync();
            }
        }

        private Task<OverlayData[]> LoadOverlays(string installPath)
        {
            var wrappers = _runtimeConfig.Current.WrapperLayers;
            if (wrappers == null || wrappers.Count == 0)
                return Task.FromResult(Array.Empty<OverlayData>());
            var log = new OverlayLogger(_log);
            return Task.WhenAll(wrappers.Select(async spec =>
            {
                var data = new OverlayData(log, installPath, spec);
                await data.Load();
                return data;
            }).ToArray());
        }

        private sealed class OverlayLogger : IOverlayLogger
        {
            private readonly ILogger _log;

            public OverlayLogger(ILogger log) => _log = log;

            public void Debug(string msg) => _log.ZLogDebug(msg);

            public void Info(string msg) => _log.ZLogInformation(msg);
        }
    }
}