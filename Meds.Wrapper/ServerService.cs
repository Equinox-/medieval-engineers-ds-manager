using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Meds.Shared;
using Meds.Shared.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sandbox;
using ZLogger;

namespace Meds.Wrapper
{
    public sealed class ServerService : IHostedService
    {
        private readonly Configuration _config;
        private readonly ISubscriber<ShutdownRequest> _shutdownSubscriber;
        private Task _runTask;
        private CancellationTokenSource _stoppingCts;
        private readonly IHostApplicationLifetime _lifetime;
        private readonly ILogger<ServerService> _log;

        public ServerService(Configuration config,
            ISubscriber<ShutdownRequest> shutdownSubscriber,
            IHostApplicationLifetime lifetime, ILogger<ServerService> log)
        {
            _config = config;
            _shutdownSubscriber = shutdownSubscriber;
            _lifetime = lifetime;
            _log = log;
        }


        private void Run()
        {
            var allArgs = new List<string>
            {
                "-console",
                "-ignorelastsession",
                "--data-path",
                _config.Install.RuntimeDirectory,
                "--system",
                $"{typeof(MedsCoreSystem).Assembly.FullName}:{typeof(MedsCoreSystem).FullName}",
            };
            // Don't use unique log names with the replacement logger is used.
            if (_config.Install.Adjustments.ReplaceLogger != true)
                allArgs.Add("--unique-log-names");

            var type = Type.GetType("MedievalEngineersDedicated.MyProgram, MedievalEngineersDedicated")
                       ?? throw new NullReferenceException("MyProgram is missing");
            var method = type.GetMethod("Main", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                         ?? throw new NullReferenceException("MyProgram#Main is missing");
            try
            {
                using (_shutdownSubscriber.Subscribe(HandleShutdownMessage))
                {
                    method.Invoke(null, new object[] { allArgs.ToArray() });
                }
            }
            finally
            {
                _lifetime.StopApplication();
            }
        }

        private void HandleShutdownMessage(ShutdownRequest msg)
        {
            var currentPid = Process.GetCurrentProcess().Id;
            if (msg.Pid == currentPid)
                _lifetime.StopApplication();
            else
                _log.ZLogInformation("Received shutdown request for different process {0}, this is {1}", msg.Pid, currentPid);
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _stoppingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _stoppingCts.Token.Register(MySandboxGame.ExitThreadSafe);

            _runTask = Task.Run(Run, _stoppingCts.Token);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _stoppingCts.Cancel();
            MySandboxGame.ExitThreadSafe();
            return _runTask;
        }
    }
}