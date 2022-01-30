using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Serialization;
using CommandLine;
using Meds.Watchdog.Steam;
using NLog;
using NLog.Config;
using NLog.Targets;
using SteamKit2;

namespace Meds.SetupTool
{
    public class Program
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public class Options
        {
            [Option("mod-install-dir", HelpText = "Directory to install mods into")]
            public string ModInstallDir { get; set; }

            [Value(0, MetaName = "dedicated-server-dir", HelpText = "Dedicated server installation directory")]
            public string InstallDir { get; set; }

            [Value(1, MetaName = "sandbox-sbc", HelpText = "Sandbox SBC file")]
            public string WorldFile { get; set; }


            [Option("skip-dedicated-server", HelpText = "Skip updating the dedicated server")]
            public bool SkipDedicatedServer { get; set; }
        }

        public static async Task Main(string[] args)
        {
            var config = new LoggingConfiguration();
            var consoleTarget = new ConsoleTarget("console")
            {
                Layout = "${shortdate} ${level} ${message}  ${exception} ${event-properties}"
            };
            config.AddRuleForAllLevels(consoleTarget);
            LogManager.Configuration = config;
            await Parser.Default.ParseArguments<Options>(args).WithParsedAsync(Run);
        }

        private static async Task Run(Options opts)
        {
            var downloader = new SteamDownloader(SteamConfiguration.Create(x => { }));
            await downloader.LoginAsync();
            try
            {
                if (!opts.SkipDedicatedServer)
                    await InstallDs(downloader, opts);
                await InstallMods(downloader, opts);
            }
            finally
            {
                await downloader.LogoutAsync();
            }
        }

        public const uint MedievalDsAppId = 367970;
        public const uint MedievalDsDepotId = 367971;
        public const uint MedievalGameAppId = 333950;

        private static async Task InstallDs(SteamDownloader downloader, Options opts)
        {
            Log.Info("Updating Medieval Engineers");
            await downloader.InstallAppAsync(MedievalDsAppId, MedievalDsDepotId, "public", opts.InstallDir, 4,
                path => true, "medieval-ds");
        }


        public static async Task InstallMods(SteamDownloader downloader, Options opts)
        {
            // Attempt to load mod list from world:
            if (string.IsNullOrEmpty(opts.WorldFile) || !File.Exists(opts.WorldFile))
            {
                Log.Info("Skipping mod pre-loading because game folder can't be found.");
                return;
            }

            HashSet<ulong> mods;
            try
            {
                using (var stream = File.OpenRead(opts.WorldFile))
                {
                    var session = (SessionModHelper)SessionModHelper.Serializer.Deserialize(stream);
                    mods = new HashSet<ulong>(session.Mods.Select(x => x.PublishedFileId).Where(x => x != 0));
                }
            }
            catch (Exception err)
            {
                Log.Info("Skipping mod pre-loading because save file couldn't be read: " + err);
                return;
            }

            var queued = new Queue<ulong>(mods);
            var modNames = new Dictionary<ulong, string>();
            while (queued.Count > 0)
            {
                var details = await downloader.LoadModDetails(MedievalGameAppId, queued);
                queued.Clear();
                foreach (var mod in details.Values)
                {
                    modNames[mod.publishedfileid] = mod.title;
                    foreach (var child in mod.children)
                        if (mods.Add(child.publishedfileid))
                            queued.Enqueue(child.publishedfileid);
                }
            }

            Log.Info($"{mods.Count} mods to pre-load");
            var modDirectory = opts.ModInstallDir ??
                               Path.Combine(opts.InstallDir, "workshop", "content", MedievalGameAppId.ToString());

            await Task.WhenAll(mods.Select(modId => downloader.InstallModAsync(MedievalGameAppId, modId,
                Path.Combine(modDirectory, modId.ToString()), 4, path => true, modNames[modId])));
        }

        [XmlRoot("MyObjectBuilder_Checkpoint")]
        public sealed class SessionModHelper
        {
            public static readonly XmlSerializer Serializer = new XmlSerializer(typeof(SessionModHelper));

            [XmlArray("Mods")] [XmlArrayItem("Mod")]
            public Mod[] Mods = new Mod[0];

            public sealed class Mod
            {
                [XmlElement("PublishedFileId")] public ulong PublishedFileId;
            }
        }
    }
}