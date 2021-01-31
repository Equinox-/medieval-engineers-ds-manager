using System;
using System.Collections.Generic;
using System.IO;
using VRage.Game;
using VRage.Game.ModAPI;

namespace Meds.Wrapper.Config
{
    public sealed class CustomConfig : IMyConfigDedicated
    {
        private ConfigData _data = new ConfigData();

        public string GetFilePath() => ConfigData.ConfigPath;

        public void Load(string path = null)
        {
            _data = ConfigData.Load();
        }

        public void Save(string path = null)
        {
            _data.Save();
        }

        public HashSet<ulong> Administrators
        {
            get => _data.Admins;
            set => _data.Admins = value;
        }

        public HashSet<MyDedicatedBanData> Banned
        {
            get => _data.BanData;
            set => _data.BanData = value;
        }

        public ulong GroupID
        {
            get => _data.GroupId;
            set => _data.GroupId = value;
        }

        public bool IgnoreLastSession
        {
            get => throw new NotImplementedException();
            set => throw new NotImplementedException();
        }

        public string IP
        {
            get => _data.IpAddress;
            set => _data.IpAddress = value;
        }

        public string LoadWorld => Path.Combine(Program.Instance.RuntimeDirectory, "world");
        public List<ulong> Mods => throw new NotImplementedException();

        public bool PauseGameWhenEmpty
        {
            get => _data.PauseGameWhenEmpty;
            set => _data.PauseGameWhenEmpty = value;
        }

        public MyDefinitionId Scenario
        {
            get => throw new NotImplementedException();
            set => throw new NotImplementedException();
        }

        public string ServerName
        {
            get => _data.Name;
            set => _data.Name = value;
        }

        public int ServerPort
        {
            get => _data.ServerPort;
            set => _data.ServerPort = value;
        }

        public MyObjectBuilder_SessionSettings SessionSettings
        {
            get => throw new NotImplementedException();
            set => throw new NotImplementedException();
        }

        public int SteamPort
        {
            get => _data.SteamPort;
            set => _data.SteamPort = value;
        }

        public string WorldName
        {
            get => null;
            set => throw new NotImplementedException();
        }

        public bool RemoteApiEnabled
        {
            get => _data.Remote.Enabled;
            set => _data.Remote.Enabled = value;
        }

        public bool PublicRemoteApiEnabled
        {
            get => _data.Remote.Public;
            set => _data.Remote.Public = value;
        }

        public string RemoteSecurityKey
        {
            get => _data.Remote.SecurityKey;
            set => _data.Remote.SecurityKey = value;
        }

        public int RemoteApiPort
        {
            get => _data.Remote.Port;
            set => _data.Remote.Port = value;
        }
    }
}