using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Xml;
using Microsoft.Extensions.Logging;
using ZLogger;

// ReSharper disable HeapView.BoxingAllocation
// ReSharper disable HeapView.ClosureAllocation
// ReSharper disable HeapView.ObjectAllocation
// ReSharper disable HeapView.DelegateAllocation

namespace Meds.Watchdog.Save
{
    public delegate void DelReportProgress(int total, int successful, int failed);

    public sealed class SaveFileAccessor : IDisposable
    {
        public readonly SaveFile Save;
        public string SavePath => Save.SavePath;
        private readonly ThreadLocal<ZipArchive> _zip;
        private ILogger Log => Save.Log;

        internal SaveFileAccessor(SaveFile save)
        {
            Save = save;
            if (Save.IsZip)
                _zip = new ThreadLocal<ZipArchive>(() => new ZipArchive(
                    new FileStream(save.SavePath, FileMode.Open, FileAccess.Read, FileShare.Read),
                    ZipArchiveMode.Read), true);
        }

        private bool TryGetFileInfo(string path, out SaveEntryInfo info)
        {
            info = default;
            if (_zip != null)
            {
                var entry = _zip.Value.GetEntry(path);
                if (entry == null)
                    return false;
                info = new SaveEntryInfo(this, path, entry.Length);
                return true;
            }

            var fileInfo = new FileInfo(Path.Combine(SavePath, path));
            if (!fileInfo.Exists)
                return false;
            info = new SaveEntryInfo(this, path, fileInfo.Length);
            return true;
        }

        internal bool TryGetStream(string path, out Stream stream)
        {
            stream = null;
            if (_zip != null)
            {
                var entry = _zip.Value.GetEntry(path);
                if (entry != null)
                {
                    stream = entry.Open();
                    return true;
                }

                Log.ZLogInformation("File {0} missing from save", path);
                return false;
            }

            var fullPath = Path.Combine(SavePath, path);
            if (!File.Exists(fullPath))
            {
                Log.ZLogInformation("File {0} missing from save", path);
                return false;
            }

            stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return true;
        }

        private bool TryGetObject<T>(string path, ulong id, DelTryParseObject<T> parser, out T result)
        {
            result = default;
            Stream stream = null;
            try
            {
                return TryGetStream(path, out stream) && parser(path, id, stream, out result);
            }
            catch (Exception err)
            {
                Log.ZLogWarning(err, "Failed to load {0} {1}", typeof(T).Name, path);
            }
            finally
            {
                stream?.Close();
            }

            return false;
        }

        private bool TryGetChunk(string path, out ChunkAccessor result)
        {
            result = default;
            Stream stream = null;
            try
            {
                if (!TryGetStream(path, out stream))
                    return false;
                var doc = new XmlDocument();
                doc.Load(stream);
                result = new ChunkAccessor(doc["MySerializedWorldChunk"]);
                return true;
            }
            catch (Exception err)
            {
                Log.ZLogWarning(err, "Failed to load chunk {0}", path);
            }
            finally
            {
                stream?.Close();
            }

            return false;
        }

        private const string EntityFolder = "Entities";
        private const string EntityExtension = ".entity";
        private const string GroupFolder = "Groups";
        private const string GroupExtension = ".group";
        private const string ChunksFolder = "WorldChunks";
        private const string ChunkExtension = ".chunk";

        private static string EntityPath(EntityId id) => Path.Combine(EntityFolder, $"{id.Value & 0xFF:X02}", $"{id.Value:X016}{EntityExtension}");
        private static string GroupPath(GroupId id) => Path.Combine(GroupFolder, $"{id.Value & 0xFF:X02}", $"{id.Value:X016}{GroupExtension}");

        private static string ChunkPath(ChunkId id) =>
            Path.Combine(ChunksFolder, $"{id.DatabaseHash() >> 24:X02}", $"{id.X}_{id.Y}_{id.Z}_{id.Lod}{ChunkExtension}");

        public bool TryGetEntityFileInfo(EntityId id, out SaveEntryInfo info) => TryGetFileInfo(EntityPath(id), out info);
        public bool TryGetGroupFileInfo(GroupId id, out SaveEntryInfo info) => TryGetFileInfo(GroupPath(id), out info);
        public bool TryGetChunkFileInfo(ChunkId id, out SaveEntryInfo info) => TryGetFileInfo(ChunkPath(id), out info);

