using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Meds.Watchdog.Steam;
using Meds.Watchdog.Utils;
using NLog;
using FileInfo = Meds.Watchdog.Utils.FileInfo;

namespace Meds.Watchdog.Tasks
{
    public sealed class UpdateInstallTask
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        private readonly Program _program;
        private Configuration Configuration => _program.Configuration;

        public UpdateInstallTask(Program program)
        {
            _program = program;
        }

        private Task<OverlayData[]> LoadOverlays(string installPath)
        {
            if (Configuration.Overlays == null || Configuration.Overlays.Count == 0)
                return Task.FromResult(new OverlayData[0]);
            return Task.WhenAll(Configuration.Overlays.Select(async spec =>
            {
                var data = new OverlayData(installPath, spec);
                await data.Load();
                return data;
            }).ToArray());
        }

        public async Task Execute(SteamDownloader downloader)
        {
            var installPath = _program.InstallDirectory;
            var overlays = await LoadOverlays(installPath);

            // Clean deleted overlay files
            await Task.WhenAll(overlays.Select(overlay => Task.Run(overlay.CleanDeleted)));

            // Update game files
            Log.Info("Updating Medieval Engineers");
            var overlayFiles =
                new HashSet<string>(overlays.SelectMany(overlay =>
                    overlay.Remote.Files.Select(remoteFile => Path.Combine(overlay.Spec.Path, remoteFile.Path))));
            await downloader.InstallAppAsync(UpdateTask.MedievalDsAppId, UpdateTask.MedievalDsDepotId, "public", installPath, 4,
                path => UpdateTask.ShouldInstallAsset(path) && !overlayFiles.Contains(path), "medieval-ds");

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

            public OverlayData(string globalInstallPath, Configuration.Overlay spec)
            {
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
                    Remote = (LocalFileCache) LocalFileCache.Serializer.Deserialize(remoteStream);
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
                        _local = (LocalFileCache) LocalFileCache.Serializer.Deserialize(stream);
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
                        Log.Debug($"Deleting old overlay file {Spec.Path}/{file.Path}");
                        File.Delete(Path.Combine(OverlayInstallPath, file.Path));
                    }
            }

            public async Task ApplyOverlay()
            {
                Log.Info($"Updating overlay {Spec.Uri} in {Spec.Path}");
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
                        Log.Info($"Downloading overlay file {Spec.Path}/{remoteFile.Path}");
                        using (var copyTarget = File.Open(Path.Combine(OverlayInstallPath, remoteFile.Path), FileMode.Create, FileAccess.Write))
                            await remoteStream.CopyToAsync(copyTarget);
                        var fileInfo = new FileInfo {Path = remoteFile.Path};
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