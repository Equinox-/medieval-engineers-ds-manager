using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Medieval.World.Persistence;
using Meds.Metrics;
using Sandbox.Game.World;
using VRage.Components.Session;
using VRage.Library.Collections;
using VRage.Scene;


namespace Meds.Wrapper.Metrics
{
    public static class GridDatabaseMetrics
    {
        private static readonly MethodInfo DatabaseProperty = AccessTools.PropertyGetter(typeof(MyInfiniteWorldPersistence), "Database");
        private static readonly FieldInfo ViewsField = AccessTools.Field(typeof(MyInfiniteWorldPersistence), "m_views");
        private static readonly FieldInfo ChunksField = AccessTools.Field(typeof(MyEntityGridDatabase), "Chunks");

        private static readonly FieldInfo EntitiesField = AccessTools.Field(typeof(MyEntityGridDatabase), "Entities");

        private static readonly FieldInfo GroupsField = AccessTools.Field(typeof(MyEntityGridDatabase), "Groups");

        public static void Register()
        {
            var iwp = MySession.Static.Components.Get<MyInfiniteWorldPersistence>();
            var db = (MyEntityGridDatabase) DatabaseProperty.Invoke(iwp, Array.Empty<object>());
            CoreMetrics(iwp, db);
            DataTrackerMetrics<ChunkId>("chunks", ChunksField.GetValue(db));
            DataTrackerMetrics<EntityId>("entities", EntitiesField.GetValue(db));
            DataTrackerMetrics<GroupId>("groups", GroupsField.GetValue(db));
        }

        private const string Prefix = "me.persistence.griddatabase.";
        private const string DataTracker = Prefix + "tracker";
        private const string Core = Prefix + "core";

        private static void CoreMetrics(MyInfiniteWorldPersistence iwp, MyEntityGridDatabase db)
        {
            var group = MetricRegistry.Group(MetricName.Of(Core));
            if (ViewsField.GetValue(iwp) is MyFreeList<MyInfiniteWorldPersistence.View> views) {
                group.Gauge("views", () => views.Count);
            }
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