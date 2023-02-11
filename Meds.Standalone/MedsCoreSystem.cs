using System.IO;
using Medieval;
using MedievalEngineersDedicated;
using Meds.Standalone.Metrics;
using Meds.Standalone.Output.Influx;
using Meds.Standalone.Output.Prometheus;
using Meds.Standalone.Shim;
using Meds.Watchdog;
using VRage.Components;
using VRage.Engine;
using VRage.FileSystem;

namespace Meds.Standalone
{
    [System("Wrapper System Early")]
    [MyDependency(typeof(MyMedievalDedicatedCompatSystem))]
    [MyForwardDependency(typeof(MyMedievalGame))]
    public sealed class MedsCoreSystem : EngineSystem, IInitBeforeMetadata
    {
        protected override void Init()
        {
            Config = Configuration.Read(Path.Combine(MyFileSystem.UserDataPath, "meds.cfg"));
            if (Config.Influx != null)
            {
                Influx = new Influx(Config.Influx);
                InfluxMetricReporter = new InfluxMetricReporter(Influx);
            }

            Patches.PatchAlways(false);
        }

        public void AfterMetadataInitialized()
        {
        }

        protected override void Start()
        {
            Patches.PatchAlways(true);
            if (Config.Prometheus)
                Patches.Patch(typeof(PrometheusPatch));
            if (Config.Metrics.Network)
                TransportLayerMetrics.Register();
            if (Config.Metrics.Player)
                PlayerMetrics.Register();
            UpdateSchedulerMetrics.Register(Config.Metrics.MethodProfiling, Config.Metrics.RegionProfiling);
            GridDatabaseMetrics.Register();
            CoreMetrics.Register();
        }

        [FixedUpdate]
        public void EveryTick()
        {
            PhysicsMetrics.Update();
            if (Config.Metrics.Player)
                PlayerMetrics.Update();
        }

        public Configuration Config { get; private set; }
        public Influx Influx { get; private set; }
        public InfluxMetricReporter InfluxMetricReporter { get; private set; }

        protected override void Shutdown()
        {
            InfluxMetricReporter?.Dispose();
            InfluxMetricReporter = null;
            Influx?.Dispose();
            Influx = null;
            base.Shutdown();
        }
    }
}