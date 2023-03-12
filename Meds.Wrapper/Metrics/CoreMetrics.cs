using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Havok;
using Medieval;
using Meds.Metrics;
using Meds.Metrics.Group;
using Sandbox;
using Sandbox.Engine.Physics;
using Sandbox.Game.AI;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using VRage.Scene;

namespace Meds.Wrapper.Metrics
{
    public static class CoreMetrics
    {
        private const string Prefix = "me.core";
        private const string Gc = "me.core.gc";
        private const string VersionData = "me.core.version";

        public static void Register()
        {
            var versionData = MetricRegistry.Group(MetricName.Of(VersionData, 
                "version_full", MyMedievalGame.VersionString,
                "version_release", MyMedievalGame.ME_VERSION.ToString(3)));
            versionData.Gauge("value", () => 1);
            
            var group = MetricRegistry.Group(MetricName.Of(Prefix));

            var process = Process.GetCurrentProcess();
            MetricRegistry.RegisterFlushAction(() => process.Refresh());

            group.Gauge("cores", () => MySandboxGame.NumberOfCores);
            group.Gauge("players", () => Sync.Clients != null ? Math.Max(0, Sync.Clients.Count - 1) : double.NaN);
            group.Gauge("sim_speed", () => MyPhysicsSandbox.SimulationRatio);
            group.Gauge("cpu_load", () => MySandboxGame.Static?.CPULoadSmooth);
            group.Gauge("entities", () => MySession.Static?.Components?.Get<MySceneComponent>()?.Scene?.Entities.Count);
            group.Gauge("time", () => MySandboxGame.Static?.UpdateTime.Seconds);
            group.Gauge("bots", () => MyAIComponent.Static?.Bots?.TotalBotCount);
            group.Gauge("paused", () => Sync.Clients != null && MySandboxGame.IsPaused);
            group.Gauge("threads", () => process.Threads.Count);

            {
                var memoryGroup = MetricRegistry.Group(MetricName.Of(Prefix + ".memory"));
                memoryGroup.Gauge("managed", () => GC.GetTotalMemory(false));
                memoryGroup.Gauge("private", () => process.PrivateMemorySize64);
                memoryGroup.Gauge("paged", () => process.PagedMemorySize64);
                memoryGroup.Gauge("working_set", () => process.WorkingSet64);
                memoryGroup.Gauge("virtual_memory", () => process.VirtualMemorySize64);
                _havokHeapUsage = memoryGroup.Gauge("physics", double.NaN);
            }

            for (var i = 0; i <= GC.MaxGeneration; i++)
            {
                var gen = i;
                var gcGroup = MetricRegistry.Group(MetricName.Of(Gc, "generation", ZeroGcStrings.ToString(gen)));
                gcGroup.Gauge("count", () => GC.CollectionCount(gen));
            }
        }

        private static Gauge _havokHeapUsage;

        public static void UpdateHavokHeapUsage()
        {
            if (_havokHeapUsage == null)
                return;
            if (TryGetHavokHeapUsage(out var usage))
                _havokHeapUsage.SetValue(usage);
            else
                _havokHeapUsage.SetValue(double.NaN);
        }

        private const string HeapUsageTag = " used in main heap";
        private static readonly StringBuilder TempPhysicsStats = new StringBuilder();
        private static char[] _tempPhysicsStatsArray;

        private static bool TryGetHavokHeapUsage(out long usage)
        {
            TempPhysicsStats.Clear();
            HkBaseSystem.GetMemoryStatistics(TempPhysicsStats);
            var len = TempPhysicsStats.Length;
            if (_tempPhysicsStatsArray == null || _tempPhysicsStatsArray.Length < len)
                Array.Resize(ref _tempPhysicsStatsArray, len);
            TempPhysicsStats.CopyTo(0, _tempPhysicsStatsArray, 0, len);
            var span = new ReadOnlySpan<char>(_tempPhysicsStatsArray, 0, len);
            var usedInMainHeapTag = span.IndexOf(HeapUsageTag.AsSpan(), StringComparison.Ordinal);
            usage = 0;
            if (usedInMainHeapTag <= 0) return false;
            span = span.Slice(0, usedInMainHeapTag);
            var lastNewLine = span.LastIndexOf('\n');
            if (lastNewLine < 0) return false;
            span = span.Slice(lastNewLine).Trim();
            // long.TryParse(span) isn't in .NET 4.7
            var good = false;
            foreach (var c in span)
            {
                if (c < '0' || c > '9')
                    break;
                usage = usage * 10 + (c - '0');
                good = true;
            }

            return good;
        }
    }
}