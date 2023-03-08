using HarmonyLib;
using Medieval.World.Persistence;
using Meds.Wrapper.Utils;
using VRage.Game.Entity;
using ZLogger;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace Meds.Wrapper.Shim
{
    public static class ViewTracking
    {
        private static ILogger Log => Entrypoint.LoggerFor(typeof(ViewTracking));

        public struct ViewHere
        {
            public string StackTrace;
        }

        [HarmonyPatch(typeof(MyInfiniteWorldPersistence), "CreateView")]
        [AlwaysPatch(Late = false)]
        public static class ViewCreationPatch
        {
            public static void Postfix(MyInfiniteWorldPersistence __instance, ref int __result, MyInfiniteWorldPersistence.View viewData)
            {
                var range = viewData.PerLodRanges != null ? viewData.PerLodRanges[viewData.PerLodRanges.Length - 1] : viewData.Range.HalfExtents.Length();
                MyEntity entity = null;
                __instance.Session?.Scene?.TryGetEntity(viewData.Viewer, out entity);
                Log.ZLogInformationWithPayload(
                    new ViewHere { StackTrace = StackUtils.CaptureGameLogicStack() },
                    "View={0} for Entity={1}({2}) with Range={3} created here",
                    __result,
                    viewData.Viewer,
                    entity?.DefinitionId?.ToString(),
                    range);
            }
        }

        [HarmonyPatch(typeof(MyInfiniteWorldPersistence), "DestroyView")]
        [AlwaysPatch(Late = false)]
        public static class ViewDestroyPatch
        {
            public static void Prefix(int viewId)
            {
                Log.ZLogInformationWithPayload(
                    new ViewHere { StackTrace = StackUtils.CaptureGameLogicStack() },
                    "View={0} destroyed here",
                    viewId);
            }
        }
    }
}