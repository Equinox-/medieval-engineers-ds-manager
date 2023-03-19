using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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

        public const string ModChannelPrefix = "mods.";

        private readonly ILogger<DiscordMessageBridge> _log;
        private readonly DiscordService _discord;
        private readonly ISubscriber<PlayerJoinedLeft> _playerJoinedLeft;
        private readonly ISubscriber<ModEventMessage> _modEvents;
        private readonly ISubscriber<ChatMessage> _chat;
        private readonly Dictionary<string, List<DiscordChannelSync>> _toDiscord = new Dictionary<string, List<DiscordChannelSync>>();
        private readonly LifetimeController _lifetime;

        public DiscordMessageBridge(DiscordService discord, Configuration config, ISubscriber<PlayerJoinedLeft> playerJoinedLeft,
            ILogger<DiscordMessageBridge> log, LifetimeController lifetime, ISubscriber<ModEventMessage> modEvents, ISubscriber<ChatMessage> chat)
        {
            _lifetime = lifetime;
            _modEvents = modEvents;
            _discord = discord;
            _playerJoinedLeft = playerJoinedLeft;
            _log = log;
            _chat = chat;
            var syncs = config.Discord.ChannelSyncs;
            if (syncs != null)
                foreach (var sync in syncs)
                {
                    if ((sync.DiscordChannel == 0 && (sync.DmUser == 0 || sync.DmGuild == 0)) || string.IsNullOrEmpty(sync.EventChannel))
                        continue;
                    if (!_toDiscord.TryGetValue(sync.EventChannel, out var group))
                        _toDiscord.Add(sync.EventChannel, group = new List<DiscordChannelSync>());
                    group.Add(sync);
                }
        }

        private bool TryGetToDiscordConfig(string eventChannel, out List<DiscordChannelSync> config)
        {
            var query = eventChannel;
            while (true)
            {
                if (_toDiscord.TryGetValue(query, out config))
                    return true;
                var prevDot = query.LastIndexOf('.');
                if (prevDot <= 0)
                    return false;
                query = query.Substring(0, prevDot);
            }
        }

        private async Task ToDiscord(string eventChannel, DiscordMessageSender sender)
        {
            if (!TryGetToDiscordConfig(eventChannel, out var channels) || channels.Count == 0)
                return;
            foreach (var channel in channels)
            {
                if (channel.DiscordChannel != 0)
                {
                    await SendToChannel(eventChannel, channel, sender);
                    // Slight delay to prevent throttling.
                    await Task.Delay(TimeSpan.FromSeconds(5));
                }

                if (channel.DmGuild != 0 && channel.DmUser != 0)
                {
                    await SendToUser(eventChannel, channel, sender);
                    // Slight delay to prevent throttling.
                    await Task.Delay(TimeSpan.FromSeconds(5));
                }
            }
        }

        private async Task SendToChannel(string eventChannel, DiscordChannelSync channel, DiscordMessageSender sender)
        {
            try
            {
                var channelObj = await _discord.Client.GetChannelAsync(channel.DiscordChannel);
                await _discord.Client.SendMessageAsync(channelObj, builder =>
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
            }
        }

        private async Task SendToUser(string eventChannel, DiscordChannelSync channel, DiscordMessageSender sender)
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

                var builder = new DiscordMessageBuilder();
                sender(channel, builder);
                await member.SendMessageAsync(builder);
            }
            catch (Exception err)
            {
                _log.ZLogWarning(err, "Failed to dispatch discord message to user {0} via guild {1} for event {2}",
                    channel.DmUser, channel.DmGuild, eventChannel);
            }
        }


        private delegate void DiscordMessageSender(DiscordChannelSync matched, DiscordMessageBuilder message);

        private void ToDiscordFork(string eventChannel, DiscordMessageSender sender)
        {
#pragma warning disable CS4014
            ToDiscord(eventChannel, sender);
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

        private void HandleStateChanged(LifetimeState prev, LifetimeState current)
        {
            if (prev.State == LifetimeStateCase.Faulted || current.State != LifetimeStateCase.Faulted)
                return;
            ToDiscordFork(FaultEvents,
                (_, builder) => builder.Content = "Server has faulted and likely won't come back up without manual intervention.");
        }

        private TimeSpan? _shutdownDuration;

        private void HandleStartStop(LifetimeController.StartStopEvent state, TimeSpan uptime)
        {
            var targetState = _lifetime.Active.State;
            switch (state)
            {
                case LifetimeController.StartStopEvent.Starting:
                    if (targetState != LifetimeStateCase.Restarting || !_shutdownDuration.HasValue)
                        ToDiscordFork(StateChangeStarted, (_, builder) => builder.Content = "⏳ Server is starting...");
                    break;
                case LifetimeController.StartStopEvent.Started:
                {
                    var shutdownDuration = _shutdownDuration;
                    if (targetState == LifetimeStateCase.Restarting && shutdownDuration.HasValue)
                        ToDiscordFork(StateChangeFinished,
                            (_, builder) => builder.Content = $"🟩 Server restarted after {(uptime + shutdownDuration.Value).FormatHumanDuration()}.");
                    else
                        ToDiscordFork(StateChangeFinished, (_, builder) => builder.Content = $"🟩 Server started after {uptime.FormatHumanDuration()}.");
                    _shutdownDuration = null;
                    break;
                }
                case LifetimeController.StartStopEvent.Stopping:
                    if (targetState == LifetimeStateCase.Shutdown)
                        ToDiscordFork(StateChangeStarted, (_, builder) => builder.Content = "⌛ Server is stopping...");
                    else if (targetState == LifetimeStateCase.Restarting)
                        ToDiscordFork(StateChangeStarted, (_, builder) => builder.Content = "⌛ Server is restarting...");
                    break;
                case LifetimeController.StartStopEvent.Stopped:
                    if (targetState == LifetimeStateCase.Shutdown)
                        ToDiscordFork(StateChangeFinished, (_, builder) => builder.Content = $"💤 Server stopped after {uptime.FormatHumanDuration()}.");
                    else
                        _shutdownDuration = uptime;
                    break;
                case LifetimeController.StartStopEvent.Crashed:
                    ToDiscordFork(StateChangeError, (_, builder) => builder.Content = $"🪦 Server crashed after {uptime.FormatHumanDuration()}.");
                    _shutdownDuration = null;
                    break;
                case LifetimeController.StartStopEvent.Froze:
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
}