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
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SteamKit2.Internal;
using ZLogger;

namespace Meds.Watchdog
{
    public sealed class Updater : IHostedService
    {
        private readonly InstallConfiguration _installConfig;
        private readonly Refreshable<Configuration> _runtimeConfig;
        private readonly SteamDownloader _downloader;
        private readonly ILogger<Updater> _log;
        private readonly ConcurrencyLimiter _limiter;
        private const int MaxPermits = 32;
        public const uint MedievalDsAppId = 367970;
        public const uint MedievalDsDepotId = 367971;
        public const uint MedievalGameAppId = 333950;
        private const uint SteamRedistDepotId = 1004;


        public Updater(ILogger<Updater> log,
            InstallConfiguration installConfig,
            Refreshable<Configuration> runtimeConfig,
            SteamDownloader downloader)
        {
            _log = log;
            _installConfig = installConfig;
            _runtimeConfig = runtimeConfig;
            _downloader = downloader;
            _limiter = new ConcurrencyLimiter(new ConcurrencyLimiterOptions
                { PermitLimit = MaxPermits, QueueProcessingOrder = QueueProcessingOrder.OldestFirst });
        }

        public Task StartAsync(CancellationToken cancellationToken) => _downloader.LoginAsync();

        public Task StopAsync(CancellationToken cancellationToken) => _downloader.LogoutAsync();

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
                    using var lease = await _limiter.AcquireAsync(reAuth ? MaxPermits : 1);
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

        public Task Update(CancellationToken cancellationToken)
        {
            return UpdateInternal(cancellationToken);
        }

        public Task<Dictionary<ulong, PublishedFileDetails>> LoadModDetails(IEnumerable<ulong> mods)
        {
            return Run(() => _downloader.LoadModDetails(MedievalGameAppId, mods));
        }

        public Task<List<CPublishedFile_GetChangeHistory_Response.ChangeLog>> LoadModChangeHistory(ulong modId, uint sinceTime)
        {
            return Run(() => _downloader.LoadModChangeHistory(modId, sinceTime));
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

        private async Task UpdateInternal(CancellationToken cancellationToken)
        {
            var installPath = _installConfig.InstallDirectory;
            var overlays = await LoadOverlays(installPath);

            // Clean deleted overlay files
            await Task.WhenAll(overlays.Select(overlay => Task.Run(overlay.CleanDeleted, cancellationToken)));

            var overlayFiles = new HashSet<string>(overlays.SelectMany(overlay =>
                overlay.Remote.Files.Select(remoteFile => Path.Combine(overlay.Spec.Path, remoteFile.Path))));

            _log.ZLogInformation("Updating Steam SDK Redist");
            var redistFiles = await Run(() => _downloader.InstallAppAsync(MedievalDsAppId, SteamRedistDepotId, "public",
                installPath, 4,
                path => !overlayFiles.Contains(path), "steam-redist",
                installPrefix: "DedicatedServer64"), cancellationToken);

            _log.ZLogInformation("Updating Medieval Engineers");
            await Run(() => _downloader.InstallAppAsync(MedievalDsAppId, MedievalDsDepotId, _runtimeConfig.Current.Steam.Branch, installPath, 4,
                    branchPassword: _runtimeConfig.Current.Steam.BranchPassword,
                    installFilter: path => !overlayFiles.Contains(path) && !redistFiles.InstalledFiles.Contains(path),
                    debugName: "medieval-ds"),
                cancellationToken);

            // Apply overlays
            foreach (var overlay in overlays)
                await overlay.ApplyOverlay();
        }
    }
}