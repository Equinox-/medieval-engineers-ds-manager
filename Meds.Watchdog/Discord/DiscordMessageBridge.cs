using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using DSharpPlus.Entities;
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

        public DiscordMessageBridge(DiscordService discord, Refreshable<Configuration> config, ISubscriber<PlayerJoinedLeft> playerJoinedLeft,
            ILogger<DiscordMessageBridge> log, LifecycleController lifetime, ISubscriber<ModEventMessage> modEvents, ISubscriber<ChatMessage> chat)
        {
            _lifetime = lifetime;
            _modEvents = modEvents;
            _discord = discord;
            _playerJoinedLeft = playerJoinedLeft;
            _log = log;
            _chat = chat;

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

        public async Task<DiscordMessageReference> ToDiscord(string eventChannel, DiscordMessageSender sender)
        {
            var reference = new DiscordMessageReference();
            if (!TryGetToDiscordConfig(eventChannel, out var channels) || channels.Count == 0)
                return reference;
            foreach (var channel in channels)
            {
                DiscordMessage msg;
                if (channel.DiscordChannel != 0)
                    msg = await SendToChannel(eventChannel, channel, sender);
                else if (channel.DmGuild != 0 && channel.DmUser != 0)
                    msg = await SendToUser(eventChannel, channel, sender);
                else
                    continue;
                // Slight delay to prevent throttling.
                await Task.Delay(TimeSpan.FromSeconds(5));

                if (msg == null) continue;
                reference.Messages.Add(new DiscordMessageReference.SingleMessageReference
                {
                    Channel = msg.ChannelId,
                    Message = msg.Id
                });
            }

            return reference;
        }

        public async Task EditDiscord(string eventChannel, DiscordMessageReference reference, DiscordMessageSender sender)
        {
            if (!TryGetToDiscordConfig(eventChannel, out var channels) || channels.Count == 0)
                return;
            foreach (var msg in reference.Messages)
            {
                try
                {
                    var channelObj = await _discord.Client.GetChannelAsync(msg.Channel);
                    if (channelObj == null) continue;
                    var matching = FindMatchingSync(channelObj);
                    if (matching == null) continue;
                    var msgObj = await channelObj.GetMessageAsync(msg.Message);
                    if (msgObj == null) continue;
                    await msgObj.ModifyAsync(builder => sender(matching, builder));
                }
                catch (Exception err)
                {
                    _log.ZLogWarning(err, "Failed to edit discord message {0} on channel {1} for event {2}",
                        msg.Message,
                        msg.Channel,
                        eventChannel);
                }
            }

            return;

            DiscordChannelSync FindMatchingSync(DiscordChannel channelObj)
            {
                foreach (var candidate in channels)
                {
                    if (candidate.DiscordChannel == channelObj.Id)
                        return candidate;
                    if (candidate.DmUser == 0 || !channelObj.IsPrivate)
                        continue;
                    foreach (var member in channelObj.Users)
                        if (member.Id == candidate.DmUser)
                            return candidate;
                }

                return null;
            }
        }

        private async Task<DiscordMessage> SendToChannel(string eventChannel, DiscordChannelSync channel, DiscordMessageSender sender)
        {
            try
            {
                var channelObj = await _discord.Client.GetChannelAsync(channel.DiscordChannel);
                return await _discord.Client.SendMessageAsync(channelObj, builder =>
                {
                    sender(channel, builder);
                    if (channel.MentionRole != 0)
                        builder.Content += $" <@&{channel.MentionRole}>";
                    if (channel.MentionUser != 0)
                        builder.Content += $" <@{channel.MentionUser}>";
                });
            }
            catch (Exception err)
            {
                _log.ZLogWarning(err, "Failed to dispatch discord message to channel {0} for event {1}",
                    channel.DiscordChannel, eventChannel);
                return null;
            }
        }

        private async Task<DiscordMessage> SendToUser(string eventChannel, DiscordChannelSync channel, DiscordMessageSender sender)
        {
            try
            {
                var guild = await _discord.Client.GetGuildAsync(channel.DmGuild, false);
                if (guild == null)
                {
                    _log.ZLogWarning("Failed to find discord guild {0} when processing event {1}", channel.DmGuild, eventChannel);
                    return null;
                }

                var member = await guild.GetMemberAsync(channel.DmUser, true);
                if (member == null)
                {
                    _log.ZLogWarning("Failed to find discord user {0} in guild {1} when processing event {2}", channel.DmUser, channel.DmGuild, eventChannel);
                    return null;
                }

                var channelObj = await member.CreateDmChannelAsync();
                return await _discord.Client.SendMessageAsync(channelObj, builder => { sender(channel, builder); });
            }
            catch (Exception err)
            {
                _log.ZLogWarning(err, "Failed to dispatch discord message to user {0} via guild {1} for event {2}",
                    channel.DmUser, channel.DmGuild, eventChannel);
                return null;
            }
        }


        public delegate void DiscordMessageSender(DiscordChannelSync matched, DiscordMessageBuilder message);

        private void ToDiscordFork(string eventChannel, DiscordMessageSender sender)
        {
#pragma warning disable CS4014
            Task.Run(async () =>
            {
                try
                {
                    await ToDiscord(eventChannel, sender);
                }
                catch (Exception err)
                {
                    _log.ZLogWarning(err, "Failed to send to discord: {0} {1}", eventChannel, sender);
                }
            });
#pragma warning restore CS4014
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

        private void HandleStartStop(LifecycleController.StartStopEvent state, TimeSpan uptime, string reason)
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

            ToDiscordFork(ModChannelPrefix + channel, (_, builder) =>
            {
                builder.Content = message;
                if (builtEmbed != null)
                    builder.AddEmbed(builtEmbed);
            });
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

    public class DiscordMessageReference
    {
        [XmlElement("Message")]
        public List<SingleMessageReference> Messages = new List<SingleMessageReference>();

        public struct SingleMessageReference
        {
            [XmlAttribute]
            public ulong Channel;

            [XmlAttribute]
            public ulong Message;
        }
    }
}