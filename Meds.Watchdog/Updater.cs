using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Meds.Dist;
using Meds.Shared;
using Meds.Watchdog.Steam;
using Microsoft.Extensions.Logging;
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

        public async Task Update(CancellationToken cancellationToken)
        {
            await _downloader.LoginAsync();
            try
            {
                await UpdateInternal(cancellationToken);
            }
            finally
            {
                await _downloader.LogoutAsync();
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
                path => !overlayFiles.Contains(path) && !redistFiles.InstalledFiles.Contains(path), "medieval-ds");

            // Apply overlays
            foreach (var overlay in overlays)
                await overlay.ApplyOverlay();
        }
    }
}