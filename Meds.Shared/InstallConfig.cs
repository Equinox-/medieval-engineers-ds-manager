using System.Xml.Serialization;

namespace Meds.Shared
{
    [XmlRoot]
    public class RenderedInstallConfig
    {
        public static readonly XmlSerializer Serializer = new XmlSerializer(typeof(RenderedInstallConfig));

        [XmlElement]
        public string RuntimeDirectory;

        [XmlElement]
        public MessagePipe Messaging;

        [XmlElement]
        public MetricConfig Metrics;

        [XmlElement]
        public AuditConfig Audit;

        public bool ReplaceLogger;
    }

    public class AuditConfig
    {
    }

    public class MetricConfig
    {
        [XmlElement]
        public bool Prometheus = true;

        [XmlElement]
        public bool MethodProfiling = true;

        [XmlElement]
        public bool RegionProfiling = true;

        [XmlElement]
        public bool Network = true;

        [XmlElement]
        public bool Player = true;
    }
}