using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Google.FlatBuffers;
using Meds.Shared.Data;
using Sandbox.Game.Entities;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.Components.Session;
using VRage.Logging;
using VRage.Session;
using LogSeverity = VRage.Logging.LogSeverity;

namespace Meds.Wrapper.Shim
{
    public static class ShimLog
    {
        public static void Hook()
        {
            try
            {
                var loggerProp = typeof(MyLog).GetProperty("Logger") ?? throw new NullReferenceException("Failed to find logger property");

                foreach (var logger in MyLog.Loggers)
                {
                    var existing = logger.Logger;
                    var replacement = new Logger(existing.FilePath);
                    loggerProp.SetValue(logger, replacement);
                }
            }
            catch (NullReferenceException e)
            {
                throw;
            }
        }

        public sealed class LogOutputHelper
        {
            private static readonly Dictionary<LogSeverity, Meds.Shared.Data.LogSeverity> SeverityTable =
                new Dictionary<LogSeverity, Meds.Shared.Data.LogSeverity>
                {
                    [LogSeverity.Debug] = Meds.Shared.Data.LogSeverity.Debug,
                    [LogSeverity.Message] = Meds.Shared.Data.LogSeverity.Info,
                    [LogSeverity.Info] = Meds.Shared.Data.LogSeverity.Info,
                    [LogSeverity.Warning] = Meds.Shared.Data.LogSeverity.Warning,
                    [LogSeverity.Error] = Meds.Shared.Data.LogSeverity.Error,
                    [LogSeverity.Critical] = Meds.Shared.Data.LogSeverity.Critical,
                    [LogSeverity.Verbatim] = Meds.Shared.Data.LogSeverity.PreFormatted,
                };

            private const int StackArgLength = 32;

            private readonly List<KeyValuePair<int, LogArg>> _argOverflow = new List<KeyValuePair<int, LogArg>>();

            public void OpenBlock(in NamedLogger source, string message)
            {
                // throw new NotImplementedException();
            }

            public void CloseBlock(in NamedLogger source, string message)
            {
                // throw new NotImplementedException();
            }

            private static StringOffset? CreateSharedString(string str, FlatBufferBuilder builder)
            {
                return string.IsNullOrEmpty(str) ? (StringOffset?) null : builder.CreateSharedString(str);
            }

            private static StringOffset? CreateString(string str, FlatBufferBuilder builder)
            {
                return string.IsNullOrEmpty(str) ? (StringOffset?) null : builder.CreateString(str);
            }

            private static int CreateIntegerArg(long arg, FlatBufferBuilder builder, out LogArg type)
            {
                type = LogArg.Int64;
                return Int64LogArg.CreateInt64LogArg(builder, arg).Value;
            }

            private static int CreateFloatingArg(double arg, FlatBufferBuilder builder, out LogArg type)
            {
                type = LogArg.Float64;
                return Float64LogArg.CreateFloat64LogArg(builder, arg).Value;
            }

            private static int CreateExceptionArg(Exception arg, FlatBufferBuilder builder, out LogArg type)
            {
                type = LogArg.Exception;
                var msg = CreateSharedString(arg.Message, builder);
                var stack = CreateString(arg.StackTrace, builder);
                var errType = CreateSharedString(arg.GetType().FullName, builder);
                ExceptionLogArg.StartExceptionLogArg(builder);
                if (msg.HasValue)
                    ExceptionLogArg.AddMessage(builder, msg.Value);
                if (stack.HasValue)
                    ExceptionLogArg.AddStack(builder, stack.Value);
                if (errType.HasValue)
                    ExceptionLogArg.AddType(builder, errType.Value);
                return ExceptionLogArg.EndExceptionLogArg(builder).Value;
            }

