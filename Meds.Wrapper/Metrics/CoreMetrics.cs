using System;
using System.Diagnostics;
using Medieval;
using Meds.Metrics;
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
            }
        }
    }
}