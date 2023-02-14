using System.IO;
using Meds.Shared;

namespace Meds.Standalone
{
    public class Configuration
    {
        public RenderedInstallConfig Install { get; }

        public MetricConfig Metrics => Install.Metrics;

        public Configuration(string installConfigPath)
        {
            using (var installStream = File.OpenRead(installConfigPath))
            {
                Install = (RenderedInstallConfig)RenderedInstallConfig.Serializer.Deserialize(installStream);
            }
        }
    }
}