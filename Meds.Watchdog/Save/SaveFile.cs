using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace Meds.Watchdog.Save
{
    public enum SaveType
    {
        Archived,
        Automatic,
    }
    
    public sealed class SaveFile : IEquatable<SaveFile>
    {
        public const string SessionBackupNameFormat = "yyyy-MM-dd HHmmss";
        private static Regex SessionBackupDateRegex = new Regex("^[0-9]{4}-[0-9]{2}-[0-9]{2} [0-9]{6}");

        public readonly string SavePath;
        public readonly SaveType Type;
        public readonly ILogger Log;
        public readonly DateTime TimeUtc;
        public readonly bool IsZip;
        public string SaveName => Path.GetFileName(SavePath);

        private SaveFile(ILogger logger, DateTime lastTimeUtc, string savePath, bool isZip, SaveType type)
        {
            Log = logger;
            TimeUtc = lastTimeUtc;
            SavePath = savePath;
            IsZip = isZip;
            Type = type;
        }

        public long SizeInBytes
        {
            get
            {
                try
                {
                    if (IsZip)
                        return new FileInfo(SavePath).Length;
                    var size = 0L;
                    foreach (var file in Directory.GetFiles(SavePath))
                    {
                        var info = new FileInfo(file);
                        if ((info.Attributes & FileAttributes.Directory) == 0)
                            size += info.Length;
                    }

                    return size;
                }
                catch
                {
                    return 0;
                }
            }
        }

        public static bool TryOpen(ILogger logger, string savePath, SaveType type, out SaveFile saveFile)
        {
            saveFile = null;
            try
            {
                var info = new FileInfo(savePath);
                if (!info.Exists)
                    return false;
                var timeUtc = info.LastWriteTimeUtc;
                var timeMatch = SessionBackupDateRegex.Match(Path.GetFileName(savePath));
                if (timeMatch.Success && DateTime.TryParseExact(timeMatch.Value, SessionBackupNameFormat, CultureInfo.InvariantCulture, DateTimeStyles.None,
                        out var matchedLocal))
                    timeUtc = matchedLocal.ToUniversalTime();
                if ((info.Attributes & FileAttributes.Directory) != 0)
                {
                    saveFile = new SaveFile(logger, timeUtc, savePath, false, type);
                    return true;
                }

                if (File.Exists(savePath) && savePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    saveFile = new SaveFile(logger, timeUtc, savePath, true, type);
                    return true;
                }
            }
            catch (Exception err)
            {
                logger.ZLogWarning(err, "Failed to open");
            }
            return false;
        }

        public SaveFileAccessor Open() => new SaveFileAccessor(this);

        public bool Equals(SaveFile other) => SavePath == other?.SavePath;

        public override bool Equals(object obj) => obj is SaveFile other && Equals(other);

        public override int GetHashCode() => SavePath.GetHashCode();
    }
}