using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Xml.Serialization;
using VRage.Game;

namespace Meds.Wrapper.Config
{
    [XmlRoot]
    public sealed class ConfigData
    {
        private static readonly XmlSerializer Serializer = new XmlSerializer(typeof(ConfigData));
        public static string ConfigPath => Path.Combine(Program.Instance.RuntimeDirectory, "server.xml");

        public static ConfigData Load()
        {
            var realPath = ConfigPath;
            if (!File.Exists(realPath))
            {
                var data = new ConfigData();
                data.Save();
                return data;
            }

            using (var stream = File.OpenRead(realPath))
                return (ConfigData) Serializer.Deserialize(stream);
        }

        public void Save()
        {
            using (var stream = File.Open(ConfigPath, FileMode.Create, FileAccess.Write))
                Serializer.Serialize(stream, this);
        }

        [XmlElement("Name")]
        public string Name = $"Meds-{Environment.MachineName.GetLongHashCode():X}";

        [XmlElement("IP")]
        public string IpAddress = "0.0.0.0";

        [XmlElement("SteamPort")]
        public int SteamPort = 8766;

        [XmlElement("ServerPort")]
        public int ServerPort = 27016;

        [XmlArray("Admins")]
        [XmlArrayItem("Admin")]
        public HashSet<ulong> Admins = new HashSet<ulong>();

        [XmlElement("PauseGameWhenEmpty")]
        public bool PauseGameWhenEmpty = false;

        [XmlArray("Bans")]
        [XmlArrayItem("Ban")]
        public HashSet<MyDedicatedBanData> BanData = new HashSet<MyDedicatedBanData>();

        [XmlElement("RemoteApi")]
        public RemoteApi Remote = new RemoteApi();

        [XmlElement("GroupID")]
        public ulong GroupId;

        public sealed class RemoteApi
        {
            [XmlAttribute("Enabled")]
            public bool Enabled = false;

            [XmlAttribute("Public")]
            public bool Public = false;

            [XmlAttribute("Port")]
            public int Port = 8080;

            [XmlAttribute("Key")]
            public string SecurityKey;
        }
    }
}