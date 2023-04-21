using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Caching;
using System.Threading;
using Meds.Watchdog.Utils;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace Meds.Watchdog.Save
{
    public class SaveFiles
    {
        private readonly ILoggerFactory _loggerFactory;
        private readonly InstallConfiguration _installConfig;
        private readonly IReadOnlyDictionary<SaveType, string> _saveRoots;
        private readonly ILogger<SaveFiles> _log;

        private readonly ObjectCache _indexCache = new MemoryCache("saveFileIndex", new NameValueCollection
        {
            ["cacheMemoryLimitMegabytes"] = "32"
        });

        public SaveFiles(ILoggerFactory loggerFactory,
            ILogger<SaveFiles> log,
            InstallConfiguration installConfig)
        {
            _log = log;
            _loggerFactory = loggerFactory;
            _installConfig = installConfig;
            _saveRoots = new Dictionary<SaveType, string>
            {
                [SaveType.Archived] = _installConfig.ArchivedBackupsDirectory,
                [SaveType.Automatic] = Path.Combine(_installConfig.RuntimeDirectory, "world", "Backup")
            };
        }

        public string GetArchivePath(string name, DateTime? now = null)
        {
            const string stripPrefix = ".zip";
            if (name.EndsWith(stripPrefix, StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - stripPrefix.Length);
            return Path.Combine(_installConfig.ArchivedBackupsDirectory,
                $"{(now ?? DateTime.Now).ToString(SaveFile.SessionBackupNameFormat, CultureInfo.InvariantCulture)}_{PathUtils.CleanFileName(name)}.zip");
        }

        private bool TryOpenSaveAbsolute(string path, SaveType type, out SaveFile save) => SaveFile.TryOpen(
            _loggerFactory.CreateLogger($"{nameof(SaveFiles)}.{Path.GetFileName(path)}"), path, type, out save);

        public bool TryOpenSave(string name, out SaveFile save)
        {
            save = null;
            if (name.StartsWith("/") || name.StartsWith("\\") || name.Contains(".."))
                return false;
            var nameIsZip = name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
            foreach (var root in _saveRoots)
            {
                var path = Path.GetFullPath(Path.Combine(root.Value, name));
                if (!path.StartsWith(Path.GetFullPath(root.Value), StringComparison.OrdinalIgnoreCase))
                    return false;
                if (!nameIsZip && TryOpenSaveAbsolute(path + ".zip", root.Key, out save))
                    return true;
                if (TryOpenSaveAbsolute(path, root.Key, out save))
                    return true;
            }

            return false;
        }

        private IEnumerable<SaveFile> SavesFor(SaveType type)
        {
            var root = _saveRoots[type];
            if (!Directory.Exists(root))
                yield break;
            foreach (var path in Directory.GetFiles(root, "*.zip"))
                if (TryOpenSaveAbsolute(path, type, out var save))
                    yield return save;
        }

        public IEnumerable<SaveFile> ArchivedSaves => SavesFor(SaveType.Archived);

        public IEnumerable<SaveFile> AutomaticSaves => SavesFor(SaveType.Automatic);

        public IEnumerable<SaveFile> AllSaves => AutomaticSaves.Concat(ArchivedSaves);

        public SaveFileIndex Index(SaveFile file)
        {
            if (_indexCache.Get(file.SavePath) is SaveFileIndex index)
                return index;
            var start = Stopwatch.GetTimestamp();
            index = new SaveFileIndex(file);
            _indexCache.Add(file.SavePath, index, new CacheItemPolicy());
            _log.ZLogInformation("Indexed save {0} in {1}ms",
                file.SavePath,
                (Stopwatch.GetTimestamp() - start) * 1000 / Stopwatch.Frequency);
            return index;
        }

        private readonly object _autoCompleteLock = new object();
        private long _autoCompleterExpires;
        private volatile AutoCompleteTree<SaveFile> _autoCompleterAll;
        private volatile AutoCompleteTree<SaveFile> _autoCompleterAutomatic;

        private bool AutoCompleteExpired => _autoCompleterAll == null || Volatile.Read(ref _autoCompleterExpires) <= Stopwatch.GetTimestamp();

        private void EnsureAutoCompleter()
        {
            if (!AutoCompleteExpired)
                return;
            lock (_autoCompleteLock)
            {
                if (!AutoCompleteExpired) return;
                _autoCompleterAll = new AutoCompleteTree<SaveFile>(AllSaves.Select(x => (Path.GetFileName(x.SavePath), x)));
                _autoCompleterAutomatic = new AutoCompleteTree<SaveFile>(AutomaticSaves.Select(x => (Path.GetFileName(x.SavePath), x)));
                // 1 minute cache
                _autoCompleterExpires = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 60;
            }
        }

        public IEnumerable<AutoCompleteTree<SaveFile>.Result> AutoCompleteSave(string prompt, int? limit = null)
        {
            EnsureAutoCompleter();
            return _autoCompleterAll.Apply(prompt, limit);
        }

        public IEnumerable<AutoCompleteTree<SaveFile>.Result> AutoCompleteAutomaticSave(string prompt, int? limit = null)
        {
            EnsureAutoCompleter();
            return _autoCompleterAutomatic.Apply(prompt, limit);
        }
    }
}