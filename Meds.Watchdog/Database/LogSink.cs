using System;
using System.Text;
using System.Threading;
using Meds.Shared;
using Meds.Shared.Data;
using NLog;

namespace Meds.Watchdog.Database
{
    public static class LogSink
    {
        private const string Series = "me.log.messages";
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private static readonly ThreadLocal<StringBuilder> StringBuilderPool = new ThreadLocal<StringBuilder>(() => new StringBuilder());
        private static readonly string[] LogSeverityNames;

        static LogSink()
        {
            var values = (LogSeverity[]) typeof(LogSeverity).GetEnumValues();
            var maxI = 0;
            foreach (var val in values)
                maxI = Math.Max(maxI, (int) val);
            LogSeverityNames = new string[maxI + 1];
            foreach (var val in values)
                LogSeverityNames[(int) val] = val.ToString();
        }

        private static string GetLogSeverityName(LogSeverity val)
        {
            var i = (int) val;
            return (i >= 0 && i < LogSeverityNames.Length ? LogSeverityNames[i] : null) ?? "Unknown";
        }

        public static void Register(Program pgm)
        {
            pgm.Distributor.RegisterPacketHandler(msg => Consume(pgm.Influx, msg), Message.StructuredLogMessage);
        }

        private static void Consume(Influx influx, PacketDistributor.MessageToken msg)
        {
            Print(msg);
            using (var writer = influx.Write(Series))
            {
                EncodeInflux(writer, msg.Value<StructuredLogMessage>());
            }
        }

