using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Serialization;

namespace Meds.Dist
{
    public class OverlaySpec
    {
        [XmlAttribute("Uri")]
        public string Uri;

        [XmlAttribute("Path")]
        public string Path = "";
    }

    public interface IOverlayLogger
    {
        void Debug(string msg);
        void Info(string msg);
    }

    public sealed class OverlayData
    {
        public readonly string OverlayInstallPath;
        public readonly OverlaySpec Spec;
        private readonly string _localManifestFile;
        public DistFileCache Remote { get; private set; }
        private DistFileCache _local;
        private readonly IOverlayLogger _log;

        public OverlayData(IOverlayLogger log, string globalInstallPath, OverlaySpec spec)
        {
            _log = log;
            OverlayInstallPath = Path.Combine(globalInstallPath, spec.Path);
            Spec = spec;
            _localManifestFile = LocalManifestPath(globalInstallPath, spec);
            Remote = new DistFileCache();
            _local = new DistFileCache();
        }

        private static string LocalManifestPath(string installPath, OverlaySpec spec)
        {
            var localCacheName = spec.Uri;
            if (localCacheName.Length > 32)
                localCacheName = Convert.ToBase64String(SHA1.Create().ComputeHash(Encoding.UTF8.GetBytes(localCacheName))) + "_" +
                                 localCacheName.Substring(localCacheName.Length - 32);
            foreach (var invalid in Path.GetInvalidFileNameChars())
                localCacheName = localCacheName.Replace(invalid, '_');
            return Path.Combine(installPath, ".cache", localCacheName + ".xml");
        }

        private async Task LoadRemote()
        {
            using (var response = await WebRequest.Create(Spec.Uri + "/manifest.xml").GetResponseAsync())
            using (var remoteStream = response.GetResponseStream())
            {
                if (remoteStream == null)
                    throw new NullReferenceException($"No response stream for overlay {Spec.Uri}");
                Remote = (DistFileCache)DistFileCache.Serializer.Deserialize(remoteStream);
            }
        }

        private void LoadLocal()
        {
            if (!File.Exists(_localManifestFile))
            {
                _local = new DistFileCache();
                return;
            }

            try
            {
                using (var stream = File.OpenRead(_localManifestFile))
                    _local = (DistFileCache)DistFileCache.Serializer.Deserialize(stream);
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
            var manifestDir = Path.GetDirectoryName(_localManifestFile);
            if (manifestDir != null)
                Directory.CreateDirectory(manifestDir);
            using var stream = File.Open(_localManifestFile, FileMode.Create, FileAccess.Write);
            DistFileCache.Serializer.Serialize(stream, _local);
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
                    _log.Debug($"Deleting old overlay file {Spec.Path}/{file.Path}");
                    File.Delete(Path.Combine(OverlayInstallPath, file.Path));
                }
        }

        public async Task ApplyOverlay()
        {
            _log.Info($"Updating overlay {Spec.Uri} in {Spec.Path}");
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
                    _log.Info($"Downloading overlay file {Spec.Uri}/{remoteFile.Path}");
                    using (var copyTarget = File.Open(Path.Combine(OverlayInstallPath, remoteFile.Path), FileMode.Create, FileAccess.Write))
                        await remoteStream.CopyToAsync(copyTarget);
                    var fileInfo = new DistFileInfo { Path = remoteFile.Path };
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