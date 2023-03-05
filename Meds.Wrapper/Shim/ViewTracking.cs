using System;
using System.Diagnostics;
using HarmonyLib;
using Medieval.World.Persistence;
using Microsoft.Extensions.Logging;
using VRage.Logging;
using ZLogger;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace Meds.Wrapper.Shim
{
    public static class ViewTracking
    {
        private static ILogger Log => Entrypoint.LoggerFor(typeof(ViewTracking));

        [HarmonyPatch(typeof(MyInfiniteWorldPersistence), "CreateView")]
        [AlwaysPatch(Late = false)]
        public static class ViewCreationPatch
        {
            public static void Postfix(ref int __result, MyInfiniteWorldPersistence.View viewData)
            {
                var range = viewData.PerLodRanges != null ? viewData.PerLodRanges[viewData.PerLodRanges.Length - 1] : viewData.Range.HalfExtents.Length();
                Log.ZLogInformation(
                    new Exception("Created here"),
                    "View={0} for Entity={1} with Range={2} created here",
                    __result,
                    viewData.Viewer,
                    range);
            }
        }

        [HarmonyPatch(typeof(MyInfiniteWorldPersistence), "DestroyView")]
        [AlwaysPatch(Late = false)]
        public static class ViewDestroyPatch
        {
            public static void Prefix(int viewId)
            {
                Log.ZLogInformation(
                    new Exception("Destroyed here"),
                    "View={0} destroyed here",
                    viewId);
            }
        }
    }
}