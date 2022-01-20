using System;
using Medieval;
using MedievalEngineersDedicated;
using Meds.Standalone.Collector;
using Meds.Standalone.Shim;
using Sandbox;
using VRage.Components;
using VRage.Engine;

namespace Meds.Standalone
{
    [System("Wrapper System Late")]
    [MyDependency(typeof(MyMedievalDedicatedCompatSystem))]
    [MyDependency(typeof(MedsCoreSystem))]
    [MyForwardDependency(typeof(MyMedievalGame))]
    public sealed class MedsLateSystem : EngineSystem
    {
        [Automatic]
        private MedsCoreSystem _core; 
        
        protected override void Init()
        {
            TransportLayerMetrics.Register();
            Patches.PatchLate();
        }

        [FixedUpdate]
        public void EveryTick()
        {
            _core.HealthReport.LastGameTick = DateTime.UtcNow;
            PhysicsMetrics.Update();
        }
    }
}