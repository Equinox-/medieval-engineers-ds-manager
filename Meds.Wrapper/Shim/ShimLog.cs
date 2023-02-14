using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Microsoft.Extensions.Logging;
using VRage.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;
using LogSeverity = VRage.Logging.LogSeverity;

namespace Meds.Standalone.Shim
{
    public class ShimLog
    {
        private readonly ILoggerFactory _loggerFactory;

        private readonly ThreadLocal<Dictionary<string, ILogger>> _perThreadLogCache =
            new ThreadLocal<Dictionary<string, ILogger>>(() => new Dictionary<string, ILogger>());
        public static readonly NamedLogger LoggerLegacy = new NamedLogger("Legacy", NullLogger.Instance);
        private readonly Configuration _config;

        public ShimLog(Configuration config, ILoggerFactory loggerFactory)
        {
            _config = config;
            _loggerFactory = loggerFactory;
        }

        public void BindTo(MyLog logger)
        {
            if (!_config.Install.ReplaceLogger)
                return;
            var loggerProp = typeof(MyLog).GetProperty("Logger") ?? throw new NullReferenceException("Failed to find logger property");

            var existing = logger.Logger;
            var replacement = new Logger(existing.FilePath, this);
            loggerProp.SetValue(logger, replacement);
        }

        private ILogger LoggerFor(string sourceName)
        {
            if (string.IsNullOrEmpty(sourceName))
                sourceName = LoggerLegacy.Name;
            var cache = _perThreadLogCache.Value;
            if (!cache.TryGetValue(sourceName, out var logger))
                cache.Add(sourceName, logger = _loggerFactory.CreateLogger(sourceName));
            return logger;
        }

        private sealed class Logger : TextLogger
        {
            private readonly ShimLog _owner;
            private readonly ThreadLocal<Stack<IDisposable>> _blockTokens = new ThreadLocal<Stack<IDisposable>>(() => new Stack<IDisposable>());

            public Logger(string pathName, ShimLog owner) : base(pathName, new ThrowingStream())
            {
                _owner = owner;
            }

            private ILogger LoggerFor(in NamedLogger source) => _owner.LoggerFor(source.Name);

            private static LogLevel MapLogLevel(LogSeverity severity)
            {
                switch (severity)
                {
                    case LogSeverity.Debug:
                        return LogLevel.Trace;
                    case LogSeverity.Message:
                        return LogLevel.Debug;
                    case LogSeverity.Verbatim:
                    case LogSeverity.Info:
                        return LogLevel.Information;
                    case LogSeverity.Warning:
                        return LogLevel.Warning;
                    case LogSeverity.Error:
                        return LogLevel.Error;
                    case LogSeverity.Critical:
                        return LogLevel.Critical;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(severity), severity, null);
                }
            }

            protected override void LogInternal(in NamedLogger source, LogSeverity severity, object message)
            {
                var logger = LoggerFor(in source);
                var level = MapLogLevel(severity);
                switch (message)
                {
                    case FormattableString formatString:
                        logger.Log(level, formatString.Format, formatString.GetArguments());
                        break;
                    default:
                        logger.Log(level, message?.ToString());
                        break;
                }
            }

            public override void OpenBlock(in NamedLogger source, string message)
            {
                var logger = LoggerFor(in source);
                _blockTokens.Value.Push(logger.BeginScope(message));
            }

            public override void CloseBlock(in NamedLogger source, string message = null)
            {
                var logger = LoggerFor(in source);
                if (message != null)
                    logger.LogInformation(message);
                var tokens = _blockTokens.Value;
                if (tokens.Count > 0)
                    tokens.Pop().Dispose();
            }
        }

        private sealed class ThrowingStream : Stream
        {
            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotImplementedException();

            public override void SetLength(long value) => throw new NotImplementedException();

            public override int Read(byte[] buffer, int offset, int count) => throw new NotImplementedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotImplementedException();

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotImplementedException();

            public override long Position
            {
                get => throw new NotImplementedException();
                set => throw new NotImplementedException();
            }
        }
    }
}