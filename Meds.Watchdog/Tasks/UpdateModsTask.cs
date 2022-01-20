using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Meds.Watchdog.Steam;
using NLog;
using SteamKit2;
using SteamKit2.GC.Dota.Internal;

namespace Meds.Watchdog.Tasks
{
    public sealed class UpdateModsTask
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private readonly Program _program;

        public UpdateModsTask(Program program)
        {
            _program = program;
        }
        
        public async Task Execute(SteamDownloader downloader)
        {
            // Attempt to load mod list from world:
            var worldFile = Path.Combine(_program.RuntimeDirectory, "world", "Sandbox.sbc");
            if (!File.Exists(worldFile))
            {
                Log.Info("Skipping mod pre-loading because game folder can't be found.");
                return;
            }

            HashSet<ulong> mods;
            try
            {
                using (var stream = File.OpenRead(worldFile))
                {
                    var session = (SessionModHelper) SessionModHelper.Serializer.Deserialize(stream);
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
                var details = await downloader.LoadModDetails(UpdateTask.MedievalGameAppId, queued);
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
            var modDirectory = Path.Combine(_program.RuntimeDirectory, "workshop", "content", UpdateTask.MedievalGameAppId.ToString());

            var modInfo = await Task.WhenAll(mods.Select(modId => downloader.InstallModAsync(UpdateTask.MedievalGameAppId, modId,
                Path.Combine(modDirectory, modId.ToString()), 4, path => true, modNames[modId])));

            var manifest = new KeyValue("AppWorkshop");
            manifest.Children.Add(new KeyValue("appid", UpdateTask.MedievalGameAppId.ToString()));
            manifest.Children.Add(new KeyValue("SizeOnDisk", "0"));
            manifest.Children.Add(new KeyValue("NeedsUpdate", "0"));
            manifest.Children.Add(new KeyValue("NeedsDownload", "0"));
            manifest.Children.Add(new KeyValue("TimeLastUpdated", "0"));
            manifest.Children.Add(new KeyValue("TimeLastAppRan", "0"));
            var items = new KeyValue("WorkshopItemsInstalled");
            foreach (var mod in modInfo)
            {
                var item = new KeyValue(mod.published_file_id.ToString());
                item.Children.Add(new KeyValue("size", "0"));
                item.Children.Add(new KeyValue("timeupdated", mod.time_updated.ToString()));
                item.Children.Add(new KeyValue("manifest", mod.manifest_id.ToString()));
                items.Children.Add(item);
            }
            manifest.Children.Add(items);
            manifest.SaveToFile(Path.Combine(_program.RuntimeDirectory, "workshop", $"appworkshop_{UpdateTask.MedievalGameAppId}.acf"), false);
        }

        [XmlRoot("MyObjectBuilder_Checkpoint")]
        public sealed class SessionModHelper
        {
            public static readonly XmlSerializer Serializer = new XmlSerializer(typeof(SessionModHelper));

            [XmlArray("Mods")]
            [XmlArrayItem("Mod")]
            public Mod[] Mods = new Mod[0];

            public sealed class Mod
            {
                [XmlElement("PublishedFileId")]
                public ulong PublishedFileId;
            }
        }
    }
}