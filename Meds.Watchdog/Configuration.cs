using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;
using Equ;
using Meds.Dist;
using Meds.Shared;
using Meds.Watchdog.Discord;

namespace Meds.Watchdog
{
    [XmlRoot]
    public class Configuration : InstallConfiguration, IEquatable<Configuration>
    {
        public static readonly XmlSerializer Serializer = new XmlSerializer(typeof(Configuration));

        [XmlElement("Wrapper")]
        public List<OverlaySpec> WrapperLayers = new List<OverlaySpec>();

        [XmlElement("WrapperEntryPoint")]
        public string WrapperEntryPoint = "DedicatedServer64\\Meds.Wrapper.exe";

        /// <summary>
        /// Timeout for server shutdown, in seconds.
        /// </summary>
        [XmlElement]
        public double ShutdownTimeout = 60 * 10;

        /// <summary>
        /// Timeout for server liveness after startup, in seconds.
        /// </summary>
        [XmlElement]
        public double LivenessTimeout = 60;

        /// <summary>
        /// Timeout for server readiness after startup, in seconds.
        /// </summary>
        [XmlElement]
        public double ReadinessTimeout = 60 * 15;

        /// <summary>
        /// In-game channel to send status change messages to. 
        /// </summary>
        [XmlElement]
        public string StatusChangeChannel = "System";

        [XmlElement]
        public SteamConfig Steam = new SteamConfig();

        [XmlElement]
        public MetricConfig Metrics = new MetricConfig();

        [XmlElement]
        public AdjustmentsConfig Adjustments = new AdjustmentsConfig();

        [XmlElement]
        public AuditConfig Audit = new AuditConfig();

        [XmlElement]
        public DiscordConfig Discord = new DiscordConfig();

        [XmlElement("ScheduledTask")]
        public List<ScheduledTaskConfig> ScheduledTasks;

        public class ScheduledTaskConfig : MemberwiseEquatable<ScheduledTaskConfig>
        {
            [XmlAttribute("Target")]
            public LifecycleStateCase Target = LifecycleStateCase.Faulted;

            private void MaybeSetState(bool arg, LifecycleStateCase val)
            {
                if (arg)
                    Target = val;
                else if (Target == val)
                    Target = LifecycleStateCase.Faulted;
            }

            [XmlAttribute("Shutdown")]
            public bool Shutdown
            {
                get => Target == LifecycleStateCase.Shutdown;
                set => MaybeSetState(value, LifecycleStateCase.Shutdown);
            }

            [XmlAttribute("Start")]
            public bool Start
            {
                get => Target == LifecycleStateCase.Running;
                set => MaybeSetState(value, LifecycleStateCase.Running);
            }

            [XmlAttribute("Restart")]
            public bool Restart
            {
                get => Target == LifecycleStateCase.Restarting;
                set => MaybeSetState(value, LifecycleStateCase.Restarting);
            }

            [XmlAttribute]
            public bool Utc;

            [XmlAttribute("Cron")]
            public string Cron;

            [XmlAttribute("Reason")]
            public string Reason;
        }

        public class SteamConfig : MemberwiseEquatable<SteamConfig>
        {
            [XmlAttribute]
            public string Branch = "communityedition";

            [XmlAttribute]
            public string BranchPassword;
        }

        private static readonly MemberwiseEqualityComparer<Configuration> EqualityComparer = MemberwiseEqualityComparer<Configuration>.ByFields;

        public bool Equals(Configuration other) => EqualityComparer.Equals(this, other);

        public override int GetHashCode() => EqualityComparer.GetHashCode(this);

        public override bool Equals(object obj) => obj is Configuration cfg && Equals(cfg);

        public static Configuration Read(string path)
        {
            using var stream = File.OpenRead(path);
            var cfg = (Configuration)Serializer.Deserialize(stream);
            cfg.OnLoaded(path);
            return cfg;
        }
    }
}