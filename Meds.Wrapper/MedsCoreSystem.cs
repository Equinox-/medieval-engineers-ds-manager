using System;
using System.Threading;
using Medieval;
using Medieval.GameSystems;
using Medieval.World.Persistence;
using MedievalEngineersDedicated;
using Meds.Shared;
using Meds.Shared.Data;
using Meds.Wrapper.Metrics;
using Meds.Wrapper.Output.Prometheus;
using Meds.Wrapper.Shim;
using Microsoft.Extensions.DependencyInjection;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Entities;
using VRage.Components;
using VRage.Components.Session;
using VRage.Engine;
using VRage.Network;
using VRageMath;
using MySession = Sandbox.Game.World.MySession;

namespace Meds.Wrapper
{
    public sealed class MedsCoreSystemArgs
    {
        public readonly Configuration Config;
        public readonly HealthReporter HealthReporter;

        public MedsCoreSystemArgs(Configuration config, HealthReporter healthReporter)
        {
            Config = config;
            HealthReporter = healthReporter;
        }
    }

    [System("Wrapper System Early")]
    [MyDependency(typeof(MyMedievalDedicatedCompatSystem))]
    [MyForwardDependency(typeof(MyMedievalGame))]
    public sealed class MedsCoreSystem : EngineSystem, IInitBeforeMetadata
    {
        private readonly MedsCoreSystemArgs _args = Entrypoint.Instance.Services.GetService<MedsCoreSystemArgs>();

        private Configuration Config => _args.Config;

        protected override void Init()
        {
            PatchHelper.PatchAlways(false);

            // https://communityedition.medievalengineers.com/mantis/view.php?id=432
            // Increase world download packet size to 512kB
            MyNetworkSettings.Static.WorldDownloadBlockSize = 512 * 1024;
        }

        public void AfterMetadataInitialized()
        {
        }

        protected override void Start()
        {
            PatchHelper.PatchAlways(true);
            if (Config.Metrics.PrometheusKey != null)
                PatchHelper.Patch(typeof(PrometheusPatch));
            if (Config.Metrics.Network)
                TransportLayerMetrics.Register();
            if (Config.Metrics.Player)
                PlayerMetrics.Register();
            CraftingMetrics.Register(Config.Metrics);
            UpdateSchedulerMetrics.Register(Config.Metrics.MethodProfiling, Config.Metrics.RegionProfiling);
            GridDatabaseMetrics.Register();
            CoreMetrics.Register();
            PaxMetrics.Register(Config.Metrics);
            MyMultiplayer.Static.ClientReady += id => Entrypoint.Instance?.Services.GetRequiredService<PlayerReporter>().HandlePlayerJoinedLeft(true, id);
            MyMultiplayer.Static.ClientLeft += (id, _) => Entrypoint.Instance?.Services.GetRequiredService<PlayerReporter>().HandlePlayerJoinedLeft(false, id);
            MyMultiplayer.Static.ViewDistance = Config.Install.Adjustments.SyncDistance ?? Math.Max(MySession.Static.Settings.ViewDistance, 100);
            AddScheduledCallback(UpdateDataStore);
        }

        [FixedUpdate]
        public void EveryTick()
        {
            _args.HealthReporter.OnTick();
        }

        [Update(1000)]
        public void UpdateMetrics(long dt)
        {
            WorkerMetrics.Update();
            PhysicsMetrics.Update();
            if (Config.Metrics.Player)
                PlayerMetrics.Update();
        }

        // Every 5 minutes
        [Update(5 * 60 * 1000)]
        public void UpdateDataStore(long dt)
        {
            var publisher = Entrypoint.Instance.Services.GetRequiredService<IPublisher<DataStoreSync>>();
            using var tok = publisher.Publish();
            var planet = MyGamePruningStructureSandbox.GetClosestPlanet(Vector3D.Zero);
            var areas = planet?.Get<MyPlanetAreasComponent>();
            var planetInfo = DataStorePlanet.CreateDataStorePlanet(
                tok.Builder,
                planet?.MinimumRadius ?? 0,
                planet?.AverageRadius ?? 0,
                planet?.MaximumRadius ?? 0,
                areas?.AreaCount ?? 0,
                areas?.AreasPerRegionCount ?? 0);

            var gridDb = MySession.Static.Components.Get<MyInfiniteWorldPersistence>()?.Settings;
            var gridDbInfo = DataStoreGridDatabase.CreateDataStoreGridDatabase(
                tok.Builder,
                gridDb?.MaxLod ?? 3,
                (float) (gridDb?.GridSize ?? 32)
            );
            tok.Send(DataStoreSync.CreateDataStoreSync(
                tok.Builder,
                planetInfo,
                gridDbInfo
            ));
        }
    }
}