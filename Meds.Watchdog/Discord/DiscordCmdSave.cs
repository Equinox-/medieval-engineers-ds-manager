using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.SlashCommands;
using Meds.Shared;
using Meds.Shared.Data;
using Meds.Watchdog.Save;

// ReSharper disable HeapView.ClosureAllocation
// ReSharper disable HeapView.ObjectAllocation
// ReSharper disable HeapView.BoxingAllocation

namespace Meds.Watchdog.Discord
{
    // Not using command groups for discrete permissions.
    public class DiscordCmdSave : ApplicationCommandModule
    {
        private readonly HealthTracker _healthTracker;
        private readonly ISubscriber<SaveResponse> _saveResponse;
        private readonly IPublisher<SaveRequest> _saveRequest;
        private readonly SaveFiles _saves;
        private readonly DataStore _dataStore;

        public DiscordCmdSave(HealthTracker healthTracker, ISubscriber<SaveResponse> saveResponse, IPublisher<SaveRequest> saveRequest, SaveFiles saves,
            DataStore dataStore)
        {
            _healthTracker = healthTracker;
            _saveResponse = saveResponse;
            _saveRequest = saveRequest;
            _saves = saves;
            _dataStore = dataStore;
        }

        private async Task SaveInternal(InteractionContext context, string name)
        {
            if (!_healthTracker.Readiness.State)
            {
                await context.CreateResponseAsync("Cannot save when server is not running");
                return;
            }

            string backupPath = null;
            string backupName = null;
            if (!string.IsNullOrEmpty(name))
            {
                backupPath = _saves.GetArchivePath(name);
                backupName = Path.GetFileName(backupPath);
            }

            await context.CreateResponseAsync(backupName != null ? $"Saving and backing up to {backupName}..." : "Saving...");
            var start = DateTime.UtcNow;
            var result = await _saveResponse.AwaitResponse(
                response => response.Result,
                TimeSpan.FromMinutes(15),
                () =>
                {
                    using var t = _saveRequest.Publish();
                    t.Send(SaveRequest.CreateSaveRequest(t.Builder, Stopwatch.GetTimestamp(), t.Builder.CreateString(backupPath)));
                });
            var duration = DateTime.UtcNow - start;
            switch (result)
            {
                case SaveResult.Success:
                    if (backupName == null)
                    {
                        await context.EditResponseAsync($"Saved in {duration.FormatHumanDuration()}");
                        return;
                    }

                    var backupSize = new FileInfo(backupPath).Length;
                    await context.EditResponseAsync(
                        $"Saved and backed up to {backupName} ({DiscordUtils.FormatHumanBytes(backupSize)}) in {duration.FormatHumanDuration()}");
                    break;
                case SaveResult.Failed:
                    await context.EditResponseAsync("Failed to save");
                    break;
                case SaveResult.TimedOut:
                    await context.EditResponseAsync("Timed out when saving");
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        [SlashCommand("save-now", "Saves the server's world file")]
        [SlashCommandPermissions(Permissions.Administrator)]
        public Task SaveNow(InteractionContext context) => SaveInternal(context, null);

        [SlashCommand("save-backup", "Saves the server's world file, names, and archives it")]
        [SlashCommandPermissions(Permissions.Administrator)]
        public Task SaveWithBackup(InteractionContext context, [Option("name", "Name of the backup to take")] string name)
        {
            return SaveInternal(context, name);
        }

        [SlashCommand("save-archive", "Names and archives an existing save")]
        [SlashCommandPermissions(Permissions.Administrator)]
        public async Task ArchiveSave(InteractionContext context,
            [Option("save", "Save file name from /save list")] [Autocomplete(typeof(AutomaticSaveFilesAutoCompleter))]
            string save,
            [Option("name", "Name of the tag to create")]
            string name)
        {
            await context.CreateResponseAsync($"Loading save `{save}`...");
            if (!_saves.TryOpenSave(save, out var saveFile))
            {
                await context.EditResponseAsync($"Failed to load save `{saveFile}`");
                return;
            }

            var tagPath = _saves.GetArchivePath(name, saveFile.TimeUtc.ToLocalTime());
            var tagName = Path.GetFileName(tagPath);
            if (File.Exists(tagPath))
            {
                await context.EditResponseAsync($"Archived save `{tagName}` already exists");
                return;
            }

            var tagDir = Path.GetDirectoryName(tagPath);
            if (tagDir != null && !Directory.Exists(tagDir))
                Directory.CreateDirectory(tagDir);
            File.Copy(saveFile.SavePath, tagPath);
            await context.EditResponseAsync(
                $"Archived `{saveFile.SaveName}` as `{tagName}` ({DiscordUtils.FormatHumanBytes(saveFile.SizeInBytes)})");
        }

        [SlashCommand("save-list", "Lists existing backups")]
        [SlashCommandPermissions(Permissions.Administrator)]
        public async Task ListSaves(InteractionContext context)
        {
            await context.CreateResponseAsync("Loading saves...");
            var saves = _saves.AllSaves.OrderByDescending(x => x.TimeUtc).ToList();
            await context.RespondPaginated(saves,
                new DiscordUtils.TableFormatter(2),
                (table, save) =>
                    table.AddRow(
                        save.SaveName,
                        DiscordUtils.FormatHumanBytes(save.SizeInBytes),
                        save.TimeUtc.AsDiscordTime()),
                "Save");
        }

        public enum BisectObjectType
        {
            [ChoiceName("Entity")]
            Entity,

            [ChoiceName("Group")]
            Group,
        }

        private readonly struct BisectState
        {
            public readonly bool Present;
            public readonly long Size;
            public readonly SaveFile Save;

            public BisectState(bool present, long size, SaveFile save)
            {
                Present = present;
                Size = size;
                Save = save;
            }
        }

        [SlashCommand("save-bisect", "Determines the saves the entity or group majorly changes in")]
        [SlashCommandPermissions(Permissions.Administrator)]
        public async Task Bisect(
            InteractionContext context,
            [Option("target", "Type of object to bisect")]
            BisectObjectType target,
            [Option("object-id", "Object ID to lookup, decimal (1234) or hex (0xABC)")]
            string objectIdString)
        {
            if (!ObjectIds.TryParseObjectId(objectIdString, out var objectId))
            {
                await context.CreateResponseAsync("Invalid object ID.  Valid styles are 1234 or 0xABC.");
                return;
            }

            await context.CreateResponseAsync("Loading saves...");
            var saves = _saves.AllSaves.OrderBy(x => x.TimeUtc).ToList();
            await context.EditResponseAsync($"Scanning {saves.Count} save{(saves.Count != 1 ? "s" : "")}");

            // At least 4KiB and a 10% change in size
            long sizeChangeThreshold = target switch
            {
                BisectObjectType.Entity => 4096,
                BisectObjectType.Group => 1024,
                _ => throw new ArgumentOutOfRangeException(nameof(target), target, null)
            };

            const float sizeChangeFactor = 0.1f;

            var events = new List<BisectState>();
            BisectState prevRecorded = default;
            BisectState? prev = null;
            foreach (var saveFile in saves)
            {
                using var save = saveFile.Open();
                long size;
                var isPresent = target switch
                {
                    BisectObjectType.Entity => save.TryGetEntityFileInfo(objectId, out size),
                    BisectObjectType.Group => save.TryGetGroupFileInfo(objectId, out size),
                    _ => throw new ArgumentOutOfRangeException(nameof(target), target, null)
                };
                var curr = new BisectState(isPresent, size, saveFile);
                var sizeChange = Math.Abs(prevRecorded.Size - curr.Size);
                if (curr.Present != prevRecorded.Present || (sizeChange > sizeChangeThreshold && sizeChange > curr.Size * sizeChangeFactor))
                {
                    if (prev.HasValue && prev.Value.Present != curr.Present && !ReferenceEquals(prev.Value.Save, prevRecorded.Save))
                        events.Add(prev.Value);
                    events.Add(curr);
                    prevRecorded = curr;
                }

                prev = curr;
            }

            await context.RespondPaginated(events,
                new DiscordUtils.TableFormatter(3),
                (table, evt) => table.AddRow(
                    evt.Present ? "Present" : "Missing",
                    DiscordUtils.FormatHumanBytes(evt.Size),
                    evt.Save.SaveName,
                    evt.Save.TimeUtc.AsDiscordTime()
                ),
                objectType: $"{target} event",
                noPagesMessage: $"{target} was not in any save files");
        }

        public enum InspectObjectType
        {
            [ChoiceName("Entity")]
            Entity,

            [ChoiceName("Group")]
            Group,
        }

        [SlashCommand("save-inspect", "Inspects a single object within a save")]
        [SlashCommandPermissions(Permissions.Administrator)]
        public async Task Inspect(
            InteractionContext context,
            [Option("save", "Save file name from /save list")] [Autocomplete(typeof(AllSaveFilesAutoCompleter))]
            string saveName,
            [Option("target", "Type of object to bisect")]
            InspectObjectType target,
            [Option("object-id", "Object ID to lookup, decimal (1234) or hex (0xABC)")]
            string objectIdString,
            [Option("include-closure", "Load all linked entities and groups through the closure")]
            bool includeClosure = false)
        {
            if (!ObjectIds.TryParseObjectId(objectIdString, out var objectId))
            {
                await context.CreateResponseAsync("Invalid object ID.  Valid styles are 1234 or 0xABC.");
                return;
            }

            await context.CreateResponseAsync($"Loading save `{saveName}`...");
            if (!_saves.TryOpenSave(saveName, out var saveFile))
            {
                await context.EditResponseAsync($"Failed to load save `{saveFile}`");
                return;
            }

            var index = _saves.Index(saveFile);
            using var save = saveFile.Open();

            var embed = new DiscordEmbedBuilder()
                .WithTitle($"{target} {objectIdString}")
                .WithTimestamp(saveFile.TimeUtc)
                .WithFooter(saveName);

            SaveFileIndex.Closure closure = null;
            bool hasClosure;
            switch (target)
            {
                case InspectObjectType.Entity:
                {
                    if (!save.TryGetEntity(objectId, out var entity))
                    {
                        await context.EditResponseAsync($"Failed to load entity {objectIdString} in `{saveFile}`");
                        return;
                    }

                    embed.AddField("type", entity.Subtype ?? "Unknown", true);
                    using (_dataStore.Read(out var dataStore))
                        embed.AddField("location", dataStore.Planet.GetAreaName(entity.CenterOrPivot(dataStore.GridDatabase)));
                    var blockCount = entity.BlockCount;
                    if (blockCount > 0)
                        embed.AddField("blocks", blockCount.ToString(), true);
                    embed.AddField("components", string.Join(", ", entity.Components.Select(x => x.Type)), true);
                    if (entity.ChunkData.HasValue)
                        embed.AddField("chunks", entity.ChunkData.Value.ToString());

                    hasClosure = includeClosure && index.TryGetClosureForEntity(objectId, out closure);
                    break;
                }
                case InspectObjectType.Group:
                {
                    if (!save.TryGetGroup(objectId, out var group))
                    {
                        await context.EditResponseAsync($"Failed to load group {objectIdString} in `{saveFile}`");
                        return;
                    }

                    embed.AddField("type", group.Type, true);
                    if (group.ChunkData.HasValue)
                    {
                        using (_dataStore.Read(out var dataStore))
                            embed.AddField("location", dataStore.Planet.GetAreaName(group.ChunkData.Value.WorldBounds(dataStore.GridDatabase).Center));
                        embed.AddField("chunks", group.ChunkData.Value.ToString());
                    }

                    var entitiesTag = $"entities ({group.TopLevelEntities.Count})";
                    embed.AddField(entitiesTag, "Loading...");
                    await context.EditResponseAsync(embed);
                    embed.RemoveFieldWithName(entitiesTag);
                    embed.AddField(entitiesTag, RenderEntityList(save, group.TopLevelEntities));
                    hasClosure = includeClosure && index.TryGetClosureForGroup(objectId, out closure);
                    break;
                }
                default:
                    throw new ArgumentOutOfRangeException(nameof(target), target, null);
            }

            await context.EditResponseAsync(embed);
            if (!hasClosure)
                return;
            {
                var entitiesTag = $"closure entities ({closure.Entities.Count})";
                var groupsTag = $"closure groups ({closure.Groups.Count})";
                embed.AddField(entitiesTag, "Loading...");
                embed.AddField(groupsTag, "Loading...");
                await context.EditResponseAsync(embed);
                embed.RemoveFieldWithName(entitiesTag);
                embed.RemoveFieldWithName(groupsTag);
                embed.AddField(entitiesTag,
                    RenderEntityList(save, closure.Entities));
                embed.AddField(groupsTag,
                    RenderGroupList(save, closure.Groups));
                await context.EditResponseAsync(embed);
            }
        }

        [SlashCommand("save-export", "Exports a single object or closure within a save")]
        [SlashCommandPermissions(Permissions.Administrator)]
        public async Task Export(
            InteractionContext context,
            [Option("save", "Save file name from /save list")] [Autocomplete(typeof(AllSaveFilesAutoCompleter))]
            string saveName,
            [Option("target", "Type of object to bisect")]
            InspectObjectType target,
            [Option("object-id", "Object ID to lookup, decimal (1234) or hex (0xABC)")]
            string objectIdString,
            [Option("include-closure", "Load all linked entities and groups through the closure")]
            bool includeClosure = true)
        {
            if (!ObjectIds.TryParseObjectId(objectIdString, out var objectId))
            {
                await context.CreateResponseAsync("Invalid object ID.  Valid styles are 1234 or 0xABC.");
                return;
            }

            await context.CreateResponseAsync($"Loading save `{saveName}`...");
            if (!_saves.TryOpenSave(saveName, out var saveFile))
            {
                await context.EditResponseAsync($"Failed to load save `{saveFile}`");
                return;
            }

            var closure = target switch
            {
                InspectObjectType.Entity => new SaveFileIndex.Closure(new EntityId(objectId)),
                InspectObjectType.Group => new SaveFileIndex.Closure(new GroupId(objectId)),
                _ => throw new ArgumentOutOfRangeException(nameof(target), target, null)
            };
            if (includeClosure)
                closure.Expand(_saves.Index(saveFile));
            await context.EditResponseAsync($"Generating blueprint with {closure.Entities.Count} entities and {closure.Groups.Count} groups");
            using var save = saveFile.Open();

            var failedEntities = new List<EntityId>();
            var failedGroups = new List<GroupId>();
            XmlDocument blueprint;
            using (_dataStore.Read(out var data))
                blueprint = BlueprintCreator.CreateBlueprint(
                    $"Exported {target} {objectIdString}", save, data.GridDatabase,
                    closure.Entities, closure.Groups,
                    failedEntities, failedGroups);

            var everythingFailed = failedEntities.Count == closure.Entities.Count && failedGroups.Count == closure.Groups.Count;
            var content = new StringBuilder();
            content.AppendLine(everythingFailed
                ? "All entities and groups failed to load"
                : $"Generated blueprint with {closure.Entities.Count - failedEntities.Count} entities and {closure.Groups.Count - failedGroups.Count} groups");
            if (failedEntities.Count > 0 || failedGroups.Count > 0)
            {
                content.AppendLine("```");
                if (failedEntities.Count > 0)
                    content.AppendLine("Failed entities:");
                foreach (var id in failedEntities)
                    content.AppendLine(id.ToString());
                if (failedGroups.Count > 0)
                    content.AppendLine("Failed groups:");
                foreach (var id in failedGroups)
                    content.AppendLine(id.ToString());
                content.Append("```");
            }

            var msg = new DiscordWebhookBuilder
            {
                Content = content.ToString()
            };
            if (!everythingFailed)
            {
                var tmp = new MemoryStream();
                blueprint.Save(tmp);
                tmp.Position = 0;
                msg.AddFile("bp.sbc", tmp);
            }

            await context.EditResponseAsync(msg);
        }

        private static string RenderEntityList(
            SaveFileAccessor save,
            IEnumerable<EntityId> entities,
            int limit = 10)
        {
            var table = new DiscordUtils.TableFormatter(3);
            table.AddRow("id", "type", "blocks");
            foreach (var row in entities
                         .Select(id => save.TryGetEntity(id, out var e) ? (e.Id, e.Subtype, e.BlockCount) : (id, "Failed", 0))
                         .OrderByDescending(e => e.Item3)
                         .Take(limit))
            {
                table.AddRow(row.Item1.ToString(), row.Item2, row.Item3 > 0 ? row.Item3.ToString() : "");
            }

            return table.RowCount > 1 ? table.ToString() : "none loaded";
        }

        private static string RenderGroupList(
            SaveFileAccessor save,
            IEnumerable<GroupId> groups,
            int limit = 10)
        {
            var table = new DiscordUtils.TableFormatter(3);
            table.AddRow("id", "type", "entities");
            foreach (var row in groups
                         .Select(id => save.TryGetGroup(id, out var e) ? (e.Id, e.Type, e.TopLevelEntities.Count) : (id, "Failed", 0))
                         .OrderByDescending(e => e.Item3)
                         .Take(limit))
            {
                table.AddRow(row.Item1.ToString(), row.Item2, row.Item3 > 0 ? row.Item3.ToString() : "");
            }

            return table.RowCount > 1 ? table.ToString() : "none loaded";
        }
    }
}