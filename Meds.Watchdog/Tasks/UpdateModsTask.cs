using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Meds.Watchdog.Steam;
using NLog;

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
            while (queued.Count > 0)
            {
                var details = await downloader.LoadModDetails(UpdateTask.MedievalGameAppId, queued);
                queued.Clear();
                foreach (var mod in details.Values)
                foreach (var child in mod.children)
                    if (mods.Add(child.publishedfileid))
                        queued.Enqueue(child.publishedfileid);
            }

            Log.Info($"{mods.Count} mods to pre-load");
            var modDirectory = Path.Combine(_program.RuntimeDirectory, "workshop", "content", UpdateTask.MedievalGameAppId.ToString());

            await Task.WhenAll(mods.Select(modId => downloader.InstallModAsync(UpdateTask.MedievalGameAppId, modId,
                Path.Combine(modDirectory, modId.ToString()), 4, UpdateTask.ShouldInstallAsset)));
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