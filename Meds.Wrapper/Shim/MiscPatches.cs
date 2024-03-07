using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Medieval.Players;
using Meds.Wrapper.Utils;
using Sandbox.Game.Players;
using VRageMath;
using ZLogger;

// ReSharper disable InconsistentNaming

namespace Meds.Wrapper.Shim
{
    [HarmonyPatch(typeof(MyMedievalPlanetRespawnComponent), "RespawnInPlayerControlledEntity")]
    [AlwaysPatch]
    public static class RespawnInPlayerControlledEntity
    {
        private static readonly MethodInfo RealMethod = AccessTools.Method(typeof(MyMedievalPlanetRespawnComponent), "CorrectSpawnPosition");

        private static bool CorrectSpawnPosition(float charSizeRad, ref MatrixD position, int call, MyPlayer player)
        {
            var originalLocation = position.Translation;
            var args = new object[] { charSizeRad, position };
            var result = (bool) RealMethod.Invoke(null, args);
            position = (MatrixD)args[1];
            if (!result)
            {
                var okay = PositionPayload.TryCreate(originalLocation, out var positionPayload, player?.Identity?.Id);
                Entrypoint.LoggerFor(typeof(MyMedievalPlanetRespawnComponent))
                    .ZLogInformation(
                        "Failed to correct spawn location on call {0} while spawning {1} ({2}).  Originally at {3} ({4}/{5}/{6})",
                        call,
                        player?.Id.SteamId,
                        player?.Identity?.DisplayName,
                        originalLocation,
                        positionPayload.Face,
                        positionPayload.X,
                        positionPayload.Y);
            }
            else
            
            {
                var okay = PositionPayload.TryCreate(originalLocation, out var positionPayload, player?.Identity?.Id);
                Entrypoint.LoggerFor(typeof(MyMedievalPlanetRespawnComponent))
                    .ZLogInformation(
                        "Corrected spawn location on call {0} while spawning {1} ({2}).  Originally at {3} ({4}/{5}/{6}), moved {7}m",
                        call,
                        player?.Id.SteamId,
                        player?.Identity?.DisplayName,
                        originalLocation,
                        positionPayload.Face,
                        positionPayload.X,
                        positionPayload.Y,
                        Vector3D.Distance(originalLocation, position.Translation));
            }
            return result;
        }
        
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var call = 0;
            foreach (var instruction in instructions)
            {
                if (instruction.opcode == OpCodes.Call && instruction.operand is MethodInfo { Name: "CorrectSpawnPosition" })
                {
                    yield return new CodeInstruction(OpCodes.Ldc_I4, call++);
                    yield return new CodeInstruction(OpCodes.Ldarg_1);
                    yield return CodeInstruction.Call(typeof(RespawnInPlayerControlledEntity), nameof(CorrectSpawnPosition));
                } else {
                    yield return instruction;
                }
            }
        }
    }
}