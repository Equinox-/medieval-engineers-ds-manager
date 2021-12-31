using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Meds.Watchdog.Utils;
using NLog;
using NLog.Fluent;
using ProtoBuf;
using SteamKit2;
using SteamKit2.Internal;
using static SteamKit2.SteamClient;
using static SteamKit2.SteamUser;
using static SteamKit2.SteamApps;
using static SteamKit2.SteamApps.PICSProductInfoCallback;
using FileInfo = Meds.Watchdog.Utils.FileInfo;
using Timer = System.Timers.Timer;

namespace Meds.Watchdog.Steam
{
    public class SteamDownloader
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        internal const string CacheDir = ".sdcache";
        private const string LockFile = CacheDir + "\\lock";

        private readonly SteamClient _client;
        private readonly SteamUser _user;
        private readonly SteamApps _apps;
        private readonly SteamCloud _cloud;
        private readonly SteamUnifiedMessages _unifiedMessages;
        private readonly CallbackPump _callbacks;
        private readonly SteamUnifiedMessages.UnifiedService<IPublishedFile> _publishedFiles;

        private LoggedOnCallback _loginDetails;

        private readonly ConcurrentDictionary<uint, byte[]> _depotKeys = new ConcurrentDictionary<uint, byte[]>();

        private readonly ConcurrentDictionary<string, CDNAuthTokenCallback> _cdnAuthTokens =
            new ConcurrentDictionary<string, CDNAuthTokenCallback>();

        private readonly ConcurrentDictionary<uint, PICSProductInfo> _appInfos =
            new ConcurrentDictionary<uint, PICSProductInfo>();

        private bool IsLoggedIn => _loginDetails != null;
        public CdnPool CdnPool { get; }

        public SteamDownloader(SteamConfiguration configuration)
        {
            _client = new SteamClient(configuration);
            _user = _client.GetHandler<SteamUser>();
            _apps = _client.GetHandler<SteamApps>();
            _cloud = _client.GetHandler<SteamCloud>();
            _unifiedMessages = _client.GetHandler<SteamUnifiedMessages>();
            _publishedFiles = _unifiedMessages.CreateService<IPublishedFile>();
            CdnPool = new CdnPool(_client);


            _callbacks = new CallbackPump(_client);
            _callbacks.CallbackReceived += CallbacksOnCallbackReceived;
        }

        public async Task<byte[]> GetDepotKeyAsync(uint appId, uint depotId)
        {
            if (_depotKeys.TryGetValue(depotId, out var depotKey))
                return depotKey;

            var depotKeyResult = await _apps.GetDepotDecryptionKey(depotId, appId).ToTask().ConfigureAwait(false);
            _depotKeys[depotId] = depotKeyResult.DepotKey;
            return depotKeyResult.DepotKey;
        }

        public async Task<string> GetCdnAuthTokenAsync(uint appId, uint depotId, string host)
        {
            var key = $"{depotId}:{host}";

            if (_cdnAuthTokens.TryGetValue(key, out var token) && token.Expiration > DateTime.Now)
                return token.Token;

            var cdnAuthTokenResult = await _apps.GetCDNAuthToken(appId, depotId, host).ToTask().ConfigureAwait(false);
            _cdnAuthTokens[key] = cdnAuthTokenResult;
            return cdnAuthTokenResult.Token;
        }

        private async Task<PICSProductInfo> GetAppInfoAsync(uint appId)
        {
            if (_appInfos.TryGetValue(appId, out var appInfo))
                return appInfo;

            var productResult = await _apps.PICSGetProductInfo(appId, null, false).ToTask().ConfigureAwait(false);
            _appInfos[appId] = productResult.Results[0].Apps[appId];
            return _appInfos[appId];
        }

        private void CallbacksOnCallbackReceived(ICallbackMsg obj)
        {
            switch (obj)
            {
                case DisconnectedCallback discon:
                    if (!discon.UserInitiated)
                        OnDisconnect();
                    break;
            }
        }

        private async Task<ulong> GetManifestForBranch(uint appId, uint depotId, string branch)
        {
            var appInfo = await GetAppInfoAsync(appId).ConfigureAwait(false);
            return appInfo.GetManifestId(depotId, branch);
        }

        private async Task<DepotManifest> GetManifestAsync(uint appId, uint depotId, ulong manifestId)
        {
            if (!IsLoggedIn)
                throw new InvalidOperationException("The Steam client is not logged in.");

            var depotKey = await GetDepotKeyAsync(appId, depotId).ConfigureAwait(false);
            var cdnClient = CdnPool.TakeClient();
            var server = CdnPool.GetBestServer();
            var cdnAuthToken = await GetCdnAuthTokenAsync(appId, depotId, server.Host).ConfigureAwait(false);
            var manifest = await cdnClient.DownloadManifestAsync(depotId, manifestId, server, cdnAuthToken, depotKey)
                .ConfigureAwait(false);

            return manifest;
        }

        #region Auth

        /// <summary>
        /// Connect to Steam and log in with the given details, or anonymously if none are provided.
        /// </summary>
        /// <param name="details">User credentials.</param>
        /// <returns>Login details.</returns>
        /// <exception cref="Exception"></exception>
        public async Task<LoggedOnCallback> LoginAsync(LogOnDetails details = default)
        {
            if (_loginDetails != null)
                throw new InvalidOperationException("Already logged in.");

            _callbacks.Start();
            _client.Connect();

            var connectResult = await _callbacks
                .WaitForAsync(x => x is ConnectedCallback || x is DisconnectedCallback)
                .ConfigureAwait(false);

            if (connectResult is DisconnectedCallback)
                throw new Exception("Failed to connect to Steam.");

            if (details == null)
                _user.LogOnAnonymous();
            else
                _user.LogOn(details);

            var loginResult = await _callbacks.WaitForAsync<LoggedOnCallback>().ConfigureAwait(false);
            if (loginResult.Result != EResult.OK)
                throw new Exception($"Failed to log into Steam: {loginResult.Result:G}");

            await CdnPool.Initialize((int)loginResult.CellID);
            _loginDetails = loginResult;
            return loginResult;
        }

