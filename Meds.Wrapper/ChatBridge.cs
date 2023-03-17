using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using Medieval.GameSystems.Chat;
using Medieval.GameSystems.Factions;
using Meds.Shared;
using Meds.Shared.Data;
using Meds.Wrapper.Shim;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Sandbox;
using Sandbox.Definitions.Chat;
using Sandbox.Game.GameSystems.Chat;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.Players;
using VRage.Game;
using VRage.Utils;

namespace Meds.Wrapper
{
    public class ChatBridge : IHostedService
    {
        private readonly ISubscriber<ChatMessage> _subscriber;
        private readonly IPublisher<ChatMessage> _publisher;

        public ChatBridge(ISubscriber<ChatMessage> subscriber, IPublisher<ChatMessage> publisher)
        {
            _subscriber = subscriber;
            _publisher = publisher;
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

        private void HandleChatSender(MyChatSender senderImpl, MyChatChannel chatChannel, ulong sender, string message)
        {
            var channelName = chatChannel.Definition?.Id.SubtypeName;
            var senderPlayer = MyPlayers.Static.GetPlayer(new MyPlayer.PlayerId(sender));
            var senderFaction = senderPlayer?.Identity != null ? MyFactionManager.GetPlayerFaction(senderPlayer.Identity.Id) : null;
            if (channelName == null)
                return;
            using var token = _publisher.Publish();
            var b = token.Builder;
            ChatChannel type;
            int channelOffset;
            switch (senderImpl)
            {
                case MyHouseChatSender _:
                {
                    if (senderFaction == null)
                        return;
                    type = ChatChannel.HouseChatChannel;
                    channelOffset = HouseChatChannel.CreateHouseChatChannel(b, senderFaction.FactionId, 
                        b.CreateString(channelName), b.CreateString(senderFaction.FactionName)).Value;
                    break;
                }
                case MyDirectChatSender _:
                {
                    MyIdentity targetId = null;
                    foreach (var allIdentity in MyIdentities.Static.GetAllIdentities())
                    {
                        if (message.StartsWith(allIdentity.Value.DisplayName, StringComparison.InvariantCultureIgnoreCase) 
                            && (targetId == null || targetId.DisplayName.Length < allIdentity.Value.DisplayName.Length))
                            targetId = allIdentity.Value;
                    }

                    var targetPlayer = targetId != null ? MyPlayers.Static?.GetPlayer(targetId) : null;
                    if (targetPlayer == null)
                        return;
                    type = ChatChannel.PlayerChatChannel;
                    channelOffset = PlayerChatChannel.CreatePlayerChatChannel(b, targetPlayer.Id.SteamId, 
                        b.CreateString(channelName), b.CreateString(targetId.DisplayName)).Value;
                    break;
                }
                // Exact match for generic sender
                case { } when senderImpl.GetType() == typeof(MyChatSender) && !senderImpl.Definition.Range.HasValue:
                {
                    type = ChatChannel.GenericChatChannel;
                    channelOffset = GenericChatChannel.CreateGenericChatChannel(b, b.CreateString(channelName)).Value;
                    break;
                }
                default:
                    return;
            }

            var senderName = senderPlayer?.Identity?.DisplayName ?? $"Unknown[{sender}]";
            token.Send(ChatMessage.CreateChatMessage(b, type, channelOffset,
                b.CreateString(message), sender, b.CreateString(senderName)));
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

        [HarmonyPatch(typeof(MyChatChannel), nameof(MyChatChannel.HandleMessage))]
        [AlwaysPatch]
        public static class Patch
        {
            private static readonly MethodInfo SenderSendChat = AccessTools.Method(typeof(MyChatSender), nameof(MyChatSender.SendChat));
            private static readonly MethodInfo SendChatShimRef = AccessTools.Method(typeof(Patch), nameof(SendChatShim));

            private static void SendChatShim(MyChatSender senderImpl, MyChatChannel chatChannel, ulong sender, string message)
            {
                senderImpl.SendChat(chatChannel, sender, message);
                Entrypoint.Instance.Services.GetRequiredService<ChatBridge>()?.HandleChatSender(senderImpl, chatChannel, sender, message);
            }

            public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                foreach (var instruction in instructions)
                {
                    if (instruction.Calls(SenderSendChat))
                    {
                        instruction.opcode = OpCodes.Call;
                        instruction.operand = SendChatShimRef;
                    }
                    yield return instruction;
                }
            }
        }
    }
}