using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using DSharpPlus.Entities;
using Equ;
using Meds.Shared;
using Meds.Shared.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace Meds.Watchdog.Discord
{
    public class DiscordMessageBridge : IHostedService
    {
        public const string JoinLeaveEvents = "internal.playerJoinLeave";
        public const string FaultEvents = "internal.serverFaulted";
        public const string StateChangeStarted = "internal.serverStateChange.Started";
        public const string StateChangeFinished = "internal.serverStateChange.Finished";
        public const string StateChangeError = "internal.serverStateChange.Error";
        public const string ChatPrefix = "chat.";

        /// <summary>
        /// Channel to send mod update messages to when the server is online.
        /// </summary>
        public const string ModUpdatedServerOnline = "internal.modUpdated.online";

        /// <summary>
        /// Channel to send mod update messages to when the server is offline.
        /// </summary>
        public const string ModUpdatedServerOffline = "internal.modUpdated.offline";

        public const string ModChannelPrefix = "mods.";

        private readonly ILogger<DiscordMessageBridge> _log;
        private readonly DiscordService _discord;
        private readonly ISubscriber<PlayerJoinedLeft> _playerJoinedLeft;
        private readonly ISubscriber<ModEventMessage> _modEvents;
        private readonly ISubscriber<ChatMessage> _chat;
        private readonly Refreshable<Dictionary<string, List<DiscordChannelSync>>> _toDiscord;
        private readonly LifecycleController _lifetime;
        private readonly Dictionary<string, Refreshable<bool>> _toDiscordConfigured = new Dictionary<string, Refreshable<bool>>();
        private readonly DataStore _dataStore;

        public DiscordMessageBridge(DiscordService discord, Refreshable<Configuration> config, ISubscriber<PlayerJoinedLeft> playerJoinedLeft,
            ILogger<DiscordMessageBridge> log, LifecycleController lifetime, ISubscriber<ModEventMessage> modEvents, ISubscriber<ChatMessage> chat,
            DataStore dataStore)
        {
            _lifetime = lifetime;
            _modEvents = modEvents;
            _discord = discord;
            _playerJoinedLeft = playerJoinedLeft;
            _log = log;
            _chat = chat;
            _dataStore = dataStore;

            _toDiscord = config
                .Map(x => x.Discord.ChannelSyncs, CollectionEquality<DiscordChannelSync>.List())
                .Map(syncs =>
                {
                    var toDiscord = new Dictionary<string, List<DiscordChannelSync>>();
                    if (syncs == null)
                        return toDiscord;
                    foreach (var sync in syncs)
                    {
                        if ((sync.DiscordChannel == 0 && (sync.DmUser == 0 || sync.DmGuild == 0)) || string.IsNullOrEmpty(sync.EventChannel))
                            continue;
                        if (!toDiscord.TryGetValue(sync.EventChannel, out var group))
                            toDiscord.Add(sync.EventChannel, group = new List<DiscordChannelSync>());
                        group.Add(sync);
                    }

                    return toDiscord;
                });
        }

        public Refreshable<bool> IsOutputChannelConfigured(string eventChannel)
        {
            lock (_toDiscordConfigured)
            {
                if (!_toDiscordConfigured.TryGetValue(eventChannel, out var refreshable))
                    _toDiscordConfigured.Add(eventChannel,
                        refreshable = _toDiscord.Map(cfg => TryGetToDiscordConfig(eventChannel, out var targets) && targets.Count > 0));
                return refreshable;
            }
        }

        private bool TryGetToDiscordConfig(string eventChannel, out List<DiscordChannelSync> config)
        {
            var query = eventChannel;
            while (true)
            {
                if (_toDiscord.Current.TryGetValue(query, out config))
                    return true;
                var prevDot = query.LastIndexOf('.');
                if (prevDot <= 0)
                    return false;
                query = query.Substring(0, prevDot);
            }
        }

        public readonly struct ReuseInfo
        {
            public readonly string Id;
            public readonly TimeSpan? Ttl;

            public ReuseInfo(string id, TimeSpan? ttl)
            {
                Id = id;
                Ttl = ttl;
            }
        }

        public async Task ToDiscord(string eventChannel, DiscordMessageSender sender, ReuseInfo reuse = default)
        {
            if (!TryGetToDiscordConfig(eventChannel, out var channels) || channels.Count == 0)
                return;
            foreach (var channel in channels)
            {
                if (channel.DiscordChannel != 0)
                    await SendToChannel(eventChannel, channel, sender, reuse);
                else if (channel.DmGuild != 0 && channel.DmUser != 0)
                    await SendToUser(eventChannel, channel, sender, reuse);
                else
                    continue;
                // Slight delay to prevent throttling.
                await Task.Delay(TimeSpan.FromSeconds(5));
            }
        }

        private async Task SendToChannel(string eventChannel, DiscordChannelSync channel, DiscordMessageSender sender, ReuseInfo reuse)
        {
            try
            {
                var channelObj = await _discord.Client.GetChannelAsync(channel.DiscordChannel);
                await SendToChannelInternal(channelObj, channel, sender, reuse);
            }
            catch (Exception err)
            {
                _log.ZLogWarning(err, "Failed to dispatch discord message to channel {0} for event {1}",
                    channel.DiscordChannel, eventChannel);
            }
        }

        private async Task SendToUser(string eventChannel, DiscordChannelSync channel, DiscordMessageSender sender, ReuseInfo reuse)
        {
            try
            {
                var guild = await _discord.Client.GetGuildAsync(channel.DmGuild, false);
                if (guild == null)
                {
                    _log.ZLogWarning("Failed to find discord guild {0} when processing event {1}", channel.DmGuild, eventChannel);
                    return;
                }

                var member = await guild.GetMemberAsync(channel.DmUser, true);
                if (member == null)
                {
                    _log.ZLogWarning("Failed to find discord user {0} in guild {1} when processing event {2}", channel.DmUser, channel.DmGuild, eventChannel);
                    return;
                }

                var channelObj = await member.CreateDmChannelAsync();
                await SendToChannelInternal(channelObj, channel, sender, reuse);
            }
            catch (Exception err)
            {
                _log.ZLogWarning(err, "Failed to dispatch discord message to user {0} via guild {1} for event {2}",
                    channel.DmUser, channel.DmGuild, eventChannel);
            }
        }

        private ulong? FindReusedMessageId(DiscordChannel channelObj, string reuseId)
        {
            using (_dataStore.Read(out var data))
                return data.Discord.Events.TryGetValue(new DiscordReuseData.EventKey(reuseId, channelObj.Id), out var msg) && !msg.IsExpired
                    ? (ulong?)msg.Message
                    : null;
        }

        private async Task SendToChannelInternal(DiscordChannel channelObj, DiscordChannelSync channel, DiscordMessageSender sender, ReuseInfo reuse)
        {
            Action<DiscordMessageBuilder> composer = builder =>
            {
                sender(channel, builder);
                if (channel.MentionRole != 0)
                    builder.Content += $" <@&{channel.MentionRole}>";
                if (channel.MentionUser != 0)
                    builder.Content += $" <@{channel.MentionUser}>";
            };

            var reused = reuse.Id != null && !channel.DisableReuse ? FindReusedMessageId(channelObj, reuse.Id) : null;
            if (reused.HasValue)
            {
                try
                {
                    var msgObj = await channelObj.GetMessageAsync(reused.Value);
                    if (msgObj != null)
                    {
                        await msgObj.ModifyAsync(composer);
                        return;
                    }
                }
                catch (Exception err)
                {
                    _log.ZLogWarning(
                        err,
                        "Failed to edit existing discord message {0} in channel {1}, from event channel {2}. Will send a new message.",
                        reused.Value, channelObj.Id, channel.EventChannel);
                }
            }

            var msg = await _discord.Client.SendMessageAsync(channelObj, composer);
            if (reuse.Id != null && !channel.DisableReuse)
            {
                using var writeHandle = _dataStore.Write(out var data);
                var eventKey = new DiscordReuseData.EventKey(reuse.Id, channelObj.Id);
                var eventData = new DiscordReuseData.EventData(msg.Id,
                    reuse.Ttl.HasValue ? DiscordReuseData.EventData.ToExpiryTime(DateTime.UtcNow + reuse.Ttl.Value) : 0);
                if (eventData.IsExpired)
                    data.Discord.Events.Remove(eventKey);
                else
                    data.Discord.Events[eventKey] = eventData;
                writeHandle.MarkUpdated();
            }
        }

        public delegate void DiscordMessageSender(DiscordChannelSync matched, DiscordMessageBuilder message);

        private readonly ConcurrentDictionary<string, (Task task, CancellationTokenSource cancel, string reuseId)> _pendingMessages =
            new ConcurrentDictionary<string, (Task, CancellationTokenSource, string)>();

        private void ToDiscordFork(string eventChannel, DiscordMessageSender sender, ReuseInfo reuse = default)
        {
            _pendingMessages.AddOrUpdate(
                eventChannel,
                key => CreateTask(null),
                (key, prior) =>
                {
                    // Cancel repeating messages.
                    if (reuse.Id != null && prior.reuseId == reuse.Id && !prior.task.IsCompleted)
                    {
                        _log.ZLogInformation("Preempting previous task on channel {0} with the same reuse ID {1}", eventChannel, reuse.Id);
                        prior.cancel.Cancel();
                    }
                    return CreateTask(prior.task);
                });
            return;

            (Task, CancellationTokenSource, string) CreateTask(Task prior)
            {
                var cts = new CancellationTokenSource();
                var token = cts.Token;
                var task = Task.Run(async () =>
                {
                    try
                    {
                        // Enforce ordering between messages.
                        if (prior != null) await prior;
                    }
                    catch
                    {
                        // ignore errors from the prior.
                    }

                    if (token.IsCancellationRequested) return;

                    try
                    {
                        await ToDiscord(eventChannel, sender, reuse);
                    }
                    catch (Exception err)
                    {
                        _log.ZLogWarning(err, "Failed to send to discord: {0} {1}", eventChannel, sender);
                    }
                }, cts.Token);
                return (task, cts, reuse.Id);
            }
        }

        private void HandlePlayerJoinedLeft(PlayerJoinedLeft obj)
        {
            if (!obj.Player.HasValue)
                return;
            var plural = obj.Players != 1;
            var msg = $"{obj.Player.Value.RenderPlayerForDiscord()} {(obj.Joined ? "joined" : "left")}." +
                      $"  There {(plural ? "are" : "is")} now {obj.Players} player{(plural ? "s" : "")} online.";
            ToDiscordFork(JoinLeaveEvents,
                (_, builder) => builder.Content = msg);
        }

        private void HandleStateChanged(LifecycleState prev, LifecycleState current)
        {
            if (prev.State == LifecycleStateCase.Faulted || current.State != LifecycleStateCase.Faulted)
                return;
            ToDiscordFork(FaultEvents,
                (_, builder) => builder.Content = "Server has faulted and likely won't come back up without manual intervention.");
        }

        private TimeSpan? _shutdownDuration;

        private void HandleStartStop(LifecycleController.StartStopEvent state, TimeSpan uptime)
        {
            var target = _lifetime.Active;
            var targetState = target.State;
            var paddedReason = string.IsNullOrWhiteSpace(target.Reason) ? "" : $" ({target.Reason})";
            switch (state)
            {
                case LifecycleController.StartStopEvent.Starting:
                    if (targetState != LifecycleStateCase.Restarting || !_shutdownDuration.HasValue)
                        ToDiscordFork(StateChangeStarted, (_, builder) => builder.Content = "⏳ Server is starting..." + paddedReason);
                    break;
                case LifecycleController.StartStopEvent.Started:
                {
                    var shutdownDuration = _shutdownDuration;
                    if (targetState == LifecycleStateCase.Restarting && shutdownDuration.HasValue)
                        ToDiscordFork(StateChangeFinished,
                            (_, builder) => builder.Content = $"🟩 Server restarted after {(uptime + shutdownDuration.Value).FormatHumanDuration()}.");
                    else
                        ToDiscordFork(StateChangeFinished, (_, builder) => builder.Content = $"🟩 Server started after {uptime.FormatHumanDuration()}.");
                    _shutdownDuration = null;
                    break;
                }
                case LifecycleController.StartStopEvent.Stopping:
                    if (targetState == LifecycleStateCase.Shutdown)
                        ToDiscordFork(StateChangeStarted, (_, builder) => builder.Content = "⌛ Server is stopping..." + paddedReason);
                    else if (targetState == LifecycleStateCase.Restarting)
                        ToDiscordFork(StateChangeStarted, (_, builder) => builder.Content = "⌛ Server is restarting..." + paddedReason);
                    break;
                case LifecycleController.StartStopEvent.Stopped:
                    if (targetState == LifecycleStateCase.Shutdown)
                        ToDiscordFork(StateChangeFinished, (_, builder) => builder.Content = $"💤 Server stopped after {uptime.FormatHumanDuration()}.");
                    else
                        _shutdownDuration = uptime;
                    break;
                case LifecycleController.StartStopEvent.Crashed:
                    ToDiscordFork(StateChangeError, (_, builder) => builder.Content = $"🪦 Server crashed after {uptime.FormatHumanDuration()}.");
                    _shutdownDuration = null;
                    break;
                case LifecycleController.StartStopEvent.Froze:
                    ToDiscordFork(StateChangeError, (_, builder) => builder.Content = $"🪦 Server froze after {uptime.FormatHumanDuration()}.");
                    _shutdownDuration = null;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(state), state, null);
            }
        }

        private void HandleModEvent(ModEventMessage obj)
        {
            var channel = obj.Channel;
            if (channel == null)
                return;
            var message = obj.Message;
            var embedOpt = obj.Embed;
            DiscordEmbed builtEmbed = null;
            if (embedOpt.HasValue)
            {
                var embed = embedOpt.Value;
                var embedBuilder = new DiscordEmbedBuilder()
                    .WithTitle(embed.Title)
                    .WithDescription(embed.Description);
                for (var i = 0; i < embed.FieldsLength; i++)
                {
                    var field = embed.Fields(i);
                    var fieldKey = field?.Key;
                    var fieldValue = field?.Value;
                    if (fieldKey != null && fieldValue != null)
                    {
                        embedBuilder.AddField(fieldKey, fieldValue, field.Value.Inline);
                    }
                }

                if (obj.SourceName != null)
                {
                    embedBuilder.WithFooter(obj.SourceName);
                }

                builtEmbed = embedBuilder.Build();
            }

            var reuseId = obj.ReuseId;
            reuseId = string.IsNullOrEmpty(reuseId) ? null : $"mod_event_{obj.SourceModId}_{reuseId}";
            var reuse = reuseId != null ? new ReuseInfo(reuseId, obj.ReuseTtlSec > 0 ? (TimeSpan?)TimeSpan.FromSeconds(obj.ReuseTtlSec) : null) : default;

            ToDiscordFork(ModChannelPrefix + channel, (_, builder) =>
            {
                builder.Content = message;
                if (builtEmbed != null)
                    builder.AddEmbed(builtEmbed);
            }, reuse);
        }

        private void HandleChat(ChatMessage obj)
        {
            string channelGroupPrefix;
            string channelName;
            string targetHouseName = null;
            string targetPlayerName = null;
            switch (obj.ChannelType)
            {
                case ChatChannel.NONE:
                    return;
                case ChatChannel.HouseChatChannel:
                {
                    var house = obj.Channel<HouseChatChannel>();
                    if (!house.HasValue)
                        return;
                    targetHouseName = house.Value.HouseName;
                    channelName = house.Value.Channel;
                    channelGroupPrefix = $"{ChatPrefix}house.{house.Value.House}";
                    break;
                }
                case ChatChannel.PlayerChatChannel:
                {
                    var player = obj.Channel<PlayerChatChannel>();
                    if (!player.HasValue)
                        return;
                    targetPlayerName = player.Value.PlayerName;
                    channelName = player.Value.Channel;
                    channelGroupPrefix = $"{ChatPrefix}player.{player.Value.Player}";
                    break;
                }
                case ChatChannel.GenericChatChannel:
                {
                    var generic = obj.Channel<GenericChatChannel>();
                    if (!generic.HasValue)
                        return;
                    channelName = generic.Value.Channel;
                    channelGroupPrefix = $"{ChatPrefix}generic";
                    break;
                }
                default:
                    throw new ArgumentOutOfRangeException();
            }

            var channelExact = $"{channelGroupPrefix}.{channelName.ToLowerInvariant()}";
            var senderName = obj.SenderName;
            var message = obj.Message;
            ToDiscordFork(channelExact, (sync, msg) =>
            {
                // Simple format for exact matches
                if (sync.EventChannel == channelExact)
                {
                    msg.Content = $"{senderName}: {message}";
                    return;
                }

                // Simple format for channel group matches
                if (sync.EventChannel == channelGroupPrefix)
                {
                    msg.Content = $"<{channelName}> {senderName}: {message}";
                    return;
                }

                // Verbose format for broad matches
                var embed = new DiscordEmbedBuilder();
                embed.AddField("Sender", senderName, true);
                embed.AddField("Message", message, true);
                embed.AddField("Channel", channelName, true);
                if (targetHouseName != null)
                    embed.AddField("House", targetHouseName, true);
                if (targetPlayerName != null)
                    embed.AddField("Direct", targetPlayerName, true);
                msg.Embed = embed.Build();
            });
        }

        private IDisposable _playerJoinedLeftSubscription;
        private IDisposable _modEventsSubscription;
        private IDisposable _chatSubscription;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _playerJoinedLeftSubscription = _playerJoinedLeft.Subscribe(HandlePlayerJoinedLeft);
            _modEventsSubscription = _modEvents.Subscribe(HandleModEvent);
            _chatSubscription = _chat.Subscribe(HandleChat);
            _lifetime.StateChanged += HandleStateChanged;
            _lifetime.StartStop += HandleStartStop;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _playerJoinedLeftSubscription.Dispose();
            _modEventsSubscription.Dispose();
            _chatSubscription.Dispose();
            _lifetime.StateChanged -= HandleStateChanged;
            _lifetime.StartStop -= HandleStartStop;
            return Task.CompletedTask;
        }
    }

    public class DiscordReuseData : MemberwiseEquatable<DiscordReuseData>
    {
        public readonly struct EventKey : IEquatable<EventKey>
        {
            public readonly string Id;
            public readonly ulong Channel;

            public EventKey(string id, ulong channel)
            {
                Id = id;
                Channel = channel;
            }

            public bool Equals(EventKey other) => Id == other.Id && Channel == other.Channel;

            public override bool Equals(object obj) => obj is EventKey other && Equals(other);

            public override int GetHashCode() => (Id.GetHashCode() * 397) ^ Channel.GetHashCode();
        }

        public readonly struct EventData
        {
            public readonly ulong Message;
            public readonly long ExpiresAt;

            public EventData(ulong message, long expiresAt)
            {
                Message = message;
                ExpiresAt = expiresAt;
            }

            public static long ToExpiryTime(DateTime utcTime) => utcTime.ToFileTimeUtc();

            public bool IsExpired => ToExpiryTime(DateTime.UtcNow) >= ExpiresAt;
        }

        [XmlIgnore]
        public readonly Dictionary<EventKey, EventData> Events = new Dictionary<EventKey, EventData>();

        [XmlElement("Event")]
        public List<EventDataForXml> EventsForXml
        {
            get => Events.Select(x => new EventDataForXml { Id = x.Key.Id, Channel = x.Key.Channel, Message = x.Value.Message, ExpiresAt = x.Value.ExpiresAt })
                .ToList();
            set
            {
                Events.Clear();
                foreach (var item in value)
                {
                    var data = new EventData(item.Message, item.ExpiresAt);
                    if (data.IsExpired) continue;
                    Events[new EventKey(item.Id, item.Channel)] = data;
                }
            }
        }

        public struct EventDataForXml
        {
            [XmlAttribute]
            public string Id;

            [XmlAttribute]
            public ulong Channel;

            [XmlAttribute]
            public ulong Message;

            [XmlAttribute]
            public long ExpiresAt;
        }
    }
}