            private static int CreateArg(object arg, FlatBufferBuilder builder, out LogArg type)
            {
                switch (arg)
                {
                    case byte val:
                        return CreateIntegerArg(val, builder, out type);
                    case sbyte val:
                        return CreateIntegerArg(val, builder, out type);
                    case short val:
                        return CreateIntegerArg(val, builder, out type);
                    case ushort val:
                        return CreateIntegerArg(val, builder, out type);
                    case int val:
                        return CreateIntegerArg(val, builder, out type);
                    case uint val:
                        return CreateIntegerArg(val, builder, out type);
                    case long val:
                        return CreateIntegerArg(val, builder, out type);
                    case ulong val:
                        return CreateIntegerArg((long) val, builder, out type);
                    case float val:
                        return CreateFloatingArg(val, builder, out type);
                    case double val:
                        return CreateFloatingArg(val, builder, out type);
                    case Exception val:
                        return CreateExceptionArg(val, builder, out type);
                    default:
                        type = LogArg.String;
                        string str;
                        if (arg is IFormattable formattable)
                            str = formattable.ToString(null, MyLog.Default.Logger.FormatProvider);
                        else
                            str = arg?.ToString() ?? "null";
                        return StringLogArg.CreateStringLogArg(builder, builder.CreateString(str)).Value;
                }
            }

            private Offset<DefinitionContext> CreateDefinitionContext(MyDefinitionBase def, FlatBufferBuilder builder)
            {
                var type = CreateSharedString(def.Id.TypeId.ShortName, builder);
                var subtype = CreateSharedString(def.Id.SubtypeName, builder);
                var package = CreateSharedString(def.Package?.Id, builder);
                DefinitionContext.StartDefinitionContext(builder);
                if (type.HasValue)
                    DefinitionContext.AddType(builder, type.Value);
                if (subtype.HasValue)
                    DefinitionContext.AddSubtype(builder, subtype.Value);
                if (package.HasValue)
                    DefinitionContext.AddPackage(builder, package.Value);
                return DefinitionContext.EndDefinitionContext(builder);
            }

            private Offset<EntityComponentContext> CreateEntityContext(MyEntityComponent component, FlatBufferBuilder builder)
            {
                var defCtx = MyDefinitionManager.TryGet<MyEntityComponentDefinition>(component.DefinitionId, out var def)
                    ? CreateDefinitionContext(def, builder)
                    : (Offset<DefinitionContext>?) null;
                var typeString = builder.CreateSharedString(component.GetType().Name);
                EntityComponentContext.StartEntityComponentContext(builder);
                EntityComponentContext.AddType(builder, typeString);
                if (component.Entity != null)
                    EntityComponentContext.AddEntity(builder, component.Entity.Id.Value);
                if (defCtx.HasValue)
                    EntityComponentContext.AddDefinition(builder, defCtx.Value);
                return EntityComponentContext.EndEntityComponentContext(builder);
            }

            private Offset<SceneComponentContext> CreateSessionContext(VRage.Scene.IMySceneComponent component, FlatBufferBuilder builder)
            {
                var defCtx = component is MySessionComponent sessionComponent &&
                             MyDefinitionManager.TryGet<MySessionComponentDefinition>(sessionComponent.DefinitionId, out var def)
                    ? CreateDefinitionContext(def, builder)
                    : (Offset<DefinitionContext>?) null;
                var typeString = builder.CreateSharedString(component.GetType().Name);
                SceneComponentContext.StartSceneComponentContext(builder);
                SceneComponentContext.AddType(builder, typeString);
                if (defCtx.HasValue)
                    SceneComponentContext.AddDefinition(builder, defCtx.Value);
                return SceneComponentContext.EndSceneComponentContext(builder);
            }

