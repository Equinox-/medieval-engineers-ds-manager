using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Meds.Shared;
using Meds.Shared.Data;
using Meds.Watchdog.Save;
using Meds.Watchdog.Utils;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace Meds.Watchdog
{
    [XmlRoot("DataStore")]
    public sealed class DataStoreData
    {
        [XmlElement]
        public PlanetData Planet = new PlanetData();

        [XmlElement]
        public GridDatabaseConfig GridDatabase = new GridDatabaseConfig();

        [XmlElement]
        public LifecycleStateSerialized LifecycleState;
    }

    public sealed class DataStore : BackgroundService
    {
        private readonly XmlSerializer _serializer = new XmlSerializer(typeof(DataStoreData));
        private readonly string _dataFile;
        private readonly ReaderWriterLockSlim _lock = new ReaderWriterLockSlim();
        private readonly DataStoreData _data;
        private readonly ILogger<DataStore> _log;
        private readonly ISubscriber<DataStoreSync> _syncSubscriber;
        private int _modified;

        public DataStore(InstallConfiguration config, ILogger<DataStore> log, ISubscriber<DataStoreSync> syncSubscriber)
        {
            _log = log;
            _dataFile = Path.Combine(config.Directory, "data.xml");

            if (File.Exists(_dataFile))
            {
                using var stream = new FileStream(_dataFile, FileMode.Open, FileAccess.Read);
                _data = (DataStoreData)_serializer.Deserialize(stream);
            }
            else
            {
                _data = new DataStoreData();
                _modified = 1;
            }

            _syncSubscriber = syncSubscriber;
        }

        public ReadToken Read(out DataStoreData data)
        {
            data = _data;
            return new ReadToken(this);
        }


        public WriteToken Write(out DataStoreData data)
        {
            data = _data;
            return new WriteToken(this);
        }

        private void Save()
        {
            _lock.EnterReadLock();
            try
            {
                FileUtils.WriteAtomic(_dataFile, _data, _serializer);
                _log.ZLogInformation("Saved data store to {0}", _dataFile);
            }
            catch (Exception err)
            {
                _log.ZLogCritical(err, "Failed to save data store");
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var subscriber = _syncSubscriber.Subscribe(sync =>
            {
                using var tok = Write(out var data);

                if (sync.Planet.HasValue)
                {
                    var planetSrc = sync.Planet.Value;
                    var planetDst = data.Planet ??= new PlanetData();
                    tok.Update(ref planetDst.MinRadius, planetSrc.MinRadius);
                    tok.Update(ref planetDst.AvgRadius, planetSrc.AvgRadius);
                    tok.Update(ref planetDst.MaxRadius, planetSrc.MaxRadius);
                    tok.Update(ref planetDst.AreasPerRegion, planetSrc.AreasPerRegion);
                    tok.Update(ref planetDst.AreasPerFace, planetSrc.AreasPerFace);
                }

                if (sync.GridDatabase.HasValue)
                {
                    var gridSrc = sync.GridDatabase.Value;
                    var gridDst = data.GridDatabase ??= new GridDatabaseConfig();
                    tok.Update(ref gridDst.MaxLod, gridSrc.MaxLod);
                    tok.Update(ref gridDst.GridSize, gridSrc.GridSize);
                }
            });
            while (!stoppingToken.IsCancellationRequested)
            {
                if (Interlocked.Exchange(ref _modified, 0) == 1)
                    Save();
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }

            Save();
        }

        public readonly struct ReadToken : IDisposable
        {
            private readonly DataStore _store;

            public ReadToken(DataStore store)
            {
                _store = store;
                store._lock.EnterReadLock();
            }

            public void Dispose() => _store._lock.ExitReadLock();
        }

        public struct WriteToken : IDisposable
        {
            private readonly DataStore _store;
            private bool _updated;

            public WriteToken(DataStore store)
            {
                _store = store;
                store._lock.EnterWriteLock();
                _updated = false;
            }

            public void Update<T>(ref T value, T newValue) where T : IEquatable<T>
            {
                // ReSharper disable once HeapView.PossibleBoxingAllocation
                if (!_updated)
                    _updated = typeof(T).IsValueType ? !newValue.Equals(value) : !Equals(newValue, value);
                value = newValue;
            }

            public void Dispose()
            {
                if (_updated)
                    Interlocked.Exchange(ref _store._modified, 1);
                _store._lock.ExitWriteLock();
            }
        }
    }
}