        private readonly DelTryParseObject<EntityAccessor> _entityParser = (string path, ulong id, Stream stream, out EntityAccessor result) =>
        {
            var doc = new XmlDocument();
            doc.Load(stream);
            result = new EntityAccessor(doc["MySerializedEntity"]);
            return result.Entity != null;
        };

        private readonly DelTryParseObject<GroupAccessor> _groupParser = (string path, ulong id, Stream stream, out GroupAccessor result) =>
        {
            var doc = new XmlDocument();
            doc.Load(stream);
            result = new GroupAccessor(doc["MySerializedGroup"]);
            return result.Group != null;
        };

        public bool TryGetEntity(EntityId id, out EntityAccessor entity) =>
            TryGetObject(EntityPath(id), id.Value, _entityParser, out entity);

        public bool TryGetGroup(GroupId id, out GroupAccessor group) =>
            TryGetObject(GroupPath(id), id.Value, _groupParser, out group);

        public bool TryGetChunk(ChunkId id, out ChunkAccessor group) => TryGetChunk(ChunkPath(id), out group);

        private IEnumerable<string> PathsToLoad(string folder, string suffix)
        {
            if (_zip != null)
            {
                return _zip.Value.Entries
                    .Select(entry => entry.FullName)
                    .Where(name => name.StartsWith(folder, StringComparison.OrdinalIgnoreCase)
                                   && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                                   && (name[folder.Length] == '/' || name[folder.Length] == '\\'));
            }

            return Directory.GetFiles(Path.Combine(SavePath, folder), "*" + suffix, SearchOption.AllDirectories)
                .Select(fullPath =>
                {
                    if (!fullPath.StartsWith(SavePath, StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException("Does not start with save path");
                    return fullPath.Substring(SavePath.Length).TrimStart('/', '\\');
                });
        }

        private IEnumerable<(string path, ulong id)> ObjectsToLoad(string folder, string suffix)
        {
            var paths = PathsToLoad(folder, suffix);
            foreach (var path in paths)
            {
                var fileName = Path.GetFileName(path);
                if (!fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (ulong.TryParse(fileName.Substring(0, fileName.Length - suffix.Length), NumberStyles.HexNumber, null, out var id))
                    yield return (path, id);
            }
        }

        public delegate bool DelTryParseObject<T>(string path, ulong objectId, Stream stream, out T result);

        private IEnumerable<T> Objects<T>(string folder, string suffix, DelTryParseObject<T> parser, DelReportProgress progressReporter = null)
        {
            var total = 0;
            var successful = 0;
            var failed = 0;
            var reporting = 0;

            var objects = ObjectsToLoad(folder, suffix);
            // ReSharper disable once InvertIf
            if (progressReporter != null)
            {
                var list = objects.ToList();
                total = list.Count;
                objects = list;
            }

            return objects.AsParallel()
                .AsUnordered()
                .WithExecutionMode(ParallelExecutionMode.ForceParallelism)
                .WithDegreeOfParallelism(32)
                .SelectMany(item =>
                {
                    IEnumerable<T> results;
                    if (TryGetObject(item.path, item.id, parser, out var result))
                    {
                        if (progressReporter != null)
                            Interlocked.Increment(ref successful);
                        results = new[] { result };
                    }
                    else
                    {
                        if (progressReporter != null)
                            Interlocked.Increment(ref failed);
                        results = Enumerable.Empty<T>();
                    }

                    if (progressReporter != null && Interlocked.CompareExchange(ref reporting, 1, 0) == 0)
                    {
                        progressReporter(total, successful, failed);
                        Interlocked.Exchange(ref reporting, 0);
                    }

                    return results;
                })
                .WithMergeOptions(ParallelMergeOptions.FullyBuffered)
                .AsEnumerable();
        }

        public IEnumerable<T> Entities<T>(DelTryParseObject<T> parser, DelReportProgress progressReporter = null)
        {
            return Objects(EntityFolder, EntityExtension, parser, progressReporter);
        }

        public IEnumerable<EntityAccessor> Entities(DelReportProgress progressReporter = null)
        {
            return Entities(_entityParser, progressReporter);
        }

        public IEnumerable<T> Groups<T>(DelTryParseObject<T> parser, DelReportProgress progressReporter = null)
        {
            return Objects(GroupFolder, GroupExtension, parser, progressReporter);
        }

        public IEnumerable<GroupAccessor> Groups(DelReportProgress progressReporter = null)
        {
            return Groups(_groupParser, progressReporter);
        }

        public IEnumerable<ChunkAccessor> Chunks(DelReportProgress progressReporter = null)
        {
            var total = 0;
            var successful = 0;
            var failed = 0;
            var reporting = 0;

            var chunks = PathsToLoad("WorldChunks", ".chunk");
            // ReSharper disable once InvertIf
            if (progressReporter != null)
            {
                var list = chunks.ToList();
                total = list.Count;
                chunks = list;
            }

            return chunks.AsParallel()
                .AsUnordered()
                .WithExecutionMode(ParallelExecutionMode.ForceParallelism)
                .WithDegreeOfParallelism(32)
                .SelectMany(path =>
                {
                    IEnumerable<ChunkAccessor> results;
                    if (TryGetChunk(path, out var result))
                    {
                        if (progressReporter != null)
                            Interlocked.Increment(ref successful);
                        results = new[] { result };
                    }
                    else
                    {
                        if (progressReporter != null)
                            Interlocked.Increment(ref failed);
                        results = Enumerable.Empty<ChunkAccessor>();
                    }

                    if (progressReporter != null && Interlocked.CompareExchange(ref reporting, 1, 0) == 0)
                    {
                        progressReporter(total, successful, failed);
                        Interlocked.Exchange(ref reporting, 0);
                    }

                    return results;
                })
                .WithMergeOptions(ParallelMergeOptions.FullyBuffered)
                .AsEnumerable();
        }

        public bool TryGetConfig(out SaveConfigAccessor config)
        {
            config = default;
            Stream stream = null;
            try
            {
                if (!TryGetStream("Sandbox.sbc", out stream))
                    return false;
                var doc = new XmlDocument();
                doc.Load(stream);
                var checkpoint = doc["MyObjectBuilder_Checkpoint"];
                if (checkpoint == null)
                    return false;
                config = new SaveConfigAccessor(checkpoint);
                return true;
            }
            catch (Exception err)
            {
                Log.ZLogWarning(err, "Failed to load checkpoint");
            }
            finally
            {
                stream?.Close();
            }

            return false;
        }
        
        public IEnumerable<SaveEntryInfo> AllFiles()
        {
            if (_zip != null)
            {
                return _zip.Value.Entries
                    .Where(entry => !entry.FullName.EndsWith("/") && !entry.FullName.EndsWith("\\"))
                    .Select(entry => new SaveEntryInfo(this, entry.FullName, entry.Length));
            }

            return Directory.GetFiles(SavePath, "*", SearchOption.AllDirectories)
                .Where(fullPath => File.Exists(fullPath) && !Directory.Exists(fullPath))
                .Select(fullPath =>
                {
                    if (!fullPath.StartsWith(SavePath, StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException("Does not start with save path");
                    return fullPath.Substring(SavePath.Length).TrimStart('/', '\\');
                })
                .Where(relPath => !relPath.StartsWith(SaveFiles.BackupFolder, StringComparison.OrdinalIgnoreCase))
                .Select(relPath => new SaveEntryInfo(this, relPath, new FileInfo(Path.Combine(SavePath, relPath)).Length));
        }

        public void Dispose()
        {
            if (_zip == null) return;
            foreach (var zip in _zip.Values)
                zip.Dispose();
        }
    }

    public readonly struct SaveEntryInfo
    {
        private readonly SaveFileAccessor _owner;
        public readonly string RelativePath;
        public readonly long Size;

        internal SaveEntryInfo(SaveFileAccessor owner, string relativePath, long size)
        {
            _owner = owner;
            RelativePath = relativePath;
            Size = size;
        }

        public Stream Open()
        {
            if (_owner.TryGetStream(RelativePath, out var stream))
                return stream;
            throw new Exception("Failed to open save entry");
        }
    }
}