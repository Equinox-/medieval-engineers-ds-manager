namespace Meds.Standalone.Collector
{
    public static class TransportLayerMetrics
    {
        private const string MetricPrefix = "me.network.";

        // private static readonly Func<Type> TransportLayerType =
        //     MiscUtils.Memoize(() => Type.GetType("Sandbox.Engine.Multiplayer.MyTransportLayer, Sandbox.Game"));

        public static void Register()
        {
            // try
            // {
            //     var transportLayerField = AccessTools.Field(typeof(MySyncLayer), "TransportLayer");
            //     var getter = AccessTools.FieldRefAccess<MySyncLayer, object>(transportLayerField);
            //     Func<object> transportLayer = () =>
            //     {
            //         var layer = Sync.Layer;
            //         return layer == null ? null : getter(layer);
            //     };
            //     var byteCountReceived = AccessTools.Property(transportLayerField.FieldType, "ByteCountReceived").CreateGetter<object, long>();
            //     var byteCountSent = AccessTools.Property(transportLayerField.FieldType, "ByteCountReceived").CreateGetter<object, long>();
            //     var group = MetricRegistry.Group(MetricName.Of(MetricPrefix + "stats"));
            //
            //     group.Gauge("sent", () =>
            //     {
            //         var transport = transportLayer();
            //         return transport != null ? byteCountSent(transport) : double.NaN;
            //     });
            //     group.Gauge("received", () =>
            //     {
            //         var transport = transportLayer();
            //         return transport != null ? byteCountReceived(transport) : double.NaN;
            //     });
            // }
            // catch
            // {
            //     // ignore errors
            // }
        }

        // [HarmonyPatch]
        // [PatchLate]
        // public static class TransportLayerProcessMessage
        // {
        //     private static readonly PerTickTimerAdder _metric = MetricRegistry.PerTickTimerAdder(MetricName.Of(MetricPrefix + "process"));
        //
        //     public static IEnumerable<MethodBase> TargetMethods()
        //     {
        //         yield return AccessTools.Method(TransportLayerType(), "ProcessMessage");
        //     }
        //
        //     public static void Prefix(out long __state)
        //     {
        //         __state = Stopwatch.GetTimestamp();
        //     }
        //
        //     public static void Postfix(long __state, byte[] data, int dataSize)
        //     {
        //         if (data.Length == 0)
        //             return;
        //         var dt = Stopwatch.GetTimestamp() - __state;
        //         _metric.Record(dt, dataSize);
        //     }
        // }
        //
        // [HarmonyPatch]
        // [PatchLate]
        // public static class TransportLayerSendMessage
        // {
        //     private static readonly string[] ModeNames;
        //
        //     static TransportLayerSendMessage()
        //     {
        //         var max = 0;
        //         foreach (var val in MyEnum<MyP2PMessageEnum>.Values)
        //             max = Math.Max((int) val, max);
        //         ModeNames = new string[max + 1];
        //         foreach (var val in MyEnum<MyP2PMessageEnum>.Values)
        //             ModeNames[(int) val] = val.ToString();
        //     }
        //
        //     private static string NameOf(MyP2PMessageEnum type)
        //     {
        //         var i = (int) type;
        //         return (i >= 0 && i < ModeNames.Length ? ModeNames[i] : null) ?? "unknown";
        //     }
        //
        //     public static IEnumerable<MethodBase> TargetMethods()
        //     {
        //         yield return AccessTools.Method(TransportLayerType(), "SendHandler");
        //     }
        //
        //     public static void Prefix(out long __state)
        //     {
        //         __state = Stopwatch.GetTimestamp();
        //     }
        //
        //     public static void Postfix(long __state, byte[] data, int byteCount, MyP2PMessageEnum msgType)
        //     {
        //         if (data.Length == 0)
        //             return;
        //         var metric = MetricRegistry.PerTickTimerAdder(MetricName.Of(MetricPrefix + "send",
        //             "mode", NameOf(msgType)));
        //
        //         var dt = Stopwatch.GetTimestamp() - __state;
        //         metric.Record(dt, byteCount);
        //     }
        // }
    }
}