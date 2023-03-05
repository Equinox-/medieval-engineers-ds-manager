using System.Threading.Tasks;
using Meds.Shared;
using Meds.Watchdog.Steam;
using Microsoft.Extensions.DependencyInjection;
using SteamKit2;

namespace Meds.Watchdog.Utils
{
    public static class CiUtils
    {
        public static async Task RestoreGameBinaries(string target, string branch)
        {
            using var host = new HostBuilder()
                .ConfigureServices(svc => { svc.AddSteamDownloader(SteamConfiguration.Create(x => { })); })
                .Build();
            await host.StartAsync();
            var steam = host.Services.GetRequiredService<SteamDownloader>();
            await steam.LoginAsync();
            try
            {
                await steam.InstallAppAsync(Updater.MedievalDsAppId, Updater.MedievalDsDepotId, branch, target, 4,
                    path => path.StartsWith("DedicatedServer64"), "medieval-ds");
            }
            finally
            {
                await steam.LogoutAsync();
                await host.StopAsync();
            }
        }
    }
}