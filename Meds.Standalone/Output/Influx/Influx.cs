using System;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Threading;
using Medieval;
using Meds.Watchdog;
using VRage.Logging;
using VRage.Utils;

namespace Meds.Standalone.Output.Influx
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

        private StringContentBuilder _writeBuffer;
        private StringContentBuilder _readBuffer;

        private readonly Uri _requestUri;
        private readonly string _authHeader;
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _triggerFlush;
        private readonly ManualResetEventSlim _waitFlush;
        private volatile bool _canceled;

        public Influx(Configuration.InfluxDb config)
        {
            _requestUri = new Uri(config.Uri
                                  + (config.Uri.EndsWith("/") ? "" : "/")
                                  + $"/api/v2/write?org={WebUtility.UrlEncode(config.Organization)}"
                                  + $"&bucket=${WebUtility.UrlEncode(config.Bucket)}&precision=ms");
            _authHeader = "Token " + config.Token;
            
            var sb = new StringBuilder();
            if (config.DefaultTags != null)
                foreach (var tag in config.DefaultTags)
                    PointDataUtils.WriteTag(sb, tag.Tag, tag.Value, false);
            _defaultTags = sb.ToString();

            _canceled = false;
            _triggerFlush = new ManualResetEventSlim(false);
            _waitFlush = new ManualResetEventSlim(false);
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
                        _triggerFlush.Set();
                }
            };
            // ReSharper disable once HeapView.DelegateAllocation
            _logWriters = new ThreadLocal<StreamingInfluxWriter>(() => new StreamingInfluxWriter(_defaultTags, writeRecord));

            AppDomain.CurrentDomain.UnhandledException += FlushOnUnhandledError;
        }

        private void FlushOnUnhandledError(object sender, UnhandledExceptionEventArgs args)
        {
            _waitFlush.Reset();
            _triggerFlush.Set();
            _waitFlush.Wait(1000);
        }

        public StreamingInfluxWriter Write(string measurement)
        {
            return _logWriters.Value.Reset(measurement);
        }

        private void Run()
        {
            while (!_canceled)
            {
                _triggerFlush.Wait(FlushInterval);
                Thread.Sleep(250);
                if (_writeBuffer.Position > 0)
                    Flush();
                _waitFlush.Set();
            }
        }

        private void Flush()
        {
            try
            {
                var request = WebRequest.CreateHttp(_requestUri);
                request.Headers["Authorization"] = _authHeader;
                request.Method = "POST";
                MyUtils.Swap(ref _writeBuffer, ref _readBuffer);
                lock (_readBuffer)
                {
                    request.ContentLength = _readBuffer.Position;
                    using (var post = request.GetRequestStream())
                    {
                        _readBuffer.FlushTo(post);
                        _triggerFlush.Reset();
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
            AppDomain.CurrentDomain.UnhandledException -= FlushOnUnhandledError;
            _canceled = true;
            _triggerFlush.Set();
            _thread.Join();
            _logWriters?.Dispose();
            _triggerFlush?.Dispose();
        }
    }
}