using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Medieval.World.Persistence;
using Meds.Metrics;
using Meds.Wrapper.Metrics;
using VRage.Scene;

// ReSharper disable InconsistentNaming

namespace Meds.Wrapper.Collector
{
    [HarmonyPatch]
    public static class GridDatabaseMetrics
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            return typeof(MyEntityGridDatabase).GetConstructors();
        }

        private const string Prefix = "me.persistence.griddatabase.";
        private const string DataTracker = Prefix + "tracker";

        public static void Postfix(object ___Chunks, object ___Entities, object ___Groups)
        {
            DataTrackerMetrics<ChunkId>("chunks", ___Chunks);
            DataTrackerMetrics<EntityId>("entities", ___Entities);
            DataTrackerMetrics<GroupId>("groups", ___Groups);
        }

        private static void DataTrackerMetrics<TKey>(string type, object tracker)
        {
            var loaded = (ICollection) AccessTools.Field(tracker.GetType(), "Loaded").GetValue(tracker);
            var toLoad = (IReadOnlyCollection<TKey>) AccessTools.Field(tracker.GetType(), "ToLoadNext").GetValue(tracker);

            var group = MetricRegistry.Group(MetricName.Of(DataTracker, "type", type));

            group.SetGauge("loaded", () => loaded.Count);
            group.SetGauge("loading", () => toLoad.Count);
        }
    }
}