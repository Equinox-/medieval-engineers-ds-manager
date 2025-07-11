using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DSharpPlus.SlashCommands;
using Meds.Watchdog.Save;

// ReSharper disable HeapView.ClosureAllocation
// ReSharper disable HeapView.ObjectAllocation
// ReSharper disable HeapView.BoxingAllocation

namespace Meds.Watchdog.Discord
{
    // Not using command groups for discrete permissions.
    public class DiscordCmdSaveSearch : DiscordCmdBase
    {
        private readonly SaveFiles _saves;
        private readonly DataStore _dataStore;

        public DiscordCmdSaveSearch(SaveFiles saves,
            DataStore dataStore, DiscordService discord) : base(discord)
        {
            _saves = saves;
            _dataStore = dataStore;
        }

        public enum SearchObjectType
        {
            [ChoiceName("Entity")]
            Entity,

            [ChoiceName("Group")]
            Group,

            [ChoiceName("Player")]
            Player,
        }

        [SlashCommand("save-search-text", "Searches all objects in a save using a regular expression")]
        [SlashCommandPermissions(DiscordService.CommandPermission)]
        public async Task SaveSearchText(
            InteractionContext context,
            [Option("save", "Save file name from /save list")] [Autocomplete(typeof(AllSaveFilesAutoCompleter))]
            string saveName,
            [Option("target", "Type of object to bisect")]
            SearchObjectType target,
            [Option("regex", "Regular expression to search for")]
            string regex,
            [Option("ignoreCase", "Ignore case when matching lines")]
            bool ignoreCase = true,
            [Option("area", "Area to search, defaults to all areas")]
            string areaText = null)
        {
            Regex compiledRegex;
            try
            {
                compiledRegex = new Regex(regex, RegexOptions.Compiled | RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : 0));
            }
            catch (Exception err)
            {
                await context.CreateResponseAsync($"Failed to compile regular expression: `{err.Message}`");
                return;
            }

            await context.CreateResponseAsync($"Loading save `{saveName}`...");
            if (!_saves.TryOpenSave(saveName, out var saveFile))
            {
                await context.EditResponseAsync($"Failed to load save `{saveFile}`");
                return;
            }

            PlanetAreas areaFilter = default;
            var areaFilterValid = false;
            if (areaText != null)
            {
                using (_dataStore.Read(out var data)) areaFilterValid = data.Planet.TryParseArea(areaText, out areaFilter);
                if (!areaFilterValid)
                {
                    await context.EditResponseAsync($"Failed to parse area reference `{areaText}`");
                    return;
                }
            }

            using var save = saveFile.Open();

            var progress = new ProgressReporter(context);
            var results = target switch
            {
                SearchObjectType.Entity => SaveFileTextSearch.Entities(save, compiledRegex, progress.Reporter),
                SearchObjectType.Group => SaveFileTextSearch.Groups(save, compiledRegex, progress.Reporter),
                SearchObjectType.Player => SaveFileTextSearch.Players(save, compiledRegex, progress.Reporter),
                _ => throw new ArgumentOutOfRangeException(nameof(target), target, null)
            };

            if (areaFilterValid)
            {
                // ReSharper disable AccessToDisposedClosure
                Func<GridDatabaseConfig, ulong, Vector3?> getResultPivot = target switch
                {
                    SearchObjectType.Entity => (gdb, id) => save.TryGetEntity(id, out var entity) ? (Vector3?)entity.CenterOrPivot(gdb) : null,
                    SearchObjectType.Group => (gdb, id) => save.TryGetGroup(id, out var group) ? group.ChunkData?.WorldBounds(gdb).Center : null,
                    SearchObjectType.Player => (_, id) => save.TryGetPlayer(id, out var player) ? player.EntityAccessor?.PositionOptional?.Position : null,
                    _ => throw new ArgumentOutOfRangeException(nameof(target), target, null)
                };
                // ReSharper restore AccessToDisposedClosure
                results = results.Where(result =>
                {
                    using var token = _dataStore.Read(out var data);
                    var pivot = getResultPivot(data.GridDatabase, result.ObjectId);
                    if (pivot == null) return false;
                    var uv = data.Planet.GetAreaCoords(pivot.Value, out var face);
                    return areaFilter.Face == face && areaFilter.MinX <= uv.X && uv.X <= areaFilter.MaxX && areaFilter.MinY <= uv.Y && uv.Y <= areaFilter.MaxY;
                });
            }

