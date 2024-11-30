using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using DSharpPlus.Entities;
using Meds.Shared;
using Meds.Shared.Data;
using Meds.Watchdog.Discord;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SteamKit2.Internal;
using ZLogger;

namespace Meds.Watchdog
{
    public class ModUpdateData
    {
        [XmlIgnore]
        public readonly Dictionary<ulong, SingleModData> Data = new Dictionary<ulong, SingleModData>();

        [XmlElement("Mod")]
        public SingleModData[] Mods
        {
            get => Data.Values.ToArray();
            set
            {
                Data.Clear();
                if (value == null) return;
                foreach (var val in value)
                    Data[val.Mod] = val;
            }
        }

        public class SingleModData
        {
            [XmlAttribute("Id")]
            public ulong Mod;

            [XmlAttribute("Game")]
            public uint GameTimeUpdated;

            [XmlAttribute("Latest")]
            public uint LatestTimeUpdated;
        }
    }

    public class ModUpdateTracker : BackgroundService
    {
        private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(15);
        
        private readonly ILogger<ModUpdateTracker> _log;
        private readonly LifecycleController _lifecycle;
        private readonly DiscordMessageBridge _discord;
        private readonly ISubscriber<ReportModsMessage> _modSubscriber;
        private readonly DataStore _data;
        private readonly Updater _updater;

        private readonly Refreshable<TimeSpan?> _restartAfterUpdate;
        private readonly Refreshable<TimeSpan> _readinessDelay;
        private readonly Refreshable<bool> _sendModChangesOnline;
        private readonly Refreshable<bool> _sendModChangesOffline;

        private DateTime? _restartCooldownEndsAt;
        private CancellationTokenSource _modCheckDelayInterrupt;

        public ModUpdateTracker(ILogger<ModUpdateTracker> log, Refreshable<Configuration> cfg,
            LifecycleController lifecycle, DiscordMessageBridge discord, Updater updater,
            ISubscriber<ReportModsMessage> modSubscriber, DataStore data)
        {
            _log = log;
            _lifecycle = lifecycle;
            _discord = discord;
            _updater = updater;
            _modSubscriber = modSubscriber;
            _data = data;

            _restartAfterUpdate = cfg.Map(x => x.RestartAfterModUpdate >= 0 ? (TimeSpan?)TimeSpan.FromSeconds(x.RestartAfterModUpdate) : null);
            _readinessDelay = cfg.Map(x => TimeSpan.FromSeconds(x.ReadinessTimeout + x.LivenessTimeout));
            _sendModChangesOnline = discord.IsOutputChannelConfigured(DiscordMessageBridge.ModUpdatedServerOnline);
            _sendModChangesOffline = discord.IsOutputChannelConfigured(DiscordMessageBridge.ModUpdatedServerOffline);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var subscription = _modSubscriber.Subscribe(ReportMods);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ReportModUpdates();
                }
                catch (Exception err)
                {
                    _log.ZLogWarning(err, "Failed to check for mod updates in the background");
                }