        /// <summary>
        /// Log out the client and disconnect from Steam.
        /// </summary>
        public async Task LogoutAsync()
        {
            if (_loginDetails == null)
                return;

            _user.LogOff();
            _client.Disconnect();

            await _callbacks.WaitForAsync<DisconnectedCallback>().ConfigureAwait(false);
            OnDisconnect();
        }

        private void OnDisconnect()
        {
            _callbacks.Stop();
            _loginDetails = null;

            _appInfos.Clear();
            _depotKeys.Clear();
            _cdnAuthTokens.Clear();
        }

        #endregion


        public async Task InstallAppAsync(uint appId, uint depotId, string branch, string installPath, int workerCount,
            Predicate<string> installFilter, string debugName)
        {
            var manifestId = await GetManifestForBranch(appId, depotId, branch);
            await InstallInternalAsync(appId, depotId, manifestId, installPath, workerCount, installFilter, debugName);
        }

        private async Task InstallInternalAsync(uint appId, uint depotId, ulong manifestId, string installPath,
            int workerCount,
            Predicate<string> installFilter, string debugName)
        {
            var localCache = new LocalFileCache();
            var localCacheFile = Path.Combine(installPath, CacheDir, depotId.ToString());

            if (File.Exists(localCacheFile))
            {
                try
                {
                    using (var fs = File.OpenRead(localCacheFile))
                        localCache = (LocalFileCache)LocalFileCache.Serializer.Deserialize(fs);
                }
                catch
                {
                    // ignored
                }
            }


            // Ensure local file cache contains up to date information for all files:
            if (File.Exists(installPath))
                foreach (var filePath in Directory.GetFiles(installPath, "*", SearchOption.AllDirectories)
                             .Where(x => !Directory.Exists(x))
                             .Select(x => x.Substring(installPath.Length).TrimStart('/', '\\'))
                             .Where(x => !x.StartsWith(CacheDir) && installFilter(x)))
                {
                    if (!localCache.TryGet(filePath, out var metadata))
                        localCache.Add(metadata = new FileInfo { Path = filePath });
                    metadata.RepairData(installPath);
                }

            foreach (var file in localCache.Files)
                file.RepairData(installPath);

            Directory.CreateDirectory(installPath);
            Directory.CreateDirectory(Path.Combine(installPath, CacheDir));

            var lockFile = Path.Combine(installPath, LockFile);
            try
            {
                using (File.Create(lockFile))
                {
                    // Get installation details from Steam
                    var manifest = await GetManifestAsync(appId, depotId, manifestId);

                    var job = InstallJob.Upgrade(appId, depotId, installPath, localCache, manifest, installFilter);
                    using (var timer = new Timer(3000) { AutoReset = true })
                    {
                        timer.Elapsed += (sender, args) => Log.Info($"{debugName} progress: {job.ProgressRatio:0.00%}");
                        timer.Start();
                        await job.Execute(this, workerCount);
                    }


                    using (var fs = File.Create(localCacheFile))
                        LocalFileCache.Serializer.Serialize(fs, localCache);
                }
            }
            catch
            {
                throw new InvalidOperationException(
                    $"A job may already be in progress on this install ({debugName}).If you're sure there isn't one, delete {lockFile}");
            }
        }

        public async Task<Dictionary<ulong, PublishedFileDetails>> LoadModDetails(uint appId, IEnumerable<ulong> modIds)
        {
            var req = new CPublishedFile_GetDetails_Request
                { appid = appId, includechildren = true, includemetadata = true };
            req.publishedfileids.AddRange(modIds);
            var response = await _publishedFiles.SendMessage(svc => svc.GetDetails(req));
            return response.GetDeserializedResponse<CPublishedFile_GetDetails_Response>().publishedfiledetails
                .ToDictionary(item => item.publishedfileid);
        }

        public async Task InstallModAsync(uint appId, ulong modId, string installPath, int workerCount,
            Predicate<string> filter, string debugName)
        {
            var appInfo = await GetAppInfoAsync(appId);
            var workshopDepot = appInfo.GetWorkshopDepot();
            var req = new CPublishedFile_GetItemInfo_Request { app_id = appId };
            req.workshop_items.Add(new CPublishedFile_GetItemInfo_Request.WorkshopItem { published_file_id = modId });
            var response = await _publishedFiles.SendMessage(svc => svc.GetItemInfo(req));
            var responseDecoded = response.GetDeserializedResponse<CPublishedFile_GetItemInfo_Response>();
            if (responseDecoded.private_items.Contains(modId))
                throw new InvalidOperationException($"Failed to latest publication of mod {modId} ({debugName}) -- it appears to be private");
            var result = responseDecoded.workshop_items
                .FirstOrDefault(x => x.published_file_id == modId);
            if (result == null)
                throw new InvalidOperationException($"Failed to latest publication of mod {modId} ({debugName})");

            await InstallInternalAsync(appId, workshopDepot, result.manifest_id, installPath, workerCount, filter,
                debugName);
            Log.Info($"Installed mod {result.published_file_id}, manifest {result.manifest_id} ({debugName})");
        }
    }
}