            var aggValue = TryFindGroup("aggValue");
            var aggTerm = TryFindGroup("aggTerm");
            if (aggValue != null || aggTerm != null)
                await WriteAggregationResults(aggValue, aggTerm);
            else
                await WriteSearchResults(save);

            return;

            int? TryFindGroup(string group)
            {
                var groupNames = compiledRegex.GetGroupNames();
                for (var i = 0; i < groupNames.Length; i++)
                    if (group.Equals(groupNames[i], StringComparison.OrdinalIgnoreCase))
                        return i;
                return null;
            }

            async Task WriteAggregationResults(int? valueCapture, int? termCapture)
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var groups = new Dictionary<string, AggregationGroup>(StringComparer.OrdinalIgnoreCase);
                foreach (var result in results)
                {
                    seen.Clear();
                    foreach (var match in result.Matches)
                    {
                        if (!valueCapture.HasValue || !float.TryParse(match.Groups[valueCapture.Value].Value, out var value))
                            value = float.NaN;
                        var groupName = termCapture.HasValue ? match.Groups[termCapture.Value].Value : null;

                        if (float.IsNaN(value) && groupName == null) continue;

                        groupName ??= "null";
                        if (!groups.TryGetValue(groupName, out var group))
                            groups.Add(groupName, group = new AggregationGroup());

                        if (seen.Add(groupName)) group.Objects++;
                        group.Matches++;
                        if (!float.IsNaN(value)) group.Sum += value;
                    }
                }

                await progress.Finish();
                if (groups.Count == 0)
                {
                    await context.EditResponseAsync("No results");
                    return;
                }

                var formatted = new DiscordUtils.TableFormatter(0);
                formatted.AddRow("Term", "Objects", "Matches", "Sum");
                foreach (var group in groups.OrderByDescending(x => x.Value))
                    formatted.AddRow(group.Key,
                        group.Value.Objects.ToString(),
                        group.Value.Matches.ToString(),
                        group.Value.Sum.ToString(CultureInfo.InvariantCulture));
                await context.RespondLongText(formatted.Lines());
            }

