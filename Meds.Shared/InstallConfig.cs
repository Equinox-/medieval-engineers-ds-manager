using System.Collections.Generic;
using System.Xml.Serialization;

namespace Meds.Shared
{
    [XmlRoot]
    public class RenderedInstallConfig
    {
        public static readonly XmlSerializer Serializer = new XmlSerializer(typeof(RenderedInstallConfig));

        [XmlElement]
        public string LogDirectory;
        
        [XmlElement]
        public string RuntimeDirectory;

        [XmlElement]
        public MessagePipe Messaging;

        [XmlElement]
        public MetricConfig Metrics;

        [XmlElement]
        public AuditConfig Audit;

        [XmlElement]
        public AdjustmentsConfig Adjustments;
    }

    public class AuditConfig
    {
    }

    public class AdjustmentsConfig
    {
        [XmlElement]
        public int? SyncDistance;

        [XmlElement]
        public bool? ModDebug;

        [XmlElement]
        public bool? ReplaceLogger;
    }

    public class MetricConfig
    {
        [XmlElement]
        public bool Prometheus = true;

        [XmlElement]
        public bool MethodProfiling = true;

        [XmlElement]
        public bool RegionProfiling = false;

        [XmlElement]
        public bool Network = true;

        [XmlElement]
        public bool Player = true;

        [XmlElement]
        public bool AllCraftingCategories = false;

        [XmlElement("CraftingCategory")]
        public List<string> CraftingCategories;

        [XmlElement]
        public bool AllCraftingComponents = false;

        [XmlElement("CraftingComponent")]
        public List<string> CraftingComponents;
    }
}