            public void Log(in NamedLogger source, LogSeverity severity, object message)
            {
                var buffer = Program.Instance.Channel.SendBuffer;
                var builder = buffer.Builder;
                unsafe
                {
                    var origin = CreateSharedString(source.Name, builder);
                    var threadName = CreateSharedString(Thread.CurrentThread.Name, builder);
                    StringOffset? format = null;

                    int? contextOffset = null;
                    var contextType = LogContext.NONE;
                    switch (source.Context)
                    {
                        case MyDefinitionBase definition:
                            contextType = LogContext.DefinitionContext;
                            contextOffset = CreateDefinitionContext(definition, builder).Value;
                            break;
                        case MyEntityComponent component:
                            contextType = LogContext.EntityComponentContext;
                            contextOffset = CreateEntityContext(component, builder).Value;
                            break;
                        case VRage.Scene.IMySceneComponent component:
                            contextType = LogContext.SceneComponentContext;
                            contextOffset = CreateSessionContext(component, builder).Value;
                            break;
                    }

                    var argCount = 0;
                    var argOffsets = stackalloc int[StackArgLength];
                    var argTypes = stackalloc LogArg[StackArgLength];
                    _argOverflow.Clear();

                    switch (message)
                    {
                        case FormattableString fmtString:
                            format = CreateSharedString(fmtString.Format, builder);
                            argCount = fmtString.ArgumentCount;
                            for (var i = 0; i < argCount; i++)
                            {
                                var arg = fmtString.GetArgument(i);
                                var argOffset = CreateArg(arg, builder, out var argType);
                                if (i < StackArgLength)
                                {
                                    argOffsets[i] = argOffset;
                                    argTypes[i] = argType;
                                }
                                else
                                    _argOverflow.Add(new KeyValuePair<int, LogArg>(argOffset, argType));
                            }

                            break;
                        case string _:
                        case StringBuilder _:
                            format = CreateSharedString(message.ToString(), builder);
                            break;
                        default:
                            format = CreateSharedString("{0}", builder);
                            argCount = 1;
                            argOffsets[0] = CreateArg(message, builder, out argTypes[0]);
                            break;
                    }


                    VectorOffset? argOffsetVector = null;
                    VectorOffset? argTypeVector = null;
                    if (argCount > 0)
                    {
                        StructuredLogMessage.StartArgsVector(builder, argCount);
                        for (var i = argCount - 1; i >= 0; i--)
                            builder.AddOffset(i < StackArgLength ? argOffsets[i] : _argOverflow[i - StackArgLength].Key);
                        argOffsetVector = builder.EndVector();

                        StructuredLogMessage.StartArgsTypeVector(builder, argCount);
                        for (var i = argCount - 1; i >= 0; i--)
                            builder.AddByte((byte) (i < StackArgLength ? argTypes[i] : _argOverflow[i - StackArgLength].Value));
                        argTypeVector = builder.EndVector();
                    }

                    _argOverflow.Clear();

                    StructuredLogMessage.StartStructuredLogMessage(builder);
                    StructuredLogMessage.AddTimeMs(builder, DateTimeOffset.Now.ToUnixTimeMilliseconds());
                    if (origin.HasValue)
                        StructuredLogMessage.AddOrigin(builder, origin.Value);
                    if (threadName.HasValue)
                        StructuredLogMessage.AddThread(builder, threadName.Value);
                    StructuredLogMessage.AddSeverity(builder, SeverityTable.GetValueOrDefault(severity, Shared.Data.LogSeverity.Info));
                    if (format.HasValue)
                        StructuredLogMessage.AddFormat(builder, format.Value);
                    if (argOffsetVector.HasValue)
                    {
                        StructuredLogMessage.AddArgs(builder, argOffsetVector.Value);
                        StructuredLogMessage.AddArgsType(builder, argTypeVector.Value);
                    }

                    if (contextOffset.HasValue)
                    {
                        StructuredLogMessage.AddContext(builder, contextOffset.Value);
                        StructuredLogMessage.AddContextType(builder, contextType);
                    }

                    buffer.EndMessage(Message.StructuredLogMessage);
                }
            }
        }

        private sealed class Logger : TextLogger
        {
            private readonly ThreadLocal<LogOutputHelper> _helper = new ThreadLocal<LogOutputHelper>(() => new LogOutputHelper());

            public Logger(string pathName) : base(pathName, new ThrowingStream())
            {
            }

            protected override void LogInternal(in NamedLogger source, LogSeverity severity, object message)
            {
                _helper.Value.Log(in source, severity, message);
            }

            public override void OpenBlock(in NamedLogger source, string message)
            {
                _helper.Value.OpenBlock(in source, message);
            }

            public override void CloseBlock(in NamedLogger source, string message = null)
            {
                _helper.Value.CloseBlock(in source, message);
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