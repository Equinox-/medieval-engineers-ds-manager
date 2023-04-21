using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Meds.Shared;
using Meds.Watchdog.Utils;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace Meds.Watchdog
{
    public sealed class ConfigRenderer : IHostedService
    {
        private readonly InstallConfiguration _installConfig;
        private readonly Refreshable<RenderedInstallConfig> _renderedInstallConfig;
        private readonly Refreshable<RenderedRuntimeConfig> _renderedRuntimeConfig;
        private readonly ILogger<ConfigRenderer> _log;

        public ConfigRenderer(InstallConfiguration installConfig, Refreshable<Configuration> config, ILogger<ConfigRenderer> log)
        {
            _installConfig = installConfig;
            InstallConfigFile = Path.Combine(installConfig.RuntimeDirectory, "install.xml");
            RuntimeConfigFile = Path.Combine(installConfig.RuntimeDirectory, "runtime.xml");
            _renderedInstallConfig = config.Map(RenderInstall);
            _renderedRuntimeConfig = config.Map(RenderRuntime);
            _log = log;
        }

        public string InstallConfigFile { get; }
        public string RuntimeConfigFile { get; }

        private IDisposable _installToken;
        private IDisposable _runtimeToken;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _installToken = _renderedInstallConfig
                .Subscribe(cfg => WriteConfig(InstallConfigFile, cfg, RenderedInstallConfig.Serializer));
            _runtimeToken = _renderedRuntimeConfig
                .Subscribe(cfg => WriteConfig(RuntimeConfigFile, cfg, RenderedRuntimeConfig.Serializer));
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _installToken.Dispose();
            _runtimeToken.Dispose();
            return Task.CompletedTask;
        }

        private void WriteConfig(string target, object obj, XmlSerializer serializer)
        {
            try
            {
                FileUtils.WriteAtomic(target, obj, serializer);
                _log.ZLogInformation("Updated config file {0}", Path.GetFileName(target));
            }
            catch (Exception err)
            {
                _log.ZLogInformation(err, "Failed to update config file {0}", Path.GetFileName(target));
            }
        }

        private RenderedInstallConfig RenderInstall(Configuration cfg) => new RenderedInstallConfig
        {
            LogDirectory = _installConfig.WrapperLogs,
            RuntimeDirectory = _installConfig.RuntimeDirectory,
            Messaging = cfg.Messaging,
            Metrics = cfg.Metrics,
            Adjustments = cfg.Adjustments,
        };

        private RenderedRuntimeConfig RenderRuntime(Configuration cfg) => new RenderedRuntimeConfig
        {
            Audit = cfg.Audit,
        };
    }
}