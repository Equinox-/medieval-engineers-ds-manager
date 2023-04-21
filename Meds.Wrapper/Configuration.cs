using System.IO;
using Meds.Shared;

namespace Meds.Wrapper
{
    public class Configuration
    {
        public RenderedInstallConfig Install { get; }

        public ConfigRefreshable<RenderedRuntimeConfig> Runtime { get; }

        public MetricConfig Metrics => Install.Metrics;

        public Configuration(string installConfigPath, string runtimeConfigPath)
        {
            using (var installStream = File.OpenRead(installConfigPath))
                Install = (RenderedInstallConfig)RenderedInstallConfig.Serializer.Deserialize(installStream);
            Runtime = ConfigRefreshable<RenderedRuntimeConfig>.FromConfigFile(runtimeConfigPath, RenderedRuntimeConfig.Serializer);
        }
    }
}