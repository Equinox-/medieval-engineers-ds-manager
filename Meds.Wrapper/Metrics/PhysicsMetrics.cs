using System;
using System.Text;
using Havok;
using Meds.Metrics;
using Meds.Metrics.Group;
using Sandbox.Engine.Physics;
using ZLogger;

namespace Meds.Wrapper.Metrics
{
    public static class PhysicsMetrics
    {
        private const string Prefix = "me.physics.";
        private const string WorldPrefix = Prefix + "world.";
        private const string Memory = "me.physics.memory";
        private static readonly Gauge Worlds;

        private static readonly PerWorldMetric RigidBodies;
        private static readonly PerWorldMetric ActiveRigidBodies;
        private static readonly PerWorldMetric CharacterRigidBodies;
        private static readonly PerWorldMetric ActiveIslands;
        private static readonly PerWorldMetric Constraints;

        private static readonly Gauge HeapUsage;
        private static readonly Gauge HeapAllocated;

        static PhysicsMetrics()
        {
            var group = MetricRegistry.Group(MetricName.Of(Prefix + "core"));
            Worlds = group.Gauge("worlds", 0.0);

            RigidBodies = new PerWorldMetric("rigid_bodies.total", group);
            ActiveRigidBodies = new PerWorldMetric("rigid_bodies.active", group);
            CharacterRigidBodies = new PerWorldMetric("rigid_bodies.character", group);
            ActiveIslands = new PerWorldMetric("islands.active", group);
            Constraints = new PerWorldMetric("constraints.total", group);

            var memory = MetricRegistry.Group(MetricName.Of(Memory));
            HeapUsage = memory.Gauge("heap.used", double.NaN);
            HeapAllocated = memory.Gauge("heap.allocated", double.NaN);
        }

        public static void Update()
        {
            var heapInfo = GetHavokMemoryInfo().Span;
            TryGetHavokLine(heapInfo, " used in main heap", out var heapUsage);
            TryGetHavokLine(heapInfo, "allocated by heap", out var heapAllocated);
            HeapUsage.SetValue(heapUsage ?? double.NaN);
            HeapAllocated.SetValue(heapAllocated ?? double.NaN);
            if (heapUsage > _lastHavokPeak) {
                if (_lastHavokPeak / HavokHeapInfoInterval != heapUsage.Value / HavokHeapInfoInterval)
                    Entrypoint.LoggerFor(typeof(PhysicsMetrics)).ZLogInformation("Havok memory state: {0}", heapInfo.ToString());
                _lastHavokPeak = heapUsage.Value;
            }

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

        private static long _lastHavokPeak;
        private const long HavokHeapInfoInterval = 16 * 1024 * 1024;

        private static readonly StringBuilder TempPhysicsStats = new StringBuilder();
        private static char[] _tempPhysicsStatsArray;

        private static ReadOnlyMemory<char> GetHavokMemoryInfo()
        {
            TempPhysicsStats.Clear();
            HkBaseSystem.GetMemoryStatistics(TempPhysicsStats);
            var len = TempPhysicsStats.Length;
            if (_tempPhysicsStatsArray == null || _tempPhysicsStatsArray.Length < len)
                Array.Resize(ref _tempPhysicsStatsArray, len);
            TempPhysicsStats.CopyTo(0, _tempPhysicsStatsArray, 0, len);
            return new ReadOnlyMemory<char>(_tempPhysicsStatsArray, 0, len);
        }

        private static void TryGetHavokLine(ReadOnlySpan<char> span, string tag, out long? parsed)
        {
            var usedInMainHeapTag = span.IndexOf(tag.AsSpan(), StringComparison.Ordinal);
            parsed = null;
            if (usedInMainHeapTag <= 0) return;
            span = span.Slice(0, usedInMainHeapTag);
            var lastNewLine = span.LastIndexOf('\n');
            if (lastNewLine < 0) return;
            span = span.Slice(lastNewLine).Trim();
            // long.TryParse(span) isn't in .NET 4.7
            var good = false;
            var usage = 0;
            foreach (var c in span)
            {
                if (c < '0' || c > '9')
                    break;
                usage = usage * 10 + (c - '0');
                good = true;
            }

            parsed = good ? (long?) usage : null;
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