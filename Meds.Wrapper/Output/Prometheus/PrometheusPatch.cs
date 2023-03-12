using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using HarmonyLib;
using Meds.Metrics;
using VRage.Dedicated.RemoteAPI;

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
}