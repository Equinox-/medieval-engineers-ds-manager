using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Serialization;

namespace Meds.Watchdog
{
    [XmlRoot]
    public class Configuration
    {
        public static readonly XmlSerializer Serializer = new XmlSerializer(typeof(Configuration));

        [XmlElement]
        public string EntryPoint;

        [XmlElement("Overlay")]
        public List<Overlay> Overlays = new List<Overlay>();

        [XmlElement]
        public string Directory;

        [XmlElement]
        public string ChannelName;

        /// <summary>
        /// Timeout for server shutdown, in seconds.
        /// </summary>
        [XmlElement]
        public double ShutdownTimeout = 60 * 5;

        /// <summary>
        /// Timeout for server liveness after startup, in seconds.
        /// </summary>
        [XmlElement]
        public double LivenessTimeout = 60;

        /// <summary>
        /// Timeout for server readiness after startup, in seconds.
        /// </summary>
        [XmlElement]
        public double ReadinessTimeout = 60 * 5;

        /// <summary>
        /// Timeout for server state
        /// </summary>
        [XmlElement]
        public double FailureTimeout = 60;

        [XmlElement("Influx")]
        public InfluxDb Influx;

        [XmlElement]
        public bool Prometheus = true;

        [XmlElement]
        public MetricSettings Metrics = new MetricSettings();

        [XmlElement]
        public AuditSettings Audit = new AuditSettings();

        public class MetricSettings
        {
            [XmlElement]
            public bool MethodProfiling = true;

            [XmlElement]
            public bool RegionProfiling = true;

            [XmlElement]
            public bool Network = true;

            [XmlElement]
            public bool Player = true;
        }

        public class AuditSettings
        {
        }

        public class Overlay
        {
            [XmlAttribute("Uri")]
            public string Uri;

            [XmlAttribute("Path")]
            public string Path = "";
        }

        public class InfluxDb
        {
            [XmlElement("Uri")]
            public string Uri;

            [XmlElement("Token")]
            public string Token;

            [XmlElement("Bucket")]
            public string Bucket;

            [XmlElement("Organization")]
            public string Organization;

            [XmlElement("DefaultTag")]
            public List<DefaultTagValue> DefaultTags;

            public class DefaultTagValue
            {
                [XmlAttribute("Tag")]
                public string Tag;

                [XmlAttribute("Value")]
                public string Value;
            }
        }

        public static Configuration Read(string path)
        {
            using (var stream = File.OpenRead(path))
            {
                return (Configuration) Serializer.Deserialize(stream);
            }
        }
    }
}