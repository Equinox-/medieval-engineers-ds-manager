using System.Threading.Tasks;
using Meds.Watchdog.Steam;
using Meds.Watchdog.Tasks;
using SteamKit2;

namespace Meds.Watchdog
{
    public static class CiUtils
    {
        public static async Task RestoreGameBinaries(string target)
        {
            var downloader = new SteamDownloader(SteamConfiguration.Create(x => { }));
            await downloader.LoginAsync();
            try
            {
                await downloader.InstallAppAsync(UpdateTask.MedievalDsAppId, UpdateTask.MedievalDsDepotId, "public", target, 4,
                    path => path.StartsWith("DedicatedServer64"), "medieval-ds");
            }
            finally
            {
                await downloader.LogoutAsync();
            }
        }
    }
}