using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json.Serialization;
using System.Threading;
using Meds.Wrapper.Audit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VRage.Library.Utils;
using ZLogger;

namespace Meds.Wrapper.Trace
{
    public struct TraceSpan : IDisposable
    {
        [JsonIgnore]
        internal List<ulong> AttachedTo;

        [JsonPropertyName("traceID")]
        [JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)]
        public ulong TraceId;

        [JsonPropertyName("spanID")]
        [JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)]
        public ulong SpanId;

        [JsonPropertyName("parentSpanID")]
        [JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)]
        public ulong? ParentSpanId;

        [JsonPropertyName("operationName")]
        public string OperationName;

        [JsonPropertyName("serviceName")]
        public string ServiceName;

        [JsonPropertyName("startTime")]
        public double StartTimeMs;

        [JsonPropertyName("duration")]
        public double DurationMs;

        public void Dispose() => this.Finish();
    }

    public struct TraceReferencePayload
    {
        [JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)]
        public ulong TraceId;

        [JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)]
        public ulong? ParentSpanId;
    }

    public class TraceState
    {
        public readonly ulong TraceId;
        private long _root;
        private readonly ThreadLocal<List<ulong>> _stack = new ThreadLocal<List<ulong>>(() => new List<ulong>());

        internal TraceState(ulong traceId) => TraceId = traceId;

        public static TraceState NewTrace() => new TraceState(TraceExt.RandomId());

        private static ulong? OptionalId(ulong id) => id != 0 ? (ulong?)id : null;

        public TraceSpan StartSpan(string operationName, string serviceName = TraceExt.DefaultService)
        {
            var span = StartDetachedSpan(operationName, serviceName);
            var stack = _stack.Value;
            span.AttachedTo = stack;
            stack.Add(span.SpanId);
            Interlocked.CompareExchange(ref _root, (long)span.SpanId, 0);
            return span;
        }

        private ulong? Parent
        {
            get
            {
                var stack = _stack.Value;
                return stack.Count > 0 ? stack[stack.Count - 1] : OptionalId((ulong)_root);
            }
        }

        public TraceSpan StartDetachedSpan(string operationName, string serviceName = TraceExt.DefaultService) =>
            TraceExt.StartDetachedSpan(TraceId, Parent, operationName, serviceName);

        public TraceReferencePayload RefPayload() => new TraceReferencePayload
        {
            TraceId = TraceId,
            ParentSpanId = Parent,
        };

        public static void MaybeDetach(ref TraceSpan span)
        {
            ref var stack = ref span.AttachedTo;
            if (stack == null) return;

            var seen = false;
            var last = stack.Count - 1;
            for (var j = last; j >= 0; j--)
            {
                var here = stack[j];
                if (here == span.SpanId)
                {
                    seen = true;
                    if (j == last) stack.RemoveAt(last);
                    continue;
                }

                if (!seen) continue;
                if (here == 0) stack.RemoveAt(j);
                else break;
            }

            stack = null;
        }
    }

    public static class TraceExt
    {
        private static readonly double StopwatchToUnixConstant;
        private static readonly double StopwatchToUnixMult;
        internal const string DefaultService = "Meds";

        static TraceExt()
        {
            var nowReal = DateTime.UtcNow;
            var nowStop = Stopwatch.GetTimestamp();

            var nowUnix = nowReal.Subtract(new DateTime(1970, 1, 1)).TotalMilliseconds;
            StopwatchToUnixMult = 1000.0 / Stopwatch.Frequency;
            StopwatchToUnixConstant = nowUnix - nowStop * StopwatchToUnixMult;
        }

        private static double NowUnix() => Stopwatch.GetTimestamp() * StopwatchToUnixMult + StopwatchToUnixConstant;

        public static ulong RandomId() => (ulong)MyRandom.Instance.NextLong();

        public static TraceSpan DetachChildSpan(this in TraceSpan parent, string operationName, string serviceName = DefaultService) =>
            StartDetachedSpan(parent.TraceId, parent.SpanId, operationName, serviceName);

        public static TraceSpan StartDetachedSpan(ulong traceId, ulong? parentSpanId, string operationName, string serviceName = DefaultService) =>
            new TraceSpan
            {
                TraceId = traceId,
                SpanId = RandomId(),
                ParentSpanId = parentSpanId,
                OperationName = operationName,
                ServiceName = serviceName,
                StartTimeMs = NowUnix(),
                DurationMs = double.NaN,
            };

        private static volatile AuditLoggerHolder _logger;

        public static void Finish(this ref TraceSpan span)
        {
            if (!double.IsNaN(span.DurationMs)) return;
            TraceState.MaybeDetach(ref span);
            span.DurationMs = NowUnix() - span.StartTimeMs;


            var log = _logger;
            var instance = Entrypoint.Instance;
            if (log == null || log.Owner != instance)
            {
                lock (typeof(AuditPayload))
                {
                    _logger = log = new AuditLoggerHolder
                    {
                        Owner = instance,
                        Logger = instance.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Trace"),
                    };
                }
            }

            log.Logger.ZLogInformationWithPayload(span, "Emitted Span");
        }

        private sealed class AuditLoggerHolder
        {
            public IHost Owner;
            public ILogger Logger;
        }
    }
}