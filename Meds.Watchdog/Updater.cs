using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Meds.Dist;
using Meds.Watchdog.Steam;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace Meds.Watchdog
{
    public sealed class Updater
    {
        private readonly Configuration _config;
        private readonly SteamDownloader _downloader;
        private readonly ILogger<Updater> _log;
        public const uint MedievalDsAppId = 367970;
        public const uint MedievalDsDepotId = 367971;
        public const uint MedievalGameAppId = 333950;


        public Updater(ILogger<Updater> log, Configuration config, SteamDownloader downloader)
        {
            _log = log;
            _config = config;
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
            if (_config.WrapperLayers == null || _config.WrapperLayers.Count == 0)
                return Task.FromResult(Array.Empty<OverlayData>());
            var log = new OverlayLogger(_log);
            return Task.WhenAll(_config.WrapperLayers.Select(async spec =>
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
            var installPath = _config.InstallDirectory;
            var overlays = await LoadOverlays(installPath);

            // Clean deleted overlay files
            await Task.WhenAll(overlays.Select(overlay => Task.Run(overlay.CleanDeleted, cancellationToken)));

            // Update game files
            _log.ZLogInformation("Updating Medieval Engineers");
            var overlayFiles =
                new HashSet<string>(overlays.SelectMany(overlay =>
                    overlay.Remote.Files.Select(remoteFile => Path.Combine(overlay.Spec.Path, remoteFile.Path))));
            await _downloader.InstallAppAsync(MedievalDsAppId, MedievalDsDepotId, _config.Steam.Branch, installPath, 4,
                path => !overlayFiles.Contains(path), "medieval-ds");

            // Apply overlays
            foreach (var overlay in overlays)
                await overlay.ApplyOverlay();
        }
    }
}