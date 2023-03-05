using System.IO;
using Meds.Shared;

namespace Meds.Watchdog
{
    public sealed class ConfigRenderer
    {
        private readonly Configuration _config;

        public ConfigRenderer(Configuration config)
        {
            _config = config;
        }

        public RenderResult Render()
        {
            var installConfig = RenderInstall();
            var installConfigPath = Path.Combine(_config.RuntimeDirectory, "install.xml");
            using (var stream = File.Create(installConfigPath))
            {
                RenderedInstallConfig.Serializer.Serialize(stream, installConfig);
            }

            return new RenderResult(installConfigPath);
        }

        public readonly struct RenderResult
        {
            public readonly string InstallConfigPath;

            public RenderResult(string installConfigPath)
            {
                InstallConfigPath = installConfigPath;
            }
        }

        private RenderedInstallConfig RenderInstall() => new RenderedInstallConfig
        {
            LogDirectory = _config.WrapperLogs,
            RuntimeDirectory = _config.RuntimeDirectory,
            Messaging = _config.Messaging,
            Metrics = _config.Metrics,
            Audit = _config.Audit,
            Adjustments = _config.Adjustments,
        };
    }
}