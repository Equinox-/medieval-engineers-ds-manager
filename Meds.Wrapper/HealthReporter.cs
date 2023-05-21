using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Meds.Shared;
using Meds.Shared.Data;
using Microsoft.Extensions.Hosting;
using Sandbox.Engine.Physics;
using Sandbox.Game.Multiplayer;

namespace Meds.Wrapper
{
    public sealed class HealthReporter : BackgroundService
    {
        private static readonly TimeSpan ReportInterval = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(ReportInterval.TotalSeconds * 4);
        private DateTime _lastGameTick;

        private readonly IPublisher<HealthState> _healthPublisher;

        public HealthReporter(IPublisher<HealthState> healthPublisher)
        {
            _healthPublisher = healthPublisher;
        }

        public void OnTick()
        {
            _lastGameTick = DateTime.UtcNow;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var versionInfo = typeof(HealthReporter).Assembly.GetCustomAttribute<VersionInfoAttribute>();
                using (var builder = _healthPublisher.Publish())
                {
                    var gitHash = versionInfo != null ? builder.Builder.CreateString(versionInfo.GitHash) : default;
                    builder.Send(HealthState.CreateHealthState(builder.Builder,
                        liveness: true,
                        readiness: _lastGameTick + ReadinessTimeout >= DateTime.UtcNow,
                        sim_speed: MyPhysicsSandbox.SimulationRatio,
                        players: (Sync.Clients?.Count ?? 1) - 1,
                        version_hashOffset: gitHash,
                        version_compiled_at: versionInfo?.CompiledAt.Ticks ?? 0L
                    ));
                }

                await Task.Delay(ReportInterval, stoppingToken);
            }
        }
    }
}