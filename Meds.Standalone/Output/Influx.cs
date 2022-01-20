using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using Medieval;
using Meds.Watchdog;
using Sandbox.Game;
using VRage.Collections;
using VRage.Logging;

namespace Meds.Standalone.Output
{
    public sealed class Influx : IDisposable
    {
#if DEBUG
        private static readonly int FlushBytes = 1024;
        private static readonly TimeSpan FlushInterval = TimeSpan.FromMinutes(1);
#else
        private static readonly int FlushBytes = 64 * 1024;
        private static readonly TimeSpan FlushInterval = TimeSpan.FromMinutes(5);
#endif

        private readonly string _defaultTags;
        private readonly ThreadLocal<StreamingInfluxWriter> _logWriters;

        private volatile StringContentBuilder _writeBuffer;
        private volatile StringContentBuilder _readBuffer;

        private readonly Uri _requestUri;
        private readonly string _authHeader;
        private readonly Thread _thread;
        private readonly ManualResetEvent _event;
        private volatile bool _canceled;

        public Influx(Configuration.InfluxDb config)
        {
            _requestUri = new Uri(config.Uri
                                  + (config.Uri.EndsWith("/") ? "" : "/")
                                  + $"/api/v2/write?org={WebUtility.UrlEncode(config.Organization)}"
                                  + $"&bucket=${WebUtility.UrlEncode(config.Bucket)}&precision=ms");
            _authHeader = "Token " + config.Token;
            if (config.DefaultTags != null)
            {
                var sb = new StringBuilder();
                foreach (var tag in config.DefaultTags)
                    PointDataUtils.WriteTag(sb, tag.Tag, tag.Value, false);
                PointDataUtils.WriteTag(sb, "version", MyMedievalGame.VersionString, true);
                _defaultTags = sb.ToString();
            }
            else
                _defaultTags = "";

            _canceled = false;
            _event = new ManualResetEvent(false);
            _thread = new Thread(Run);
            _thread.Start();
            _writeBuffer = new StringContentBuilder();
            _readBuffer = new StringContentBuilder();
            
            // ReSharper disable once ConvertToLocalFunction
            // ReSharper disable once HeapView.ClosureAllocation
            Action<StringBuilder> writeRecord = str =>
            {
                lock (_writeBuffer)
                {
                    _writeBuffer.Append(str);
                    _writeBuffer.Append("\n");
                    if (_writeBuffer.Position > FlushBytes)
                        _event.Set();
                }
            };
            // ReSharper disable once HeapView.DelegateAllocation
            _logWriters = new ThreadLocal<StreamingInfluxWriter>(() => new StreamingInfluxWriter(_defaultTags, writeRecord));
        }

        public StreamingInfluxWriter Write(string measurement)
        {
            return _logWriters.Value.Reset(measurement);
        }

        private void Run()
        {
            while (!_canceled)
            {
                _event.WaitOne(FlushInterval);
                Thread.Sleep(250);
                if (_writeBuffer.Position > 0)
                    Flush();
            }
        }

        private void Flush()
        {
            try
            {
                var request = WebRequest.CreateHttp(_requestUri);
                request.Headers["Authorization"] = _authHeader;
                request.Method = "POST";
                var flush = _writeBuffer;
                _writeBuffer = _readBuffer;
                _readBuffer = flush;
                lock (_readBuffer)
                {
                    request.ContentLength = _readBuffer.Position;
                    using (var post = request.GetRequestStream())
                    {
                        _readBuffer.FlushTo(post);
                        _event.Reset();
                    }
                }

                using (var response = (HttpWebResponse) request.GetResponse())
                {
                    if (response.StatusCode != HttpStatusCode.OK && response.StatusCode != HttpStatusCode.NoContent)
                        Debugger.Break();
                }
            }
            catch (WebException err)
            {
                MyLog.Default.WriteLineAndConsole($"Failed to write metrics to InfluxDB: {err.Status}");
            }
        }

        public void Dispose()
        {
            _canceled = true;
            _event.Set();
            _thread.Join();
            _logWriters?.Dispose();
            _event?.Dispose();
        }
    }
}