using System;
using System.Collections.Generic;
using System.Xml.Serialization;
using Equ;

namespace Meds.Watchdog.GrafanaAgent
{
    public class GaConfig : MemberwiseEquatable<GaConfig>
    {
        [XmlAttribute]
        public bool Enabled;
        
        [XmlElement]
        public string Version = "v0.44.2";

        [XmlElement]
        public string TenantId;

        [XmlElement]
        public OAuthConfig OAuth;

        [XmlElement]
        public BasicAuthConfig BasicAuth;

        [XmlElement]
        public RemoteWriteConfig Prometheus;

        [XmlElement]
        public LokiWriteConfig Loki;

        [XmlElement("StaticTag")]
        public List<StaticTag> StaticTags = new List<StaticTag>();

        [XmlElement]
        public int HttpPort = 12345;

        [XmlElement]
        public int GrpcPort = 12346;

        public class StaticTag : MemberwiseEquatable<StaticTag>
        {
            [XmlAttribute]
            public string Key;

            [XmlAttribute]
            public string Value;
        }


        public class OAuthConfig : MemberwiseEquatable<OAuthConfig>
        {
            [XmlAttribute]
            public string Id;

            [XmlAttribute]
            public string Secret;

            [XmlAttribute]
            public string Scopes;

            [XmlAttribute]
            public string TokenUrl;
        }

        public class BasicAuthConfig : MemberwiseEquatable<BasicAuthConfig>
        {
            [XmlAttribute]
            public string Username;

            [XmlAttribute]
            public string Password;
        }

        public class RemoteWriteConfig : MemberwiseEquatable<RemoteWriteConfig>
        {
            [XmlAttribute]
            public string Url;

            [XmlAttribute]
            public string TenantId;

            [XmlAttribute]
            public string ScrapeInterval;

            [XmlElement]
            public OAuthConfig OAuth;

            [XmlElement]
            public BasicAuthConfig BasicAuth;
        }

        public class LokiWriteConfig : MemberwiseEquatable<LokiWriteConfig>
        {
            [XmlAttribute]
            public string Url;

            [XmlAttribute]
            public string TenantId;

            [XmlElement]
            public OAuthConfig OAuth;

            [XmlElement]
            public BasicAuthConfig BasicAuth;
        }
    }
}