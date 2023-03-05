using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Meds.Shared;
using Meds.Watchdog.Steam;
using Meds.Watchdog.Utils;
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
            if (_config.Overlays == null || _config.Overlays.Count == 0)
                return Task.FromResult(Array.Empty<OverlayData>());
            return Task.WhenAll(_config.Overlays.Select(async spec =>
            {
                var data = new OverlayData(_log, installPath, spec);
                await data.Load();
                return data;
            }).ToArray());
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

        private sealed class OverlayData
        {
            public readonly string OverlayInstallPath;
            public readonly Configuration.Overlay Spec;
            private readonly string _localManifestFile;
            public LocalFileCache Remote { get; private set; }
            private LocalFileCache _local;
            private readonly ILogger<Updater> _log;

            public OverlayData(ILogger<Updater> log, string globalInstallPath, Configuration.Overlay spec)
            {
                _log = log;
                OverlayInstallPath = Path.Combine(globalInstallPath, spec.Path);
                Spec = spec;
                _localManifestFile = LocalManifestPath(globalInstallPath, spec);
                Remote = new LocalFileCache();
                _local = new LocalFileCache();
            }

            private static string LocalManifestPath(string installPath, Configuration.Overlay spec)
            {
                var localCacheName = spec.Uri;
                if (localCacheName.Length > 32)
                    localCacheName = Convert.ToBase64String(SHA1.Create().ComputeHash(Encoding.UTF8.GetBytes(localCacheName))) + "_" +
                                     localCacheName.Substring(localCacheName.Length - 32);
                foreach (var invalid in Path.GetInvalidFileNameChars())
                    localCacheName = localCacheName.Replace(invalid, '_');
                return Path.Combine(installPath, SteamDownloader.CacheDir, localCacheName + ".xml");
            }

            private async Task LoadRemote()
            {
                using (var response = await WebRequest.Create(Spec.Uri + "/manifest.xml").GetResponseAsync())
                using (var remoteStream = response.GetResponseStream())
                {
                    if (remoteStream == null)
                        throw new NullReferenceException($"No response stream for overlay {Spec.Uri}");
                    Remote = (LocalFileCache)LocalFileCache.Serializer.Deserialize(remoteStream);
                }
            }

            private void LoadLocal()
            {
                if (!File.Exists(_localManifestFile))
                {
                    _local = new LocalFileCache();
                    return;
                }

                try
                {
                    using (var stream = File.OpenRead(_localManifestFile))
                        _local = (LocalFileCache)LocalFileCache.Serializer.Deserialize(stream);
                    foreach (var entry in _local.Files)
                        entry.RepairData(OverlayInstallPath);
                }
                catch
                {
                    // ignored
                }
            }

            private void SaveLocal()
            {
                using (var stream = File.Open(_localManifestFile, FileMode.Create, FileAccess.Write))
                    LocalFileCache.Serializer.Serialize(stream, _local);
            }

            public Task Load()
            {
                return Task.WhenAll(LoadRemote(), Task.Run(LoadLocal));
            }

            public void CleanDeleted()
            {
                foreach (var file in _local.Files)
                    if (!Remote.TryGet(file.Path, out _))
                    {
                        _log.ZLogDebug("Deleting old overlay file {0}/{1}", Spec.Path, file.Path);
                        File.Delete(Path.Combine(OverlayInstallPath, file.Path));
                    }
            }

            public async Task ApplyOverlay()
            {
                _log.ZLogInformation("Updating overlay {0} in {1}", Spec.Uri, Spec.Path);
                await Task.WhenAll(Remote.Files.Select(async remoteFile =>
                {
                    lock (_local)
                    {
                        if (_local.TryGet(remoteFile.Path, out var localFile) && localFile.Size == remoteFile.Size &&
                            localFile.Hash.SequenceEqual(remoteFile.Hash))
                            return;
                    }

                    var remoteFileUri = Spec.Uri + "/" + remoteFile.Path.Replace('\\', '/');
                    using (var response = await WebRequest.Create(remoteFileUri).GetResponseAsync())
                    using (var remoteStream = response.GetResponseStream())
                    {
                        if (remoteStream == null)
                            throw new NullReferenceException($"No response stream for overlay file {remoteFileUri}");
                        _log.ZLogInformation("Downloading overlay file {0}/{1}", Spec.Uri, remoteFile.Path);
                        using (var copyTarget = File.Open(Path.Combine(OverlayInstallPath, remoteFile.Path), FileMode.Create, FileAccess.Write))
                            await remoteStream.CopyToAsync(copyTarget);
                        var fileInfo = new Utils.FileInfo { Path = remoteFile.Path };
                        fileInfo.RepairData(OverlayInstallPath);
                        lock (_local)
                        {
                            _local.Add(fileInfo);
                        }
                    }
                }));
                SaveLocal();
            }
        }
    }
}