using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Meds.Watchdog.Save
{
    public class SaveFileTextSearchResult
    {
        public ulong ObjectId { get; }
        public IReadOnlyList<Match> Matches { get; }

        public SaveFileTextSearchResult(ulong obj, IReadOnlyList<Match> matches)
        {
            Matches = matches;
            ObjectId = obj;
        }
    }

    public static class SaveFileTextSearch
    {
        private static SaveFileAccessor.DelTryParseObject<SaveFileTextSearchResult> CreateSearcher(Regex regex)
        {
            return (string path, ulong id, Stream stream, out SaveFileTextSearchResult result) =>
            {
                using var reader = new StreamReader(stream);
                var matches = new List<Match>();
                while (true)
                {
                    var line = reader.ReadLine();
                    if (line == null)
                        break;
                    var match = regex.Match(line);
                    if (match.Success)
                        matches.Add(match);
                }

                if (matches.Count == 0)
                {
                    result = null;
                    return false;
                }

                result = new SaveFileTextSearchResult(id, matches);
                return true;
            };
        }

        public static IEnumerable<SaveFileTextSearchResult> Entities(SaveFileAccessor save, Regex regex, DelReportProgress progressReporter = null)
        {
            return save.Entities(CreateSearcher(regex), progressReporter);
        }

        public static IEnumerable<SaveFileTextSearchResult> Groups(SaveFileAccessor save, Regex regex, DelReportProgress progressReporter = null)
        {
            return save.Groups(CreateSearcher(regex), progressReporter);
        }

        public static IEnumerable<SaveFileTextSearchResult> Players(SaveFileAccessor save, Regex regex, DelReportProgress progressReporter = null)
        {
            return save.Players(CreateSearcher(regex), progressReporter);
        }
    }
}