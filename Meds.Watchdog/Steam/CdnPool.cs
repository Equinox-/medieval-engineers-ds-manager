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
        private readonly List<Server> _servers = new List<Server>();

        public CdnPool(ILogger<CdnPool> log, SteamClient client)
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
            Client.RequestTimeout = TimeSpan.FromSeconds(10);
            ServicePointManager.DefaultConnectionLimit = Math.Max(ServicePointManager.DefaultConnectionLimit, 100);
            await RefreshServers();
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

        private void SortServers()
        {
            _servers.Sort((a, b) => a.WeightedLoad.CompareTo(b.WeightedLoad));
        }

        private async Task RefreshServers()
        {
            var servers = await ContentServerDirectoryService
                .LoadAsync(_client.Configuration, _cellId, CancellationToken.None)
                .ConfigureAwait(false);
            foreach (var server in servers)
                if (!server.UseAsProxy && !server.SteamChinaOnly)
                    _servers.Add(server);
            _log.ZLogInformation($"Got {_servers.Count} CDN servers.");
            SortServers();
        }

        public async Task<Server> TakeServer()
        {
            if (_servers.Count == 0)
                await RefreshServers();
            var server = _servers[0];
            _servers.RemoveAt(0);
            _log.ZLogInformation("Using CDN server {0}", server.Host);
            return server;
        }

        public void ReturnServer(Server server)
        {
            _servers.Add(server);
            SortServers();
        }
    }
}