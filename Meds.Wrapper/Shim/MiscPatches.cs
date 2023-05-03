using HarmonyLib;
using Medieval.Players;
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
}