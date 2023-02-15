using System.Diagnostics;
using Havok;
using Medieval.GameSystems;
using Meds.Metrics;
using Sandbox.Game.Entities;
using VRage.Components.Physics;
using VRage.Physics;
using VRageMath;
using VRageMath.Spatial;

namespace Meds.Wrapper.Metrics
{
    public static class RegionMetrics
    {
        private const int UpdateRate = 5;

        private const string Prefix = "me.region.";
        private const string EntityPrefix = Prefix + "entity.";
        private const string EntityProfiler = EntityPrefix + "profiler";

        private const string PhysicsGroup = Prefix + "physics";
        private const string PhysicsProfiler = PhysicsGroup + ".profiler";

        public static MetricName CreateRegionMetric(string metric, long regionId, string key3 = null, string value3 = null)
        {
            MyPlanetAreasComponent.UnpackAreaId(regionId, out int face, out var x, out var y);
            return MetricName.Of(metric,
                "face", ZeroGcStrings.ToString(face),
                "regionX", ZeroGcStrings.ToString(x),
                "regionY", ZeroGcStrings.ToString(y),
                key3, value3);
        }

        public static long? GetRegionId(Vector3D pt)
        {
            var planet = MyGamePruningStructureSandbox.GetClosestPlanet(pt);
            var areas = planet?.Get<MyPlanetAreasComponent>();
            return areas?.GetRegion(pt - planet.GetPosition());
        }

        public static void RecordRegionUpdateTime(Vector3D pt, long dt)
        {
            var region = GetRegionId(pt);
            if (!region.HasValue) return;
            var regionName = CreateRegionMetric(EntityProfiler, region.Value);
            MetricRegistry.PerTickTimer(in regionName).Record(dt);
        }


        // [HarmonyPatch(typeof(MyPhysicsSandbox), "StepWorld")]
        public static class RegionalPhysicsProfiler
        {
            public static void Prefix(out long __state)
            {
                __state = Stopwatch.GetTimestamp();
            }

            public static void Postfix(long __state, HkWorld world)
            {
                var dt = Stopwatch.GetTimestamp() - __state;
                var active = world.RigidBodies;
                long? region = null;
                foreach (var body in active)
                    if (body.UserObject is MyPhysicsComponentBase phys && phys.ClusterObjectId != MyClusterTree.CLUSTERED_OBJECT_ID_UNITIALIZED)
                    {
                        region = GetRegionId(MyPhysics.GetObjectOffset(phys.ClusterObjectId));
                        break;
                    }

                if (!region.HasValue) return;
                var name = CreateRegionMetric(PhysicsProfiler, region.Value);
                var timer = MetricRegistry.PerTickTimer(name);
                timer.UpdateRate = UpdateRate;
                timer.Record(dt);
                var group = MetricRegistry.Group(name.WithSeries(PhysicsGroup));
                group.UpdateRate = UpdateRate;
                group.PerTickAdder("rigid_bodies.total").Add(world.RigidBodies.Count);
                group.PerTickAdder("rigid_bodies.active").Add(world.ActiveRigidBodies.Count);
                group.PerTickAdder("rigid_bodies.character").Add(world.CharacterRigidBodies.Count);
                group.PerTickAdder("islands.active").Add(world.GetActiveSimulationIslandsCount());
                group.PerTickAdder("constraints.total").Add(world.GetConstraintCount());
            }
        }
    }
}