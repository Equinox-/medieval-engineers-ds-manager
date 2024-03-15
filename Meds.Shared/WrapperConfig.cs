using System;
using System.Collections.Generic;
using System.Xml.Serialization;
using Equ;

namespace Meds.Shared
{
    [XmlRoot]
    public class RenderedInstallConfig : MemberwiseEquatable<RenderedInstallConfig>
    {
        public static readonly XmlSerializer Serializer = new XmlSerializer(typeof(RenderedInstallConfig));

        [XmlElement]
        public string LogDirectory;

        [XmlElement]
        public string RuntimeDirectory;

        [XmlElement]
        public string DiagnosticsDirectory;

        [XmlElement]
        public MessagePipe Messaging;

        [XmlElement]
        public MetricConfig Metrics = new MetricConfig();

        [XmlElement]
        public AdjustmentsConfig Adjustments = new AdjustmentsConfig();
    }

    [XmlRoot]
    public sealed class RenderedRuntimeConfig : MemberwiseEquatable<RenderedRuntimeConfig>
    {
        public static readonly XmlSerializer Serializer = new XmlSerializer(typeof(RenderedRuntimeConfig));

        [XmlElement]
        public AuditConfig Audit = new AuditConfig();

        [XmlElement]
        public BackupConfig Backup = new BackupConfig();
    }

    public class AuditConfig : MemberwiseEquatable<AuditConfig>
    {
    }

    public class AdjustmentsConfig : MemberwiseEquatable<AdjustmentsConfig>
    {
        [XmlElement]
        public int? SyncDistance;

        [XmlElement]
        public bool? ModDebug;

        [XmlElement]
        public bool? ReplaceLogger;

        [XmlElement("SuppressPatch")]
        public List<string> SuppressPatch = new List<string>();

        [XmlElement("RequestPatch")]
        public List<string> RequestPatch = new List<string>();
    }

    public class BackupConfig : MemberwiseEquatable<BackupConfig>
    {
        [XmlElement]
        public bool DefaultTiers;

        [XmlElement("Tier")]
        public List<BackupTierConfig> Tiers = new List<BackupTierConfig>();
    }

    public class BackupTierConfig : MemberwiseEquatable<BackupTierConfig>
    {
        [XmlIgnore]
        public TimeSpan Interval => TimeSpan.FromDays(Days) + TimeSpan.FromHours(Hours) + TimeSpan.FromMinutes(Minutes);

        [XmlAttribute]
        public double Minutes;

        [XmlAttribute]
        public double Hours;

        [XmlAttribute]
        public double Days;

        [XmlAttribute]
        public int Count;
    }

    public class MetricConfig : MemberwiseEquatable<MetricConfig>
    {
        [XmlElement]
        public string PrometheusKey;

        [XmlElement]
        public bool MethodProfiling = true;

        [XmlElement]
        public bool RpcProfiling = true;

        [XmlElement]
        public bool DefinitionProfiling = true;

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