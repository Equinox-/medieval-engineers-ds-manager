using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DSharpPlus.Entities;
using Meds.Shared;
using Meds.Shared.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Meds.Watchdog.Discord
{
    public class DiscordMessageBridge : IHostedService
    {
        public const string JoinLeaveEvents = "internal.playerJoinLeave";
        public const string FaultEvents = "internal.serverFaulted";

        private readonly ILogger<DiscordMessageBridge> _log;
        private readonly DiscordService _discord;
        private readonly ISubscriber<PlayerJoinedLeft> _playerJoinedLeft;
        private readonly Dictionary<string, List<DiscordChannelSync>> _toDiscord = new Dictionary<string, List<DiscordChannelSync>>();
        private readonly LifetimeController _lifetime;

        public DiscordMessageBridge(DiscordService discord, Configuration config, ISubscriber<PlayerJoinedLeft> playerJoinedLeft,
            ILogger<DiscordMessageBridge> log, LifetimeController lifetime)
        {
            _lifetime = lifetime;
            _discord = discord;
            _playerJoinedLeft = playerJoinedLeft;
            _log = log;
            var syncs = config.Discord.ChannelSyncs;
            if (syncs != null)
                foreach (var sync in syncs)
                {
                    if (sync.DiscordChannel == 0 || string.IsNullOrEmpty(sync.EventChannel))
                        continue;
                    if (sync.ToDiscord)
                    {
                        if (!_toDiscord.TryGetValue(sync.EventChannel, out var group))
                            _toDiscord.Add(sync.EventChannel, group = new List<DiscordChannelSync>());
                        group.Add(sync);
                    }
                }
        }

        private async Task ToDiscord(string eventChannel, Action<DiscordMessageBuilder> message)
        {
            if (!_toDiscord.TryGetValue(eventChannel, out var channels) || channels.Count == 0)
                return;
            foreach (var channel in channels)
            {
                try
                {
                    var channelObj = await _discord.Client.GetChannelAsync(channel.DiscordChannel);
                    await _discord.Client.SendMessageAsync(channelObj, builder =>
                    {
                        message(builder);
                        if (channel.MentionRole != 0)
                            builder.Content += $" <@&{channel.MentionRole}>";
                        if (channel.MentionUser != 0)
                            builder.Content += $" <@{channel.MentionUser}>";
                    });
                }
                catch (Exception err)
                {
                    _log.LogWarning(err, "Failed to dispatch discord message to channel {DiscordChannel} for event {EventChannel}", channel, eventChannel);
                }
            }
        }

        private void ToDiscordFork(string eventChannel, Action<DiscordMessageBuilder> message)
        {
#pragma warning disable CS4014
            ToDiscord(eventChannel, message);
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
                builder => builder.Content = msg);
        }

        private void HandleFaulted(LifetimeState prev, LifetimeState current)
        {
            if (prev.State == LifetimeStateCase.Faulted || current.State != LifetimeStateCase.Faulted)
                return;
            ToDiscordFork(FaultEvents,
                builder => builder.Content = "Server has faulted and likely won't come back up without manual intervention.");
        }

        private IDisposable _playerJoinedLeftSubscription;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _playerJoinedLeftSubscription = _playerJoinedLeft.Subscribe(HandlePlayerJoinedLeft);
            _lifetime.StateChanged += HandleFaulted;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _playerJoinedLeftSubscription.Dispose();
            _lifetime.StateChanged -= HandleFaulted;
            return Task.CompletedTask;
        }
    }
}