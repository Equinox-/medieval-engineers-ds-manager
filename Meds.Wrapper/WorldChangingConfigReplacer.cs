using System.Collections.Generic;
using System.IO;
using VRage.Game;
using VRage.Game.ModAPI;

namespace Meds.Standalone
{
    public class WorldChangingConfigReplacer : IMyConfigDedicated
    {
        private readonly IMyConfigDedicated _delegate;
        private readonly Configuration _config;

        public WorldChangingConfigReplacer(Configuration config, IMyConfigDedicated del)
        {
            _config = config;
            _delegate = del;
        }

        public void Load(string path = null) => _delegate.Load(path);

        public void Save(string path = null) => _delegate.Save(path);

        public string GetFilePath() => _delegate.GetFilePath();

        public HashSet<ulong> Administrators
        {
            get => _delegate.Administrators;
            set => _delegate.Administrators = value;
        }

        public HashSet<MyDedicatedBanData> Banned
        {
            get => _delegate.Banned;
            set => _delegate.Banned = value;
        }

        public ulong GroupID
        {
            get => _delegate.GroupID;
            set => _delegate.GroupID = value;
        }

        public bool IgnoreLastSession
        {
            get => _delegate.IgnoreLastSession;
            set => _delegate.IgnoreLastSession = value;
        }

        public string IP
        {
            get => _delegate.IP;
            set => _delegate.IP = value;
        }

        public string LoadWorld => Path.Combine(_config.Install.RuntimeDirectory, "world");

        public List<ulong> Mods => _delegate.Mods;

        public bool PauseGameWhenEmpty
        {
            // Not supported due to health checks.
            get => false;
            set { } 
        }

        public MyDefinitionId Scenario
        {
            // Not supported due to requiring pre-made worlds.
            get => default;
            set { }
        }

        public string ServerName
        {
            get => _delegate.ServerName;
            set => _delegate.ServerName = value;
        }

        public int ServerPort
        {
            get => _delegate.ServerPort;
            set => _delegate.ServerPort = value;
        }

        public MyObjectBuilder_SessionSettings SessionSettings
        {
            get => _delegate.SessionSettings;
            set => _delegate.SessionSettings = value;
        }

        public int SteamPort
        {
            get => _delegate.SteamPort;
            set => _delegate.SteamPort = value;
        }

        public string WorldName
        {
            get => _delegate.WorldName;
            set => _delegate.WorldName = value;
        }

        public bool RemoteApiEnabled
        {
            get => _delegate.RemoteApiEnabled;
            set => _delegate.RemoteApiEnabled = value;
        }

        public bool PublicRemoteApiEnabled
        {
            get => _delegate.PublicRemoteApiEnabled;
            set => _delegate.PublicRemoteApiEnabled = value;
        }

        public string RemoteSecurityKey
        {
            get => _delegate.RemoteSecurityKey;
            set => _delegate.RemoteSecurityKey = value;
        }

        public int RemoteApiPort
        {
            get => _delegate.RemoteApiPort;
            set => _delegate.RemoteApiPort = value;
        }
    }
}