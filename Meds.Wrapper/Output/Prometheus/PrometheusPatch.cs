using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Meds.Metrics;
using Meds.Wrapper.Shim;
using VRage.Dedicated.RemoteAPI;
using VRage.Logging;

namespace Meds.Wrapper.Output.Prometheus
{
    [HarmonyPatch(typeof(MyRemoteServer), "Process")]
    public sealed class PrometheusPatch
    {
        private const string BearerPrefix = "Bearer";

        public static bool Prefix(HttpListenerContext context)
        {
            if (context.Request.RawUrl != "/vrageremote/metrics") return true;
            var requiredAuth = Entrypoint.Config.Metrics.PrometheusKey;
            if (requiredAuth != "")
            {
                var auth = context.Request.Headers["Authorization"];
                if (auth == null
                    || !auth.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase)
                    || !auth.AsSpan().Slice(BearerPrefix.Length).Trim().SequenceEqual(requiredAuth.AsSpan()))
                {
                    context.Response.StatusCode = 401;
                    return false;
                }
            }

            context.Response.StatusCode = 200;
            context.Response.ContentType = "text/plain; version=0.0.4";
            context.Response.AddHeader("Content-Encoding", "gzip");
            using var writer = new StreamWriter(new GZipStream(context.Response.OutputStream, CompressionLevel.Fastest, false));
            writer.NewLine = "\n";
            var formatter = new PrometheusMetricWriter(writer);
            var metrics = MetricRegistry.Read();
            // ReSharper disable once ForCanBeConvertedToForeach
            for (var i = 0; i < metrics.Count; i++)
            {
                metrics[i].WriteTo(formatter);
            }

            return false;
        }
    }

    [HarmonyPatch(typeof(MyRemoteServer), "Listen")]
    public sealed class RemoteServerLessLogging
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var removeLogTriggers = new[] { "Received request from", "URL:", "HTTP Method", "Request processed in" };
            var removingLog = false;
            foreach (var instruction in instructions)
            {
                if (instruction.opcode == OpCodes.Ldstr && instruction.operand is string str && removeLogTriggers.Any(trigger => str.Contains(trigger)))
                    removingLog = true;
                if (removingLog
                    && instruction.operand is MethodInfo { IsStatic: false } method
                    && method.ReturnType == typeof(void)
                    && (method.DeclaringType == typeof(MyLog) || method.DeclaringType == typeof(NamedLogger))
                    && !(method.Name == nameof(NamedLogger.OpenBlock) || method.Name == nameof(MyLog.IncreaseIndent) ||
                         method.Name == nameof(MyLog.DecreaseIndent)))
                {
                    yield return instruction.ChangeInstruction(OpCodes.Nop);
                    var pops = 1 + method.GetParameters().Length;
                    for (var i = 0; i < pops; i++)
                        yield return new CodeInstruction(OpCodes.Pop);
                    removingLog = false;
                    continue;
                }

                yield return instruction;
            }
        }
    }
}