                _modCheckDelayInterrupt = new CancellationTokenSource();
                try
                {
                    await Task.Delay(CheckInterval,
                        CancellationTokenSource.CreateLinkedTokenSource(_modCheckDelayInterrupt.Token, stoppingToken).Token);
                }
                catch
                {
                    // ignore errors
                }
            }
        }

        private const int ChangelogCharLimit = 1024;
        private const int ChangelogSingleLineLimit = 32;

        private async Task ReportModUpdates()
        {
            var restartAfterUpdate = _restartAfterUpdate.Current;

            var serverRunning = _lifecycle.Active.State == LifecycleStateCase.Running;
            var sendDiscord = (serverRunning ? _sendModChangesOnline : _sendModChangesOffline).Current;

            if (!sendDiscord && !(serverRunning && restartAfterUpdate != null)) return;

            var mods = await _updater.Run(tok => LoadGameMods(tok, sendDiscord));
            if (mods.Count == 0) return;

            var updatedGameMods = mods.Where(x => x.Details.time_updated > x.GameTimeUpdated).ToList();
            if (serverRunning && restartAfterUpdate != null && updatedGameMods.Count > 0
                && (_restartCooldownEndsAt == null || DateTime.UtcNow >= _restartCooldownEndsAt))
            {
                var restartAtUtc = DateTime.UtcNow + restartAfterUpdate.Value;
                var curr = _lifecycle.Request;
                if (curr == null || curr.Value.ActivateAtUtc > restartAtUtc)
                {
                    _lifecycle.Request = new LifecycleStateRequest(restartAtUtc, new LifecycleState(
                        LifecycleStateCase.Restarting,
                        $"Mod Update{(updatedGameMods.Count > 1 ? "s" : "")}: {string.Join(", ", updatedGameMods.Take(4).Select(x => x.Details.title))}"));
                    _restartCooldownEndsAt = DateTime.UtcNow + restartAfterUpdate.Value + CheckInterval + _readinessDelay.Current;
                }
            }

            if (!sendDiscord) return;
            foreach (var mod in mods)
            {
                // Not changed since last message.
                if (mod.Details.time_updated <= mod.PrevTimeUpdated) continue;

                // No change notes.
                if (mod.Changes.All(x => string.IsNullOrWhiteSpace(x.change_description))) continue;

                var updatedAt = DateTimeOffset.FromUnixTimeSeconds(mod.Details.time_updated);
                await _discord.ToDiscord(
                    serverRunning ? DiscordMessageBridge.ModUpdatedServerOnline : DiscordMessageBridge.ModUpdatedServerOffline,
                    (_, msg) =>
                    {
                        msg.Content = "Mod Updated";
                        var embed = new DiscordEmbedBuilder()
                            .WithTitle($"{mod.Details.title}")
                            .WithUrl(mod.Changes.Count == 0
                                ? $"https://steamcommunity.com/sharedfiles/filedetails/?id={mod.Id}"
                                : $"https://steamcommunity.com/sharedfiles/filedetails/changelog/{mod.Id}")
                            .WithTimestamp(updatedAt);
                        if (!string.IsNullOrEmpty(mod.Details.image_url))
                            embed.WithImageUrl(mod.Details.image_url);
                        var desc = new StringBuilder();
                        foreach (var change in mod.Changes)
                        {
                            if (desc.Length + change.change_description.Length >= ChangelogCharLimit) break;
                            var changeLines = SteamToDiscord(change.change_description)
                                .Split('\n')
                                .Select(x => x.Trim())
                                .Where(x => x.Length > 0)
                                .ToList();

                            desc.Append($"{DateTimeOffset.FromUnixTimeSeconds(change.timestamp).AsDiscordTime()}:");
                            switch (changeLines.Count)
                            {
                                case 0:
                                    desc.AppendLine(" (no change notes)");
                                    break;
                                case 1 when changeLines[0].Length <= ChangelogSingleLineLimit:
                                    desc.Append(" ").AppendLine(changeLines[0].Substring(changeLines[0].StartsWith("- ") ? 2 : 0));
                                    break;
                                default:
                                {
                                    desc.AppendLine();
                                    foreach (var line in changeLines)
                                        desc.Append("> ").AppendLine(line);
                                    break;
                                }
                            }
                        }

                        if (desc.Length > 0) embed.WithDescription(desc.ToString());
                        msg.WithEmbed(embed.Build());
                    });
            }
        }

        private static readonly Regex TagPattern = new Regex(@"\[(\/?)([a-z*]+)\]");

        private static string SteamToDiscord(string steamText) => TagPattern.Replace(steamText, match =>
        {
            var closing = match.Groups[1].Length > 0;

            switch (match.Groups[2].Value)
            {
                case "h1":
                    return closing ? "\n" : "# ";
                case "h2":
                    return closing ? "\n" : "## ";
                case "h3":
                    return closing ? "\n" : "### ";
                case "b":
                    return "**";
                case "i":
                    return "*";
                case "u":
                    return "__";
                case "*":
                    return "\n- ";
                case "code":
                    return "`";
                case "spoiler":
                    return "||";
                case "url":
                    return "";
                case "list":
                case "olist":
                case "tr":
                case "td":
                case "table":
                case "hr":
                    return "\n";
                default:
                    return match.Value;
            }
        });

        private async Task<List<ModUpdateInfo>> LoadGameMods(Updater.UpdaterToken updater, bool loadChangelog)
        {
            var results = new List<ModUpdateInfo>();

            using (_data.Read(out var data))
            {
                var modUpdates = data.ModUpdates.Data;
                foreach (var mod in modUpdates.Values)
                    if (mod.GameTimeUpdated > 0)
                        results.Add(new ModUpdateInfo { Id = mod.Mod, GameTimeUpdated = mod.GameTimeUpdated, PrevTimeUpdated = mod.LatestTimeUpdated });
            }

            var loadedDetails = await updater.LoadModDetails(results.Select(x => x.Id));
            using (var write = _data.Write(out var data))
            {
                var modUpdates = data.ModUpdates.Data;
                foreach (var mod in modUpdates.Values)
                    if (loadedDetails.TryGetValue(mod.Mod, out var details))
                        write.Update(ref mod.LatestTimeUpdated, details.time_updated);
            }

            for (var i = 0; i < results.Count; i++)
            {
                var entry = results[i];
                if (loadedDetails.TryGetValue(entry.Id, out var details))
                {
                    entry.Details = details;
                    var loadSince = entry.PrevTimeUpdated == 0 ? entry.GameTimeUpdated : entry.PrevTimeUpdated;

                    if (loadChangelog && details.time_updated > loadSince)
                        entry.Changes = (await updater.LoadModChangeHistory(entry.Id, loadSince))
                            .Where(x => !string.IsNullOrWhiteSpace(x.change_description))
                            .ToList();
                    else
                        entry.Changes = new List<CPublishedFile_GetChangeHistory_Response.ChangeLog>(0);
                    results[i] = entry;
                    continue;
                }

                results.RemoveAt(i);
                --i;
            }

            return results;
        }

        private struct ModUpdateInfo
        {
            public ulong Id;

            public uint GameTimeUpdated;
            public uint PrevTimeUpdated;

            public PublishedFileDetails Details;
            public List<CPublishedFile_GetChangeHistory_Response.ChangeLog> Changes;
        }

        private void ReportMods(ReportModsMessage obj)
        {
            var mods = new HashSet<ulong>();
            for (var i = 0; i < obj.ModsLength; i++)
                mods.Add(obj.Mods(i));
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
            Task.Run(async () =>
            {
                try
                {
                    await _updater.Run(token => ReportMods(token, mods));
                    _modCheckDelayInterrupt.Cancel();
                }
                catch (Exception err)
                {
                    _log.ZLogWarning(err, "Failed to update mods: {0}", string.Join(", ", mods));
                }
            });
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
        }

        private async Task ReportMods(Updater.UpdaterToken updater, HashSet<ulong> mods)
        {
            var info = await updater.LoadModDetails(mods);
            using var tok = _data.Write(out var data);
            var modUpdates = data.ModUpdates.Data;
            foreach (var mod in info)
            {
                if (!modUpdates.TryGetValue(mod.Key, out var modUpdate))
                    modUpdates.Add(mod.Key, modUpdate = new ModUpdateData.SingleModData { Mod = mod.Key });
                tok.Update(ref modUpdate.GameTimeUpdated, mod.Value.time_updated);
            }

            foreach (var kv in modUpdates)
                if (!mods.Contains(kv.Key))
                    tok.Update(ref kv.Value.GameTimeUpdated, 0u);
        }
    }
}