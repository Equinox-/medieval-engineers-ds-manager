using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection.Emit;
using HarmonyLib;
using Medieval.GameSystems.DeathNotifications;
using Meds.Shared;
using Meds.Shared.Data;
using Meds.Wrapper.Audit;
using Microsoft.Extensions.DependencyInjection;
using Sandbox.Game.EntityComponents.Character;
using Sandbox.Game.GameSystems.Chat;
using Sandbox.Game.Players;
using VRage.Game.Entity;
using VRage.Utils;

namespace Meds.Wrapper.Shim
{
    [HarmonyPatch(typeof(MyDeathNotificationSystem), "OnEntityDied", typeof(MyEntity))]
    [AlwaysPatch]
    public static class DeathMessagePatch
    {
        public static void BroadcastMessageShim(MyChatSystem chat, MyStringHash channel, ulong sender, string message, MyEntity died)
        {
            chat.BroadcastMessage(channel, sender, message);

            var player = MyPlayers.Static?.GetControllingPlayer(died);
            if (player == null) return;

            var killer = MyPlayers.Static?.GetControllingPlayer(died?.Components.Get<MyCharacterDamageComponent>()?.LastDamage.Attacker);

            if (killer != null && killer != player)
            {
                // When players kill each other give it a unique message.
                var builder = MedsModApi.SendModEvent("meds.death.attributed", MedsAppPackage.Instance);
                builder.SetMessage(message);
                builder.Send();
            }
            else
            {
                // Otherwise use the same message.
                var builder = MedsModApi.SendModEvent("meds.death.self", MedsAppPackage.Instance);
                builder.SetReuseIdentifier($"meds-death-self-{player.Id.SteamId}", TimeSpan.FromHours(1));
                builder.SetMessage($"{message} (At {DateTime.UtcNow.AsDiscordTime()})");
                builder.Send();
            }
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
                    yield return new CodeInstruction(OpCodes.Ldarg_1);
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