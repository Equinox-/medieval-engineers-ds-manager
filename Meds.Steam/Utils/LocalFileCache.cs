using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Xml.Serialization;

namespace Meds.Watchdog.Utils
{
    [XmlRoot("FileCache")]
    public sealed class LocalFileCache
    {
        public static XmlSerializer Serializer = new XmlSerializer(typeof(LocalFileCache));

        [XmlIgnore]
        private readonly Dictionary<string, FileInfo> _files = new Dictionary<string, FileInfo>();

        public void Remove(string path) => _files.Remove(path);

        public void Add(FileInfo file) => _files[file.Path] = file;

        public bool TryGet(string path, out FileInfo info) => _files.TryGetValue(path, out info);

        [XmlIgnore]
        public ICollection<FileInfo> Files => _files.Values;
        
        [XmlElement("File")]
        public FileInfo[] FilesSerialized
        {
            get => _files.Values.ToArray();
            set
            {
                _files.Clear();
                foreach (var file in value)
                    _files[file.Path] = file;
            }
        }
    }

    public sealed class FileInfo
    {
        public static readonly ThreadLocal<SHA1> SHA1 = new ThreadLocal<SHA1>(System.Security.Cryptography.SHA1.Create);
        
        [XmlAttribute("Path")]
        public string Path { get; set; }

        [XmlAttribute("Hash")]
        public string HashString
        {
            get => Convert.ToBase64String(Hash);
            set => Hash = Convert.FromBase64String(value);
        }

        [XmlIgnore]
        public byte[] Hash { get; set; }

        [XmlAttribute("Size")]
        public long Size { get; set; }

        public void RepairData(string installPath)
        {
            var realPath = System.IO.Path.Combine(installPath, Path);
            if (!File.Exists(realPath))
            {
                Size = 0;
                Hash = Array.Empty<byte>();
                return;
            }
            var fileLength = new System.IO.FileInfo(realPath).Length;
            if (Size == fileLength)
                return;
            using (var stream = File.OpenRead(realPath))
            {
                Hash = SHA1.Value.ComputeHash(stream);
                Size = fileLength;
            }
        }
    }
}