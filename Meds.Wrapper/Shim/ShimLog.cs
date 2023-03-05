using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using Microsoft.Extensions.Logging;
using VRage.Collections;
using VRage.Components;
using VRage.Engine;
using VRage.Game;
using VRage.Game.Components;
using VRage.Logging;
using VRage.Session;
using ZLogger;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace Meds.Wrapper.Shim
{
    public class ShimLog
    {
        private readonly ILoggerFactory _rootLogger;

        private readonly ThreadLocal<Dictionary<string, EnrichedLogger>> _perThreadLogCache =
            new ThreadLocal<Dictionary<string, EnrichedLogger>>(() => new Dictionary<string, EnrichedLogger>());

        public static readonly NamedLogger LoggerLegacy = new NamedLogger("Legacy", NullLogger.Instance);

        public ShimLog(ILoggerFactory rootLogger)
        {
            _rootLogger = rootLogger;
        }

        public void BindTo(MyLog logger)
        {
            var loggerProp = typeof(MyLog).GetProperty("Logger") ?? throw new NullReferenceException("Failed to find logger property");

            var existing = logger.Logger;
            var replacement = new SerilogLogger(existing.FilePath, this);
            loggerProp.SetValue(logger, replacement);
        }

        private EnrichedLogger LoggerFor(string sourceName)
        {
            if (string.IsNullOrEmpty(sourceName))
                sourceName = LoggerLegacy.Name;
            var cache = _perThreadLogCache.Value;
            if (!cache.TryGetValue(sourceName, out var logger))
                cache.Add(sourceName, logger = new EnrichedLogger(_rootLogger, sourceName));
            return logger;
        }

        private sealed class EnrichedLogger : ILogger
        {
            private readonly ILogger _delegate;
            public readonly Stack<string> Scopes = new Stack<string>();

            public EnrichedLogger(ILoggerFactory root, string name)
            {
                _delegate = root.CreateLogger(name);
            }

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            {
                _delegate.Log(logLevel, eventId, state, exception, formatter);
            }

            public bool IsEnabled(LogLevel logLevel) => _delegate.IsEnabled(logLevel);

            public IDisposable BeginScope<TState>(TState state) => _delegate.BeginScope(state);
        }

        private sealed class SerilogLogger : TextLogger
        {
            private readonly ShimLog _owner;

            public SerilogLogger(string pathName, ShimLog owner) : base(pathName, new ThrowingStream())
            {
                _owner = owner;
            }

            private EnrichedLogger LoggerFor(in NamedLogger source) => _owner.LoggerFor(source.Name);

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

            private static readonly ConcurrentDictionary<Assembly, IApplicationPackage> _packageForAssembly =
                new ConcurrentDictionary<Assembly, IApplicationPackage>();

            private static IApplicationPackage PackageForAssembly(Assembly asm)
            {
                var mods = MySession.Static?.ModManager?.Assemblies ?? DictionaryReader<MyModContext, Assembly>.Empty;
                if (asm == null || mods.Count == 0)
                    return MyModContext.BaseGame;
                return _packageForAssembly.GetOrAdd(asm, asmVal =>
                {
                    foreach (var mod in mods)
                        if (mod.Value == asmVal)
                            return mod.Key;
                    return MyModContext.BaseGame;
                });
            }

            private struct PackagePayload
            {
                public string Name;
                public ulong? ModId;

                public PackagePayload(IApplicationPackage package)
                {
                    Name = package.Name;
                    ModId = (package as MyModContext)?.WorkshopItem?.Id;
                }

                public PackagePayload(Type type) : this(PackageForAssembly(type?.Assembly))
                {
                }
            }

            private struct MemberPayload
            {
                public PackagePayload Package;
                public string Type;
                public string Member;

                public MemberPayload(MemberInfo member)
                {
                    Package = new PackagePayload(member.DeclaringType);
                    Type = member.DeclaringType?.Name;
                    Member = member.Name;
                }
            }

            private struct DefinitionPayload
            {
                public PackagePayload Package;
                public string Type;
                public string Subtype;

                public DefinitionPayload(MyObjectBuilder_DefinitionBase ob)
                {
                    Package = new PackagePayload(ob.Package);
                    Type = ob.Id.TypeIdString;
                    Subtype = ob.SubtypeName;
                }

                public DefinitionPayload(MyDefinitionBase ob)
                {
                    Package = new PackagePayload(ob.Package);
                    Type = ob.Id.TypeId.ShortName;
                    Subtype = ob.Id.SubtypeName;
                }
            }

            private struct ComponentPayload
            {
                public PackagePayload Package;
                public string Type;

                public ComponentPayload(IComponent comp)
                {
                    Package = new PackagePayload(comp.GetType());
                    Type = comp.GetType().Name;
                }
            }

            private struct EntityComponentPayload
            {
                public PackagePayload Package;
                public string Type;
                public long? EntityId;

                public EntityComponentPayload(MyEntityComponent comp)
                {
                    Package = new PackagePayload(comp.GetType());
                    Type = comp.GetType().Name;
                    EntityId = comp.Entity?.EntityId;
                }
            }

            protected override void LogInternal(in NamedLogger source, LogSeverity severity, object message)
            {
                var logger = LoggerFor(in source);
                var level = MapLogLevel(severity);

                void SendWithPayload<TPayload>(TPayload payload, object msg)
                {
                    switch (msg)
                    {
                        case FormattableString formatString:
                            var args = formatString.GetArguments();
                            var format = formatString.Format;
                            switch (args.Length)
                            {
                                case 0:
                                    logger.ZLogWithPayload(level, payload, format);
                                    break;
                                case 1:
                                    logger.ZLogWithPayload(level, payload, format, args[0]);
                                    break;
                                case 2:
                                    logger.ZLogWithPayload(level, payload, format, args[0], args[1]);
                                    break;
                                case 3:
                                    logger.ZLogWithPayload(level, payload, format, args[0], args[1], args[2]);
                                    break;
                                case 4:
                                    logger.ZLogWithPayload(level, payload, format, args[0], args[1], args[2], args[3]);
                                    break;
                                case 5:
                                    logger.ZLogWithPayload(level, payload, format, args[0], args[1], args[2], args[3],
                                        args[4]);
                                    break;
                                case 6:
                                    logger.ZLogWithPayload(level, payload, format, args[0], args[1], args[2], args[3],
                                        args[4], args[5]);
                                    break;
                                case 7:
                                    logger.ZLogWithPayload(level, payload, format, args[0], args[1], args[2], args[3],
                                        args[4], args[5], args[6]);
                                    break;
                                case 8:
                                    logger.ZLogWithPayload(level, payload, format, args[0], args[1], args[2], args[3],
                                        args[4], args[5], args[6], args[7]);
                                    break;
                                case 9:
                                    logger.ZLogWithPayload(level, payload, format, args[0], args[1], args[2], args[3],
                                        args[4], args[5], args[6], args[7],
                                        args[8]);
                                    break;
                                case 10:
                                    logger.ZLogWithPayload(level, payload, format, args[0], args[1], args[2], args[3],
                                        args[4], args[5], args[6], args[7],
                                        args[8], args[9]);
                                    break;
                                case 11:
                                    logger.ZLogWithPayload(level, payload, format, args[0], args[1], args[2], args[3],
                                        args[4], args[5], args[6], args[7], args[8], args[9], args[10]);
                                    break;
                                default:
                                    logger.Log(level, format, formatString.GetArguments());
                                    break;
                            }

                            break;
                        default:
                            logger.ZLogWithPayload(level, payload, msg?.ToString() ?? string.Empty);
                            break;
                    }
                }

                if (message is ContextualLogMessage contextual)
                {
                    message = contextual.UserMessage;
                    switch (message)
                    {
                        case MyObjectBuilder_DefinitionBase ctx:
                            SendWithPayload(new DefinitionPayload(ctx), message);
                            break;
                        case MyDefinitionBase ctx:
                            SendWithPayload(new DefinitionPayload(ctx), message);
                            break;
                        case MyEntityComponent ctx:
                            SendWithPayload(new EntityComponentPayload(ctx), message);
                            break;
                        case IComponent ctx:
                            SendWithPayload(new ComponentPayload(ctx), message);
                            break;
                        case MemberInfo ctx:
                            SendWithPayload(new MemberPayload(ctx), ctx);
                            break;
                        default:
                            SendWithPayload(contextual.Context?.ToString(), message);
                            break;
                    }
                }
                else
                {
                    SendWithPayload<object>(null, message);
                }
            }

            public override void OpenBlock(in NamedLogger source, string message)
            {
                var logger = LoggerFor(in source);
                logger.Scopes.Push(message);
            }

            public override void CloseBlock(in NamedLogger source, string message = null)
            {
                var logger = LoggerFor(in source);
                if (message != null)
                    logger.ZLogInformation(message);
                var scopes = logger.Scopes;
                if (scopes.Count > 0)
                    scopes.Pop();
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