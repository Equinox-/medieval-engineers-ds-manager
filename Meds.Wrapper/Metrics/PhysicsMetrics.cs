using System;
using Havok;
using Meds.Metrics;
using Meds.Metrics.Group;
using Sandbox.Engine.Physics;

namespace Meds.Wrapper.Metrics
{
    public static class PhysicsMetrics
    {
        private const string Prefix = "me.physics.";
        private const string WorldPrefix = Prefix + "world.";
        private static readonly Gauge Worlds;

        private static readonly PerWorldMetric RigidBodies;
        private static readonly PerWorldMetric ActiveRigidBodies;
        private static readonly PerWorldMetric CharacterRigidBodies;
        private static readonly PerWorldMetric ActiveIslands;
        private static readonly PerWorldMetric Constraints;

        static PhysicsMetrics()
        {
            var group = MetricRegistry.Group(MetricName.Of(Prefix + "core"));
            Worlds = group.Gauge("worlds", 0.0);

            RigidBodies = new PerWorldMetric("rigid_bodies.total", group);
            ActiveRigidBodies = new PerWorldMetric("rigid_bodies.active", group);
            CharacterRigidBodies = new PerWorldMetric("rigid_bodies.character", group);
            ActiveIslands = new PerWorldMetric("islands.active", group);
            Constraints = new PerWorldMetric("constraints.total", group);
        }

        public static void Update()
        {
            try
            {
                var clustersOpt = MyPhysicsSandbox.GetClusterList();
                if (!clustersOpt.HasValue)
                    return;
                var clusters = clustersOpt.Value;
                Worlds.SetValue(clusters.Count);

                using (var rigidBodies = RigidBodies.Write())
                using (var activeBodies = ActiveRigidBodies.Write())
                using (var characterBodies = CharacterRigidBodies.Write())
                using (var activeIslands = ActiveIslands.Write())
                using (var constraints = Constraints.Write())
                    foreach (var cluster in clusters)
                    {
                        var world = (HkWorld) cluster;

                        rigidBodies.Record(world.RigidBodies.Count);
                        activeBodies.Record(world.ActiveRigidBodies.Count);
                        characterBodies.Record(world.CharacterRigidBodies.Count);
                        activeIslands.Record(world.GetActiveSimulationIslandsCount());
                        constraints.Record(world.GetConstraintCount());
                    }
            }
            catch
            {
                // ignored
            }
        }

        private sealed class PerWorldMetric
        {
            public readonly Gauge Sum;
            public readonly Histogram PerWorld;

            public PerWorldMetric(string name, MetricGroup group)
            {
                Sum = group.Gauge(name, 0.0);
                PerWorld = MetricRegistry.Histogram(MetricName.Of(WorldPrefix + name));
            }

            public PerWorldMetricToken Write()
            {
                return new PerWorldMetricToken(this);
            }
        }

        private struct PerWorldMetricToken : IDisposable
        {
            private long _sum;
            private readonly PerWorldMetric _metric;

            public PerWorldMetricToken(PerWorldMetric metric)
            {
                _sum = 0;
                _metric = metric;
            }

            public void Record(long value)
            {
                _sum += value;
                _metric.PerWorld.Record(value);
            }

            public void Dispose()
            {
                _metric.Sum.SetValue(_sum);
            }
        }
    }
}