using System;
using System.Text;
using System.Threading;
using InfluxDB.Client;
using InfluxDB.Client.Api.Domain;

namespace Meds.Watchdog.Database
{
    public sealed class Influx : IDisposable
    {
        private readonly InfluxDBClient _client;
        private readonly WriteApi _writer;
        private readonly string _defaultTags;
        private readonly ThreadLocal<StreamingInfluxWriter> _logWriters;

        public Influx(Configuration.InfluxDb config)
        {
            var opts = InfluxDBClientOptions.Builder.CreateNew()
                .AuthenticateToken(config.Token)
                .Org(config.Organization)
                .Bucket(config.Bucket)
                .Url(config.Uri);
            if (config.DefaultTags != null)
            {
                var sb = new StringBuilder();
                foreach (var tag in config.DefaultTags)
                {
                    opts.AddDefaultTag(tag.Tag, tag.Value);
                    PointDataUtils.WriteTag(sb, tag.Tag, tag.Value, false);
                }

                _defaultTags = sb.ToString();
            }
            else
                _defaultTags = "";

            _client = InfluxDBClientFactory.Create(opts.Build());
            _writer = _client.GetWriteApi();
            // ReSharper disable once ConvertToLocalFunction
            // ReSharper disable once HeapView.ClosureAllocation
            Action<string> writeRecord = record => _writer.WriteRecord(WritePrecision.Ms, record);
            // ReSharper disable once HeapView.DelegateAllocation
            _logWriters = new ThreadLocal<StreamingInfluxWriter>(() => new StreamingInfluxWriter(_defaultTags, writeRecord));
        }

        public StreamingInfluxWriter Write(string measurement)
        {
            return _logWriters.Value.Reset(measurement);
        }

        public void Dispose()
        {
            _writer.Dispose();
            _client.Dispose();
        }
    }
}