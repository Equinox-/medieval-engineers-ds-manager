using System;
using Medieval;
using MedievalEngineersDedicated;
using Meds.Wrapper.Config;
using Meds.Wrapper.Shim;
using Sandbox;
using VRage.Components;
using VRage.Engine;

namespace Meds.Wrapper
{
    [System("Wrapper System Late")]
    [MyDependency(typeof(MyMedievalDedicatedCompatSystem))]
    [MyForwardDependency(typeof(MyMedievalGame))]
    public sealed class LateInjectorSystem : EngineSystem
    {
        protected override void Init()
        {
            MySandboxGame.ConfigDedicated = new CustomConfig();
        }

        [FixedUpdate]
        public void EveryTick()
        {
            Program.Instance.HealthReport.LastGameTick = DateTime.UtcNow;
        }
    }
}