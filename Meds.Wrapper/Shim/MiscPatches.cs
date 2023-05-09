using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Medieval.Entities.Components;
using Medieval.ObjectBuilders.Session;
using Medieval.Players;
using Meds.Wrapper.Utils;
using Sandbox.Game.Players;
using Sandbox.Game.SessionComponents;
using VRage;
using VRage.Session;
using VRageMath;
using ZLogger;

// ReSharper disable InconsistentNaming

namespace Meds.Wrapper.Shim
{
    [HarmonyPatch(typeof(MyMedievalPlanetRespawnComponent), "CorrectSpawnPosition")]
    [AlwaysPatch]
    public static class RespawnReporting
    {
        public static void Prefix(ref MatrixD worldMatrix, out MatrixD __state)
        {
            __state = worldMatrix;
        }

        public static void Postfix(ref MatrixD worldMatrix, MatrixD __state, float characterSize)
        {
            if (worldMatrix.EqualsFast(ref __state))
                return;
            Entrypoint.LoggerFor(typeof(RespawnReporting))
                .ZLogInformation("Adjusted position when respawning, distance={0}, characterSize={1}, from={2}, to={3}",
                    Vector3D.Distance(worldMatrix.Translation, __state.Translation),
                    characterSize, __state.Translation, worldMatrix.Translation);
        }
    }

    [HarmonyPatch(typeof(MyPlayerStorageComponent), "RegeneratePlayerData")]
    [AlwaysPatch]
    public static class RegenPlayerData
    {
        public static void Postfix(MyObjectBuilder_PlayerStorage __result, MyPlayer player)
        {
            if (__result.Entity is { PositionAndOrientation: null })
            {
                Entrypoint.LoggerFor(typeof(MyPlayerStorageComponent))
                    .ZLogInformation("Player {0} ({1}) has entity data but does not have position data during save. Their entity is at {3}",
                        player.Id.SteamId, player.Identity?.DisplayName, player.Identity?.ControlledEntity?.GetPosition());
            }
        }
    }

    [HarmonyPatch(typeof(MyPlayerStorageComponent), "GetPlayerData")]
    [AlwaysPatch]
    public static class GetPlayerData
    {
        public static void Postfix(MyObjectBuilder_PlayerStorage __result, MyPlayer.PlayerId playerId)
        {
            var caller = StackUtils.CaptureGameLogicStackPayload();
            if (__result.Entity is { PositionAndOrientation: null })
            {
                var identity = MyPlayers.Static?.GetPlayer(playerId)?.Identity;
                Entrypoint.LoggerFor(typeof(MyPlayerStorageComponent))
                    .ZLogWarningWithPayload(caller, "Player {0} ({1}) has entity data but does not have position data during get. Their entity is at {3}",
                        playerId.SteamId, identity?.DisplayName, identity?.ControlledEntity?.GetPosition());
                return;
            }

            MySession.Static?.SystemUpdateScheduler.AddScheduledCallback(dt =>
            {
                if (__result.Entity is { PositionAndOrientation: null })
                {
                    var identity = MyPlayers.Static?.GetPlayer(playerId)?.Identity;
                    Entrypoint.LoggerFor(typeof(MyPlayerStorageComponent))
                        .ZLogWarningWithPayload(caller,
                            "Player {0} ({1}) lost their position after their data was returned. Their entity is at {3}",
                            playerId.SteamId, identity?.DisplayName, identity?.ControlledEntity?.GetPosition());
                }
            }, 1000);
        }
    }

    [HarmonyPatch(typeof(MyMedievalPlanetRespawnComponent), "GetSpawnPosition")]
    [AlwaysPatch]
    public static class PreferBoundRespawnFallback
    {
        private static IEnumerable<MyRespawnLocation> PreferBoundRespawns(IEnumerable<MyRespawnLocation> options)
        {
            return options.OrderBy(loc => loc is MyBindableRespawnLocation ? 0 : 1);
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var instruction in instructions)
            {
                yield return instruction;
                if (instruction.opcode == OpCodes.Call
                    && instruction.operand is MethodInfo { Name: "GetAllRespawns" } info
                    && info.ReturnType == typeof(IEnumerable<MyTuple<UniformRespawnProviderId, MyRespawnLocation>>))
                {
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(PreferBoundRespawnFallback), nameof(PreferBoundRespawns)));
                }
            }
        }
    }
}