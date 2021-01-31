using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using SteamKit2;

namespace Meds.Watchdog.Steam
{
    public class CdnPool
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();
        
        private readonly SteamClient _client;
        private int _cellId;
        private readonly ConcurrentBag<CDNClient> _clientBag = new ConcurrentBag<CDNClient>();
        private List<CDNClient.Server> _servers;
        
        public CdnPool(SteamClient client)
        {
            _client = client;
        }

        /// <summary>
        /// Initializes stuff needed to download content from the Steam content servers.
        /// </summary>
        /// <returns></returns>
        public async Task Initialize(int cellId)
        {
            _cellId = cellId;
            _servers = (await ContentServerDirectoryService.LoadAsync(_client.Configuration, _cellId, CancellationToken.None)
                                                          .ConfigureAwait(false)).OrderBy(x => x.WeightedLoad).ToList();
            CDNClient.RequestTimeout = TimeSpan.FromSeconds(10);
            ServicePointManager.DefaultConnectionLimit = Math.Max(ServicePointManager.DefaultConnectionLimit, 100);
            Log.Info($"Got {_servers.Count} CDN servers.");
        }
        
        public CDNClient TakeClient()
        {
            if (_servers == null)
                return null;
            
            if (!_clientBag.TryTake(out var client))
            {
                client = new CDNClient(_client);
            }
            
            return client;
        }

        public void ReturnClient(CDNClient client)
        {
            _clientBag.Add(client);
        }

        public CDNClient.Server GetBestServer()
        {
            return _servers[0];
        }
    }
}