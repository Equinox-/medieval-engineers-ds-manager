using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Reflection.Emit;
using HarmonyLib;
using Meds.Metrics;
using VRage.Dedicated.RemoteAPI;

namespace Meds.Wrapper.Output.Prometheus
{
    [HarmonyPatch(typeof(MyRemoteServer), "ProcessController")]
    public sealed class PrometheusPatch
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> original,
            ILGenerator il)
        {
            // var result = Implementation(context)
            yield return new CodeInstruction(OpCodes.Ldarg_1);
            yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(PrometheusPatch), nameof(Implementation)));
            var originalCode = il.DefineLabel();
            // if (!result) {
            yield return new CodeInstruction(OpCodes.Brtrue, originalCode);
            // return RequestProcessed;
            yield return new CodeInstruction(OpCodes.Ldc_I4_1);
            yield return new CodeInstruction(OpCodes.Ret);
            // }
            yield return new CodeInstruction(OpCodes.Nop).WithLabels(originalCode);
            foreach (var arg in original)
                yield return arg;
            
        }
        
        private static bool Implementation(HttpListenerContext context)
        {
            if (context.Request.RawUrl != "/vrageremote/metrics") return true;
            context.Response.StatusCode = 200;
            context.Response.ContentType = "text/plain; version=0.0.4";
            context.Response.AddHeader("Content-Encoding", "gzip");
            using (var writer = new StreamWriter(new GZipStream(context.Response.OutputStream, CompressionLevel.Fastest, false)))
            {
                writer.NewLine = "\n";
                var formatter = new PrometheusMetricWriter(writer);
                var metrics = MetricRegistry.Read();
                // ReSharper disable once ForCanBeConvertedToForeach
                for (var i = 0; i < metrics.Count; i++)
                {
                    metrics[i].WriteTo(formatter);
                }
            }
            return false;
        }
    }
}