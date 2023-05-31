using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Meds.Dist;
using Meds.Watchdog.Utils;
using ProtoBuf;
using SteamKit2;
using SteamKit2.CDN;

namespace Meds.Watchdog.Steam
{
    public class InstallJob
    {
        private readonly Dictionary<string, FileParts> _fileParts = new Dictionary<string, FileParts>();
        private readonly ConcurrentStack<ChunkWorkItem> _neededChunks = new ConcurrentStack<ChunkWorkItem>();
        private long _finishedChunks;
        private long _totalChunks;
        private readonly List<string> _filesToDelete = new List<string>();
        private uint _appId;
        private uint _depotId;
        private string _basePath;
        private DistFileCache _cache;
        private SteamDownloader _downloader;

        public float ProgressRatio => Interlocked.Read(ref _finishedChunks) / (float) _totalChunks;

        public async Task Execute(SteamDownloader downloader, int workerCount = 8, Action<string> stateCallback = null)
        {
            _totalChunks = _neededChunks.Count;

            // Stage 1: Remove files
            foreach (var file in _filesToDelete)
            {
                File.Delete(Path.Combine(_basePath, file));
                _cache.Remove(file);
            }

            // Stage 2: Get new files
            _downloader = downloader;

            var workers = new Task[workerCount];
            for (var i = 0; i < workerCount; i++)
            {
                workers[i] = StartWorkerAsync();
            }

            await Task.WhenAll(workers);

            // Stage 3: Update local cache
            foreach (var newFile in _fileParts.Values)
            {
                var (relPath, hash, totalSize, _) = newFile.GetCacheDetails();
                _cache.Add(new DistFileInfo
                {
                    Path = relPath,
                    Hash = hash,
                    Size = (long) totalSize
                });
            }
        }

        private async Task StartWorkerAsync()
        {
            var depotKey = await _downloader.GetDepotKeyAsync(_appId, _depotId).ConfigureAwait(false);

            while (_neededChunks.Count > 0)
            {
                if (!_neededChunks.TryPop(out var workItem))
                    continue;

                var server = await _downloader.CdnPool.TakeServer();
                var client = _downloader.CdnPool.TakeClient();

                try
                {
                    // Don't need AuthenticateDepot because we're managing auth keys ourselves.
                    var chunk = await client.DownloadDepotChunkAsync(_depotId, workItem.ChunkData, server, depotKey).ConfigureAwait(false);

                    if (depotKey != null || CryptoHelper.AdlerHash(chunk.Data).SequenceEqual(chunk.ChunkInfo.Checksum))
                    {
                        await _fileParts[workItem.FileName].SubmitAsync(chunk).ConfigureAwait(false);
                        Interlocked.Increment(ref _finishedChunks);
                    }
                    else
                    {
                        _neededChunks.Push(workItem);
                    }
                    _downloader.CdnPool.ReturnServer(server);
                }
                catch
                {
                    _neededChunks.Push(workItem);
                }

                _downloader.CdnPool.ReturnClient(client);
            }
        }

        public static InstallJob Upgrade(uint appId, uint depotId, string installPath, DistFileCache localFiles, DepotManifest remoteFiles,
            Predicate<string> installFilter)
        {
            var job = new InstallJob
            {
                _cache = localFiles,
                _basePath = installPath,
                _appId = appId,
                _depotId = depotId
            };

            var remoteFileDict = remoteFiles.Files
                .Where(x => (x.Flags & EDepotFileFlag.Directory) == 0)
                .Where(x => installFilter(x.FileName))
                .ToDictionary(x => x.FileName);

            // Find difference between local files and remote files.
            foreach (var localFile in localFiles.Files)
            {
                if (remoteFileDict.TryGetValue(localFile.Path, out var remoteFile))
                {
                    // Don't download unchanged files.
                    if (localFile.Hash.SequenceEqual(remoteFile.FileHash))
                        remoteFileDict.Remove(localFile.Path);
                }
                else
                {
                    // Delete files that are present locally but not in the new manifest.
                    job._filesToDelete.Add(localFile.Path);
                }
            }

            // Populate needed chunks
            foreach (var file in remoteFileDict.Values)
            {
                job._fileParts.Add(file.FileName, new FileParts(file, installPath));
                foreach (var chunk in file.Chunks)
                {
                    job._neededChunks.Push(new ChunkWorkItem
                    {
                        ChunkData = chunk,
                        FileName = file.FileName
                    });
                }
            }

            return job;
        }

        private class FileParts
        {
            private readonly System.IO.FileInfo _destPath;
            private readonly DepotManifest.FileData _fileData;
            private ConcurrentBag<DepotChunk> _completeChunks;
            private DateTime _completionTime;

            private bool _started;
            private Stopwatch _sw = new Stopwatch();

            public bool IsComplete { get; private set; }

            public (string relPath, byte[] hash, ulong totalSize, DateTime completionTime) GetCacheDetails()
            {
                if (!IsComplete)
                    throw new InvalidOperationException("File is not complete!");

                return (_fileData.FileName, _fileData.FileHash, _fileData.TotalSize, _completionTime);
            }

            public FileParts(DepotManifest.FileData fileData, string basePath)
            {
                _fileData = fileData;
                _destPath = new System.IO.FileInfo(Path.Combine(basePath, fileData.FileName));
                _completeChunks = new ConcurrentBag<DepotChunk>();

                IsComplete = false;
            }

            public async Task SubmitAsync(DepotChunk chunk)
            {
                if (IsComplete)
                    throw new InvalidOperationException("The file is already complete.");

                if (!_started)
                {
                    _sw.Start();
                    _started = true;
                }

                _completeChunks.Add(chunk);
                //Console.WriteLine($"{chunk.ChunkInfo.Offset} {_destPath.FullName}");

                if (_completeChunks.Count == _fileData.Chunks.Count)
                {
                    IsComplete = true;
                    await Save();
                }
            }

            /// <summary>
            /// Creates a physical file and writes the chunks to disk.
            /// </summary>
            /// <exception cref="InvalidOperationException">The file is not ready to be saved.</exception>
            private async Task Save()
            {
                Directory.CreateDirectory(_destPath.DirectoryName);

                using (var fs = File.Create(_destPath.FullName, (int) _fileData.TotalSize))
                {
                    foreach (var chunk in _completeChunks.OrderBy(x => x.ChunkInfo.Offset))
                    {
                        await fs.WriteAsync(chunk.Data, 0, chunk.Data.Length);
                    }

                    _completeChunks = null;
                }

                _sw.Stop();
                //Console.WriteLine($"{_sw.Elapsed.TotalSeconds.ToString().PadRight(20)} {_fileData.TotalSize.ToString().PadRight(20)} {_fileData.FileName}");
                _completionTime = DateTime.Now;
            }
        }

        private struct ChunkWorkItem
        {
            public string FileName;
            public DepotManifest.ChunkData ChunkData;
        }
    }
}