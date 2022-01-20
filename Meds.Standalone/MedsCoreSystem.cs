using System.IO;
using Medieval;
using MedievalEngineersDedicated;
using Meds.Standalone.Collector;
using Meds.Standalone.Output;
using Meds.Standalone.Reporter;
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
            Influx = new Influx(Config.Influx);
            HealthReport = new HealthReport(Influx);
            MetricReport = new MetricReport(Influx);
            
            Patches.Patch();
            CoreMetrics.Register();
        }

        public void AfterMetadataInitialized()
        {
        }

        public Configuration Config { get; private set; }
        public Influx Influx { get; private set; }
        public HealthReport HealthReport { get; private set; }
        public MetricReport MetricReport { get; private set; }

        protected override void Shutdown()
        {
            HealthReport?.Dispose();
            HealthReport = null;
            MetricReport?.Dispose();
            MetricReport = null;
            Influx?.Dispose();
            Influx = null;
            base.Shutdown();
        }
    }
}