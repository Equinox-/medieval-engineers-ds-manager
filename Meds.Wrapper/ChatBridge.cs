using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Medieval.GameSystems.Factions;
using Meds.Shared;
using Meds.Shared.Data;
using Microsoft.Extensions.Hosting;
using Sandbox;
using Sandbox.Definitions.Chat;
using Sandbox.Game.GameSystems.Chat;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.Players;
using VRage.Game;
using VRage.Sync;
using VRage.Utils;

namespace Meds.Standalone
{
    public class ChatBridge : IHostedService
    {
        private readonly ISubscriber<ChatMessage> _subscriber;

        public ChatBridge(ISubscriber<ChatMessage> subscriber)
        {
            _subscriber = subscriber;
        }

        private void SendChat(ChatMessage obj)
        {
            long? houseChannel = null;
            ulong? playerChannel = null;
            MyStringHash channelId;
            switch (obj.ChannelType)
            {
                case ChatChannel.HouseChatChannel:
                {
                    var channel = obj.Channel<HouseChatChannel>();
                    if (!channel.HasValue) return;
                    houseChannel = channel.Value.House;
                    channelId = MyStringHash.GetOrCompute(channel.Value.Channel);
                    break;
                }
                case ChatChannel.PlayerChatChannel:
                {
                    var channel = obj.Channel<PlayerChatChannel>();
                    if (!channel.HasValue) return;
                    playerChannel = channel.Value.Player;
                    channelId = MyStringHash.GetOrCompute(channel.Value.Channel);
                    break;
                }
                case ChatChannel.GenericChatChannel:
                {
                    var channel = obj.Channel<GenericChatChannel>();
                    if (!channel.HasValue) return;
                    channelId = MyStringHash.GetOrCompute(channel.Value.Channel);
                    break;
                }
                case ChatChannel.NONE:
                default:
                    return;
            }

            var msg = obj.Message;
            var sender = obj.Sender;
            MySandboxGame.Static?.Invoke(() => HandleSendMainThread(channelId, sender, msg, houseChannel, playerChannel));
        }

        private void HandleSendMainThread(MyStringHash channel, ulong sender, string message, long? houseChannel, ulong? playerChannel)
        {
            if (!MyDefinitionManager.TryGet<MyChatChannelDefinition>(channel, out _))
                return;
            var chat = MyChatSystem.Static;
            var identities = MyIdentities.Static;
            var players = MyPlayers.Static;
            var clients = Sync.Clients;
            if (chat == null || identities == null || players == null || clients == null) return;

            void SendTo(ulong player)
            {
                if (clients.HasClient(player))
                    chat.SendMessageToClient(player, channel, sender, message);
            }

            if (houseChannel.HasValue)
            {
                var faction = MyFactionManager.Instance?.GetFactionById(houseChannel.Value);
                if (faction == null) return;
                foreach (var id in faction.Members.Keys)
                    if (identities.GetAllIdentities().TryGetValue(id, out var identity))
                    {
                        var player = players.GetPlayer(identity);
                        if (player != null)
                            SendTo(player.Id.SteamId);
                    }

                return;
            }

            if (playerChannel.HasValue)
            {
                SendTo(playerChannel.Value);
                return;
            }

            chat.BroadcastMessage(channel, sender, message);
        }

        private IDisposable _sendChatSubscription;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _sendChatSubscription = _subscriber.Subscribe(SendChat);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _sendChatSubscription.Dispose();
            return Task.CompletedTask;
        }
    }
}