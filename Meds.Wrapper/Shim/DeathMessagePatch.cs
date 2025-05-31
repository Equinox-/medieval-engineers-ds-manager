using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using Medieval.GameSystems.DeathNotifications;
using Meds.Shared;
using Meds.Shared.Data;
using Microsoft.Extensions.DependencyInjection;
using Sandbox.Game.GameSystems.Chat;
using VRage.Utils;

namespace Meds.Wrapper.Shim
{
    [HarmonyPatch(typeof(MyDeathNotificationSystem), "OnEntityDied")]
    [AlwaysPatch]
    public static class DeathMessagePatch
    {
        public static void BroadcastMessageShim(MyChatSystem chat, MyStringHash channel, ulong sender, string message)
        {
            chat.BroadcastMessage(channel, sender, message);
            var publisher = Entrypoint.Instance?.Services.GetService<IPublisher<ChatMessage>>();
            if (publisher == null) return;
            using var token = publisher.Publish();
            token.Send(ChatMessage.CreateChatMessage(
                token.Builder,
                ChatChannel.GenericChatChannel,
                GenericChatChannel.CreateGenericChatChannel(token.Builder, token.Builder.CreateString("Death")).Value,
                token.Builder.CreateString(message),
                0, token.Builder.CreateString("System")));
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var originalCall = AccessTools.Method(typeof(MyChatSystem),
                nameof(MyChatSystem.BroadcastMessage),
                new[] { typeof(MyStringHash), typeof(ulong), typeof(string) });
            var shim = AccessTools.Method(typeof(DeathMessagePatch), nameof(BroadcastMessageShim));

            foreach (var instruction in instructions)
            {
                if (shim != null && instruction.Calls(originalCall))
                {
                    var copy = instruction.Clone();
                    copy.opcode = OpCodes.Call;
                    copy.operand = shim;
                    yield return copy;
                }
                else
                {
                    yield return instruction;
                }
            }
        }
    }
}