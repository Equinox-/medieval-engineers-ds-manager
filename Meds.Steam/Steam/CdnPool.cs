using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Meds.Shared;
using Microsoft.Extensions.Logging;
using ZLogger;
using SteamKit2;
using SteamKit2.CDN;

namespace Meds.Watchdog.Steam
{
    public class CdnPool
    {
        private readonly ILogger<CdnPool> _log;
        private readonly SteamClient _client;
        private int _cellId;
        private readonly ConcurrentBag<Client> _clientBag = new ConcurrentBag<Client>();
        private List<Server> _servers;

        public CdnPool (ILogger<CdnPool> log, SteamClient client)
        {
            _log = log;
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
            Client.RequestTimeout = TimeSpan.FromSeconds(10);
            ServicePointManager.DefaultConnectionLimit = Math.Max(ServicePointManager.DefaultConnectionLimit, 100);
            _log.ZLogInformation($"Got {_servers.Count} CDN servers.");
        }
        
        public Client TakeClient()
        {
            if (_servers == null)
                return null;
            
            if (!_clientBag.TryTake(out var client))
            {
                client = new Client(_client);
            }
            
            return client;
        }

        public void ReturnClient(Client client)
        {
            _clientBag.Add(client);
        }

        public Server GetBestServer()
        {
            return _servers[0];
        }
    }
}