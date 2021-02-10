using Medieval;
using MedievalEngineersDedicated;
using Meds.Wrapper.Collector;
using Meds.Wrapper.Shim;
using VRage.Components;
using VRage.Engine;

namespace Meds.Wrapper
{
    [System("Wrapper System Early")]
    [MyDependency(typeof(MyMedievalDedicatedCompatSystem))]
    [MyForwardDependency(typeof(MyMedievalGame))]
    public sealed class EarlyInjectorSystem : EngineSystem, IInitBeforeMetadata
    {
        protected override void Init()
        {
            Patches.Patch();
            ShimLog.Hook();
            CoreMetrics.Register();
        }

        public void AfterMetadataInitialized()
        {
        }
    }
}