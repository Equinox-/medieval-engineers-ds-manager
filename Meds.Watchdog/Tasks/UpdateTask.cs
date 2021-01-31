using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Meds.Watchdog.Steam;
using SteamKit2;

namespace Meds.Watchdog.Tasks
{
    public sealed class UpdateTask : ITask
    {
        public const uint MedievalDsAppId = 367970;
        public const uint MedievalDsDepotId = 367971;
        public const uint MedievalGameAppId = 333950;


        private readonly UpdateInstallTask _install;
        private readonly UpdateModsTask _mods;

        public UpdateTask(Program program)
        {
            _install = new UpdateInstallTask(program);
            _mods = new UpdateModsTask(program);
        }

        public async Task Execute()
        {
            var downloader = new SteamDownloader(SteamConfiguration.Create(x => { }));
            await downloader.LoginAsync();
            try
            {
                await _install.Execute(downloader);
                await _mods.Execute(downloader);
            }
            finally
            {
                await downloader.LogoutAsync();
            }
        }

        private static readonly IEnumerable<string> ExcludeExtensions = new[]
        {
            ".dds",
            ".png",
            ".jpg",
            ".jpeg",
            ".blend"
        };


        public static bool ShouldInstallAsset(string asset)
        {
            if (asset.StartsWith("Content"))
                return true;
            // Exclude image shaped files outside of the content directory.  These are normally mod textures.
            foreach (var ext in ExcludeExtensions)
                if (asset.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                    return false;
            return true;
        }
    }
}