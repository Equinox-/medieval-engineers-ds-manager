using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace Meds.Shared
{
    public sealed class ExtraContextLoggerFactory : ILoggerFactory
    {
        private readonly ILoggerFactory _backing;

        public ExtraContextLoggerFactory(ILoggerFactory backing) => _backing = backing;

        public void Dispose() => _backing.Dispose();

        public ILogger CreateLogger(string categoryName) => new ExtraContextLogger(_backing.CreateLogger(categoryName));

        public void AddProvider(ILoggerProvider provider) => throw new System.NotImplementedException();
    }

    internal sealed class ExtraContextLogger : ILogger
    {
        private readonly ILogger _backing;
        internal static JsonEncodedText ThreadNameProp = JsonEncodedText.Encode("ThreadName");
        internal static JsonEncodedText ThreadIdProp = JsonEncodedText.Encode("ThreadId");

        public ExtraContextLogger(ILogger backing) => _backing = backing;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            _backing.Log(logLevel, eventId, new ExtraContextState<TState>(state, Thread.CurrentThread, formatter), exception,
                ExtraContextState<TState>.Formatter);
        }

        public bool IsEnabled(LogLevel logLevel) => _backing.IsEnabled(logLevel);

        public IDisposable BeginScope<TState>(TState state) => _backing.BeginScope(state);
    }

    internal struct ExtraContextState<TState> : IZLoggerState
    {
        internal TState State;
        internal readonly Thread CallingThread;
        private readonly Func<TState, Exception, string> _formatter;

        public ExtraContextState(TState state, Thread callingThread, Func<TState, Exception, string> formatter)
        {
            State = state;
            CallingThread = callingThread;
            _formatter = formatter;
        }

        public static readonly Func<ExtraContextState<TState>, LogInfo, IZLoggerEntry> Factory = factory;
        private static IZLoggerEntry factory(ExtraContextState<TState> self, LogInfo logInfo) => self.CreateLogEntry(logInfo);

        public static readonly Func<ExtraContextState<TState>, Exception, string> Formatter = (state, err) => state._formatter(state.State, err);

        public IZLoggerEntry CreateLogEntry(LogInfo logInfo) => ExtraContextLogEntry<TState>.Create(in logInfo, in this);
    }

    internal class ExtraContextLogEntry<TState> : IZLoggerEntry
    {
        private Thread _callingThread;
        private IZLoggerEntry _backing;
        private static readonly ConcurrentQueue<ExtraContextLogEntry<TState>> cache = new ConcurrentQueue<ExtraContextLogEntry<TState>>();

        private ExtraContextLogEntry()
        {
        }

        public static ExtraContextLogEntry<TState> Create(
            in LogInfo logInfo,
            in ExtraContextState<TState> state)
        {
            if (!cache.TryDequeue(out var result))
                result = new ExtraContextLogEntry<TState>();
            result._callingThread = state.CallingThread;
            result._backing = CreateLogEntryDynamic<TState>.Factory?.Invoke(state.State, logInfo);
            return result;
        }

        public void FormatUtf8(
            IBufferWriter<byte> writer,
            ZLoggerOptions options,
            Utf8JsonWriter jsonWriter)
        {
            if (_backing == null) return;
            _backing.FormatUtf8(writer, options, jsonWriter);
            if (!options.EnableStructuredLogging) return;
            jsonWriter.WriteString(ExtraContextLogger.ThreadNameProp, _callingThread.Name);
            jsonWriter.WriteNumber(ExtraContextLogger.ThreadIdProp, _callingThread.ManagedThreadId);
        }

        public void SwitchCasePayload<TPayload>(Action<IZLoggerEntry, TPayload, object> payloadCallback, object state)
        {
            // Don't bother properly include thread data when switching cases.
            _backing.SwitchCasePayload(payloadCallback, state);
        }

        public object GetPayload() => _backing?.GetPayload();

        public LogInfo LogInfo => _backing?.LogInfo ?? default;

        public void Return()
        {
            _backing?.Return();
            _backing = null;
            _callingThread = null;
            cache.Enqueue(this);
        }

        private static class CreateLogEntryDynamic<T>
        {
            public static readonly Func<T, LogInfo, IZLoggerEntry> Factory;

            static CreateLogEntryDynamic()
            {
                if (!typeof(IZLoggerState).IsAssignableFrom(typeof(T))) return;
                try
                {
                    var field = typeof(T).GetField("Factory", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    if (field == null)
                        return;
                    Factory = field.GetValue(null) as Func<T, LogInfo, IZLoggerEntry>;
                }
                catch (Exception)
                {
                    // ignore
                }
            }
        }
    }
}