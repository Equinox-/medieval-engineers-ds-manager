using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Meds.Dist;
using Meds.Shared;
using Meds.Watchdog.Steam;
using Microsoft.Extensions.Logging;
using SteamKit2.Internal;
using ZLogger;

namespace Meds.Watchdog
{
    public sealed class Updater
    {
        private readonly InstallConfiguration _installConfig;
        private readonly Refreshable<Configuration> _runtimeConfig;
        private readonly SteamDownloader _downloader;
        private readonly ILogger<Updater> _log;
        public const uint MedievalDsAppId = 367970;
        public const uint MedievalDsDepotId = 367971;
        public const uint MedievalGameAppId = 333950;
        private const uint SteamRedistDepotId = 1004;

        private int _logins;

        public Updater(ILogger<Updater> log,
            InstallConfiguration installConfig,
            Refreshable<Configuration> runtimeConfig,
            SteamDownloader downloader)
        {
            _log = log;
            _installConfig = installConfig;
            _runtimeConfig = runtimeConfig;
            _downloader = downloader;
        }

        private async ValueTask LoginInternal()
        {
            if (Interlocked.Increment(ref _logins) == 1) await _downloader.LoginAsync();
        }

        private async ValueTask LogoutInternal()
        {
            if (Interlocked.Decrement(ref _logins) == 0) await _downloader.LogoutAsync();
        }

        public ValueTask<LoginToken> Login() => LoginToken.Of(this);

        public readonly struct LoginToken : IAsyncDisposable
        {
            private readonly Updater _updater;

            private LoginToken(Updater updater) => _updater = updater;

            public static async ValueTask<LoginToken> Of(Updater updater)
            {
                await updater.LoginInternal();
                return new LoginToken(updater);
            }

            public ValueTask DisposeAsync() => _updater.LogoutInternal();
        }

        public async Task Update(CancellationToken cancellationToken)
        {
            await using var loginToken = await Login();
            await UpdateInternal(cancellationToken);
        }

        public async Task<Dictionary<ulong, PublishedFileDetails>> LoadModDetails(IEnumerable<ulong> mods)
        {
            await using var loginToken = await Login();
            return await _downloader.LoadModDetails(MedievalGameAppId, mods);
        }

        public async Task<List<CPublishedFile_GetChangeHistory_Response.ChangeLog>> LoadModChangeHistory(ulong modId, uint sinceTime)
        {
            await using var loginToken = await Login();
            return await _downloader.LoadModChangeHistory(modId, sinceTime);
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
            var redistFiles = await _downloader.InstallAppAsync(MedievalDsAppId, SteamRedistDepotId, "public",
                installPath, 4,
                path => !overlayFiles.Contains(path), "steam-redist",
                installPrefix: "DedicatedServer64");

            _log.ZLogInformation("Updating Medieval Engineers");
            await _downloader.InstallAppAsync(MedievalDsAppId, MedievalDsDepotId, _runtimeConfig.Current.Steam.Branch, installPath, 4,
                branchPassword: _runtimeConfig.Current.Steam.BranchPassword,
                installFilter: path => !overlayFiles.Contains(path) && !redistFiles.InstalledFiles.Contains(path),
                debugName: "medieval-ds");

            // Apply overlays
            foreach (var overlay in overlays)
                await overlay.ApplyOverlay();
        }
    }
}