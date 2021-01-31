using System;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Meds.Shared;
using Meds.Shared.Data;
using NLog;

namespace Meds.Watchdog.Logs
{
    public sealed class LogSink : IDisposable
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        private readonly BlockingCollection<PacketDistributor.MessageToken> _messages = new BlockingCollection<PacketDistributor.MessageToken>();
        private readonly Thread _task;
        private volatile bool _disposed;

        public LogSink(Program pgm)
        {
            pgm.Distributor.RegisterPacketHandler(Consume, Message.StructuredLogMessage);
            _task = new Thread(Execute) {Name = "log-sink"};
            _task.Start();
        }

        public void Consume(PacketDistributor.MessageToken msg)
        {
            // _messages.Add(msg.AddRef());
            Print(msg);
        }

        private void Print(PacketDistributor.MessageToken msg)
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
                    var dc = logMsg.Context<DefinitionContext>().Value;
                    context = "[" + dc.Type + "/" + dc.Subtype + "] ";
                    break;
                case LogContext.EntityComponentContext:
                    var cc = logMsg.Context<EntityComponentContext>().Value;
                    context = "[" + cc.Type + "/" + cc.Subtype + "@" + cc.Entity + "] ";
                    break;
                case LogContext.SceneComponentContext:
                    var sc = logMsg.Context<SceneComponentContext>().Value;
                    context = "[" + sc.Type + "] ";
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            var evt = new LogEventInfo
            {
                Level = ConvertSeverity(logMsg.Severity),
                LoggerName = logMsg.Origin,
                TimeStamp = DateTimeOffset.FromUnixTimeMilliseconds(logMsg.Time).DateTime,
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

        private void Execute()
        {
            while (!_disposed)
            {
                var msg = _messages.Take();
                using (msg)
                {
                    Print(msg);
                }
            }
        }

        public void Dispose()
        {
            _disposed = true;
        }
    }
}