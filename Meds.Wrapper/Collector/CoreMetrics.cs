using System;
using System.Diagnostics;
using Meds.Metrics;
using Meds.Wrapper.Metrics;
using Sandbox;
using Sandbox.Engine.Physics;
using Sandbox.Game.AI;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using VRage.Scene;

namespace Meds.Wrapper.Collector
{
    public static class CoreMetrics
    {
        private const string Prefix = "me.core";

        public static void Register()
        {
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
            group.Gauge("paused", () => MySandboxGame.IsPaused);
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