            async Task WriteSearchResults(SaveFileAccessor saveCaptured)
            {
                CreateSearchObjectFormatter(saveCaptured, target, out var objectHeaders, out var objectFormatter);
                var groupNames = compiledRegex.GetGroupNames();
                string[] matchHeaders;
                if (groupNames.Length == 1)
                    matchHeaders = new[] { "Match" };
                else
                {
                    matchHeaders = new string[groupNames.Length - 1];
                    for (var i = 1; i < groupNames.Length; i++)
                    {
                        var name = groupNames[i];
                        matchHeaders[i - 1] = name == i.ToString() ? "Group " + name : name;
                    }
                }

                var formatted = FormatSearchResults(
                    results.Select(match => (match.ObjectId, (IEnumerable<Match>)match.Matches)),
                    objectHeaders, objectFormatter,
                    matchHeaders, MatchFormatter);
                await progress.Finish();
                if (formatted.RowCount > 1)
                    await context.RespondLongText(formatted.Lines());
                else
                    await context.EditResponseAsync("No results");
                return;

                bool MatchFormatter(Match match, string[] row)
                {
                    if (groupNames.Length == 1)
                    {
                        row[0] = match.Value.Trim();
                        return true;
                    }

                    for (var i = 1; i < groupNames.Length; i++)
                    {
                        var group = match.Groups[i];
                        row[i - 1] = group.Success ? group.Value : "";
                    }

                    return true;
                }
            }
        }

        private class AggregationGroup : IComparable<AggregationGroup>
        {
            public int Objects;
            public int Matches;
            public float Sum;

            public int CompareTo(AggregationGroup other)
            {
                if (ReferenceEquals(this, other)) return 0;
                if (other is null) return 1;
                var sumComparison = Sum.CompareTo(other.Sum);
                if (sumComparison != 0) return sumComparison;
                var matchesComparison = Matches.CompareTo(other.Matches);
                if (matchesComparison != 0) return matchesComparison;
                return Objects.CompareTo(other.Objects);
            }
        }

        [SlashCommand("save-search-geo", "Searches all objects in a save using an area")]
        [SlashCommandPermissions(DiscordService.CommandPermission)]
        public async Task SaveSearchGeo(
            InteractionContext context,
            [Option("save", "Save file name from /save list")] [Autocomplete(typeof(AllSaveFilesAutoCompleter))]
            string saveName,
            [Option("target", "Type of object to bisect")]
            SearchObjectType target,
            [Option("area", "Area to search")] string areaText)
        {
            await context.CreateResponseAsync($"Loading save `{saveName}`...");
            if (!_saves.TryOpenSave(saveName, out var saveFile))
            {
                await context.EditResponseAsync($"Failed to load save `{saveFile}`");
                return;
            }

            bool TryParseArea(out string desc, out SaveFileGeoSearch.LodSearch search)
            {
                // Can't await while holding the lock
                using var token = _dataStore.Read(out var data);
                if (!data.Planet.TryParseArea(areaText, out var parsedArea))
                {
                    desc = null;
                    search = default;
                    return false;
                }

                var queryBox = data.Planet.CalculateEnclosingBox(parsedArea);
                var index = _saves.Index(saveFile);
                search = new SaveFileGeoSearch.LodSearch(data.GridDatabase, index, queryBox);
                desc =
                    "```\n" +
                    $"Min Area: {data.Planet.GetAreaName(parsedArea.Face, parsedArea.MinX, parsedArea.MinY)}\n" +
                    $"Max Area: {data.Planet.GetAreaName(parsedArea.Face, parsedArea.MaxX - 1, parsedArea.MaxY - 1)}\n" +
                    $"Min Coord: {queryBox.Min}\n" +
                    $"Max Coord: {queryBox.Max}\n";
                for (var i = 0; i < search.LodCellCount.Length; i++)
                {
                    desc += $"Lod {i}: ({search.LodCellCount[i]} - {search.LodCellBox[i]}\n";
                }

                return true;
            }

            if (!TryParseArea(out var searchText, out var lodSearch))
            {
                await context.EditResponseAsync($"Failed to parse area reference `{areaText}`");
                return;
            }

            await context.EditResponseAsync(searchText + "```");

            using var save = saveFile.Open();
            CreateSearchObjectFormatter(save, target, out var objectHeaders, out var objectFormatter);

            int objectCount;
            IEnumerable<ulong> objectIds;

            switch (target)
            {
                // ReSharper disable AccessToDisposedClosure
                case SearchObjectType.Entity:
                    var entities = SaveFileGeoSearch.Entities(save, lodSearch);
                    objectCount = entities.Count;
                    objectIds = entities
                        .OrderByDescending(x => save.TryGetEntityFileInfo(x, out var info) ? info.Size : 0)
                        .Select(x => x.Value);
                    break;
                case SearchObjectType.Group:
                    var groups = SaveFileGeoSearch.Groups(save, lodSearch);
                    objectCount = groups.Count;
                    objectIds = groups
                        .OrderByDescending(x => save.TryGetGroupFileInfo(x, out var info) ? info.Size : 0)
                        .Select(x => x.Value);
                    break;
                case SearchObjectType.Player:
                    var players = SaveFileGeoSearch.Players(save, lodSearch);
                    objectCount = players.Count;
                    objectIds = players
                        .OrderByDescending(x => save.TryGetPlayerFileInfo(x, out var info) ? info.Size : 0)
                        .Select(x => x.Value);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(target), target, null);
                // ReSharper restore AccessToDisposedClosure
            }

            searchText += $"{target} Count: {objectCount}\n";
            await context.EditResponseAsync(searchText + "```");
            if (objectCount == 0)
                return;

            var temp = new[] { "" };
            var formatted = FormatSearchResults(
                objectIds.Select(id => (id, (IEnumerable<string>)temp)),
                objectHeaders, objectFormatter,
                Array.Empty<string>(), (match, row) => true);
            if (formatted.RowCount > 1)
                await context.RespondLongText(formatted.Lines());
        }

        private delegate bool DelTryFormatSearchResult<in T>(T obj, string[] row);

        private void CreateSearchObjectFormatter(
            SaveFileAccessor save,
            SearchObjectType target,
            out string[] headers, out DelTryFormatSearchResult<ulong> formatter)
        {
            switch (target)
            {
                case SearchObjectType.Entity:
                    headers = new[] { "Entity ID", "Type", "Location", "Blocks" };
                    formatter = (id, row) =>
                    {
                        EntityId entityId = id;
                        if (!save.TryGetEntity(entityId, out var entity))
                            return false;
                        row[0] = entityId.ToString();
                        row[1] = entity.Subtype;
                        using (_dataStore.Read(out var data))
                            row[2] = data.Planet.GetAreaName(entity.CenterOrPivot(data.GridDatabase));
                        row[3] = entity.BlockCount.ToString();
                        return true;
                    };
                    break;
                case SearchObjectType.Group:
                    headers = new[] { "Group ID", "Type", "Location", "Entities" };
                    formatter = (id, row) =>
                    {
                        GroupId groupId = id;
                        if (!save.TryGetGroup(groupId, out var group))
                            return false;
                        row[0] = groupId.ToString();
                        row[1] = group.Type;
                        using (_dataStore.Read(out var data))
                            row[2] = group.ChunkData.HasValue
                                ? data.Planet.GetAreaName(group.ChunkData.Value.WorldBounds(data.GridDatabase).Center)
                                : "Unknown";
                        row[3] = group.TopLevelEntities.Count.ToString();
                        return true;
                    };
                    break;
                case SearchObjectType.Player:
                    headers = new[] { "Steam ID", "Display Name", "Location" };
                    formatter = (id, row) =>
                    {
                        SteamId steamId = id;
                        if (!save.TryGetPlayer(steamId, out var player))
                            return false;
                        row[0] = steamId.ToString();
                        row[1] = player.DisplayName ?? "Unknown";
                        var pos = player.EntityAccessor.PositionOptional?.Position;
                        using (_dataStore.Read(out var data))
                            row[2] = pos.HasValue ? data.Planet.GetAreaName(pos.Value) : "Unknown";
                        return true;
                    };
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(target), target, null);
            }
        }

        private static DiscordUtils.TableFormatter FormatSearchResults<TObj, TResult>(
            IEnumerable<(TObj obj, IEnumerable<TResult> matches)> results,
            string[] objectHeaders,
            DelTryFormatSearchResult<TObj> objectFormatter,
            string[] matchHeaders,
            DelTryFormatSearchResult<TResult> matchFormatter,
            int resultLimit = 50)
        {
            string[] Concat(string[] a, string[] b)
            {
                var dest = new string[a.Length + b.Length];
                Array.Copy(a, 0, dest, 0, a.Length);
                Array.Copy(b, 0, dest, a.Length, b.Length);
                return dest;
            }

            var headers = Concat(objectHeaders, matchHeaders);
            var formatter = new DiscordUtils.TableFormatter(0);
            formatter.AddRow(headers);

            var objRow = new string[objectHeaders.Length];
            var matchRow = new string[matchHeaders.Length];
            foreach (var result in results)
            {
                if (!objectFormatter(result.obj, objRow))
                    continue;
                foreach (var match in result.matches)
                {
                    if (formatter.RowCount >= resultLimit)
                    {
                        formatter.AddRow("...and more");
                        break;
                    }

                    if (!matchFormatter(match, matchRow))
                        continue;
                    formatter.AddRow(Concat(objRow, matchRow));
                }

                if (formatter.RowCount >= resultLimit)
                    break;
            }

            return formatter;
        }
    }
}