        private static void WriteLogArgs(StreamingInfluxWriter sb, StructuredLogMessage logMsg)
        {
            for (var i = 0; i < logMsg.ArgsLength; i++)
            {
                switch (logMsg.ArgsType(i))
                {
                    case LogArg.String:
                    {
                        var val = logMsg.Args<StringLogArg>(i).Value;
                        sb.WriteLogArg("argS", i, val.Value);
                        break;
                    }
                    case LogArg.Int64:
                    {
                        var val = logMsg.Args<Int64LogArg>(i).Value;
                        sb.WriteLogArg("argD", i, val.Value);
                        break;
                    }
                    case LogArg.Float64:
                    {
                        var val = logMsg.Args<Float64LogArg>(i).Value;
                        sb.WriteLogArg("argF", i, val.Value);
                        break;
                    }
                    case LogArg.Exception:
                    {
                        var val = logMsg.Args<ExceptionLogArg>(i).Value;
                        sb.WriteLogArg("exType", i, val.Type);
                        sb.WriteLogArg("exMsg", i, val.Message);
                        sb.WriteLogArg("exStack", i, val.Stack);
                        break;
                    }
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }
        }

        private static void WriteDefCtxValues(StreamingInfluxWriter sb, DefinitionContext def)
        {
            sb.WriteVal("defType", def.Type, true);
            sb.WriteVal("defSubtype", def.Subtype, true);
        }

        private static void WriteLogContextTags(StreamingInfluxWriter sb, StructuredLogMessage logMsg)
        {
            switch (logMsg.ContextType)
            {
                case LogContext.NONE:
                    break;
                case LogContext.DefinitionContext:
                {
                    var dc = logMsg.Context<DefinitionContext>().Value;
                    sb.WriteTag("package", dc.Package, true);
                    break;
                }
                case LogContext.EntityComponentContext:
                {
                    var cc = logMsg.Context<EntityComponentContext>().Value;
                    var def = cc.Definition;
                    if (def.HasValue)
                        sb.WriteTag("package", def.Value.Package, true);
                    break;
                }
                case LogContext.SceneComponentContext:
                {
                    var sc = logMsg.Context<SceneComponentContext>().Value;
                    var def = sc.Definition;
                    if (def.HasValue)
                        sb.WriteTag("package", def.Value.Package, true);
                    break;
                }
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        private static void WriteLogContextValues(StreamingInfluxWriter sb, StructuredLogMessage logMsg)
        {
            switch (logMsg.ContextType)
            {
                case LogContext.NONE:
                    break;
                case LogContext.DefinitionContext:
                {
                    var dc = logMsg.Context<DefinitionContext>().Value;
                    WriteDefCtxValues(sb, dc);
                    break;
                }
                case LogContext.EntityComponentContext:
                {
                    var cc = logMsg.Context<EntityComponentContext>().Value;
                    sb.WriteVal("compType", cc.Type, true);
                    sb.WriteVal("compEntity", (long) cc.Entity, true);
                    var def = cc.Definition;
                    if (def.HasValue)
                        WriteDefCtxValues(sb, def.Value);
                    break;
                }
                case LogContext.SceneComponentContext:
                {
                    var sc = logMsg.Context<SceneComponentContext>().Value;
                    sb.WriteVal("compType", sc.Type, true);
                    var def = sc.Definition;
                    if (def.HasValue)
                        WriteDefCtxValues(sb, def.Value);
                    break;
                }
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        public static void EncodeInflux(StreamingInfluxWriter sb, StructuredLogMessage logMsg)
        {
            sb.WriteVal("format", logMsg.Format, true);
            WriteLogContextValues(sb, logMsg);
            WriteLogArgs(sb, logMsg);
            if (!sb.HasValues)
                return;
            sb.WriteTag("severity", GetLogSeverityName(logMsg.Severity), true);
            sb.WriteTag("origin", logMsg.Origin, true);
            sb.WriteTag("thread", logMsg.Thread, true);
            WriteLogContextTags(sb, logMsg);
            sb.TimeMs = logMsg.TimeMs;
        }

        private static void Print(PacketDistributor.MessageToken msg)
        {
            var logMsg = msg.Value<StructuredLogMessage>();
            var args = new object[logMsg.ArgsLength];
            for (var i = 0; i < args.Length; i++)
            {
                switch (logMsg.ArgsType(i))
                {
                    case LogArg.NONE:
                        args[i] = null;
                        break;
                    case LogArg.String:
                        args[i] = logMsg.Args<StringLogArg>(i)?.Value;
                        break;
                    case LogArg.Int64:
                        args[i] = logMsg.Args<Int64LogArg>(i)?.Value;
                        break;
                    case LogArg.Float64:
                        args[i] = logMsg.Args<Float64LogArg>(i)?.Value;
                        break;
                    case LogArg.Exception:
                        var ex = logMsg.Args<ExceptionLogArg>(i);
                        if (ex.HasValue) args[i] = ex.Value.Type + "(" + ex.Value.Message + ")\n" + ex.Value.Stack;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }

            var context = "";
            switch (logMsg.ContextType)
            {
                case LogContext.NONE:
                    break;
                case LogContext.DefinitionContext:
                {
                    var dc = logMsg.Context<DefinitionContext>().Value;
                    context = "[" + dc.Type + "/" + dc.Subtype + "] ";
                    break;
                }
                case LogContext.EntityComponentContext:
                {
                    var cc = logMsg.Context<EntityComponentContext>().Value;
                    context = "[" + cc.Type + "@" + cc.Entity;
                    var def = cc.Definition;
                    if (def.HasValue)
                        context += " " + def.Value.Subtype + " " + def.Value.Package;
                    context += "] ";
                    break;
                }
                case LogContext.SceneComponentContext:
                {
                    var sc = logMsg.Context<SceneComponentContext>().Value;
                    context = "[" + sc.Type;
                    var def = sc.Definition;
                    if (def.HasValue)
                        context += " " + def.Value.Subtype + " " + def.Value.Package;
                    context += "] ";
                    break;
                }
                default:
                    throw new ArgumentOutOfRangeException();
            }

            var evt = new LogEventInfo
            {
                Level = ConvertSeverity(logMsg.Severity),
                LoggerName = logMsg.Origin,
                TimeStamp = DateTimeOffset.FromUnixTimeMilliseconds(logMsg.TimeMs).DateTime,
                Parameters = args,
                Message = logMsg.Format ?? "{}"
            };
            if (!string.IsNullOrEmpty(context))
                evt.Properties["context"] = context;
            var thread = logMsg.Thread;
            if (!string.IsNullOrEmpty(thread))
                evt.Properties["thread"] = thread;
            Log.Log(evt);
        }

        private static LogLevel ConvertSeverity(LogSeverity severity)
        {
            switch (severity)
            {
                case LogSeverity.Debug:
                    return LogLevel.Debug;
                case LogSeverity.Info:
                    return LogLevel.Info;
                case LogSeverity.Warning:
                    return LogLevel.Warn;
                case LogSeverity.Error:
                    return LogLevel.Error;
                case LogSeverity.Critical:
                    return LogLevel.Fatal;
                case LogSeverity.PreFormatted:
                    return LogLevel.Info;
                default:
                    throw new ArgumentOutOfRangeException(nameof(severity), severity, null);
            }
        }
    }
}