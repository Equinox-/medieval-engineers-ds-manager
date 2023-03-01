using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text;
using HarmonyLib;
using Medieval.ObjectBuilders;
using MedievalEngineersDedicated;
using Meds.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Sandbox;
using Sandbox.Engine.Analytics;
using Sandbox.Engine.Physics;
using VRage.Dedicated;
using VRage.Game;
using VRage.Logging;
using VRage.Scripting;
using VRage.Session;

// ReSharper disable RedundantAssignment
// ReSharper disable InconsistentNaming

namespace Meds.Wrapper.Shim
{
    public static class Patches
    {
        private static readonly Harmony _harmony = new Harmony("meds.wrapper.core");

        public static void PatchAlways(bool late)
        {
            foreach (var type in typeof(Patches).Assembly.GetTypes())
            {
                var attr = type.GetCustomAttribute<AlwaysPatch>();
                if (attr == null || attr.Late != late) continue;
                Patch(type);
            }

            if (!late && Entrypoint.Instance.Services.GetRequiredService<Configuration>().Install.ReplaceLogger)
            {
                Patch(typeof(PatchLogger1));
                Patch(typeof(PatchLogger2));
                Patch(typeof(PatchLogger3));
                Patch(typeof(PatchLogger4));
            }
        }

        public static void PatchStartup()
        {
            Patch(typeof(PatchWaitForKey));
            Patch(typeof(PatchConfigSetup));
            Patch(typeof(PatchReplaceLogger));
        }

        public static void Patch(Type type)
        {
            _harmony.CreateClassProcessor(type).Patch();
        }

        public static IEnumerable<(MyModContext mod, Type type)> ModTypes(string typeName)
        {
            foreach (var kv in MySession.Static.ModManager.Assemblies)
            {
                var type = kv.Value.GetType(typeName);
                if (type == null) continue;
                yield return (kv.Key, type);
            }
        }

        [HarmonyPatch(typeof(DedicatedServer<MyObjectBuilder_MedievalSessionSettings>), "RunInternal")]
        public static class PatchWaitForKey
        {
            private static readonly FieldInfo IsConsoleVisible = AccessTools.Field(typeof(MySandboxGame), "IsConsoleVisible");

            public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                foreach (var instruction in instructions)
                {
                    if (instruction.Is(OpCodes.Ldsfld, IsConsoleVisible))
                    {
                        yield return new CodeInstruction(OpCodes.Ldc_I4_0)
                            .MoveBlocksFrom(instruction)
                            .MoveLabelsFrom(instruction);
                    }
                    else
                    {
                        yield return instruction;
                    }
                }
            }
        }

        [HarmonyPatch(typeof(MyMedievalDedicatedCompatSystem), "Init")]
        public static class PatchConfigSetup
        {
            public static void Postfix()
            {
                MySandboxGame.ConfigDedicated = new WorldChangingConfigReplacer(
                    Entrypoint.Instance.Services.GetService<Configuration>(),
                    MySandboxGame.ConfigDedicated);
            }
        }

        [HarmonyPatch(typeof(MyLog), "Init")]
        public static class PatchReplaceLogger
        {
            public static void Postfix(MyLog __instance)
            {
                Entrypoint.Instance.Services.GetService<ShimLog>().BindTo(__instance);
            }
        }

        // No need to log process information since we track that with metrics.
        [HarmonyPatch(typeof(MyLog), "WriteProcessInformation")]
        [AlwaysPatch]
        public static class DisableProcessInfoLogging
        {
            public static bool Prefix() => false;
        }

        // No need to log physics information since we track that with metrics.
        [HarmonyPatch(typeof(MyPhysicsSandbox), "LogPhysics")]
        [AlwaysPatch]
        public static class DisablePhysicsLogging
        {
            public static bool Prefix() => false;
        }

        [HarmonyPatch(typeof(MyAnalyticsManager), nameof(MyAnalyticsManager.RegisterAnalyticsTracker))]
        [AlwaysPatch]
        public static class DisableAnalytics
        {
            public static bool Prefix() => false;
        }

        [HarmonyPatch(typeof(MyLog), "Log", typeof(LogSeverity), typeof(StringBuilder))]
        public static class PatchLogger1
        {
            public static bool Prefix(MyLog __instance, LogSeverity severity, StringBuilder builder)
            {
                __instance.Logger.Log(in ShimLog.LoggerLegacy, severity, builder);
                return false;
            }
        }
        
        [HarmonyPatch(typeof(MyLog), "Log", typeof(LogSeverity), typeof(string))]
        public static class PatchLogger2
        {
            public static bool Prefix(MyLog __instance, LogSeverity severity, string message)
            {
                __instance.Logger.Log(in ShimLog.LoggerLegacy, severity, message);
                return false;
            }
        }
        
        [HarmonyPatch(typeof(MyLog), "Log", typeof(LogSeverity), typeof(string), typeof(object[]))]
        public static class PatchLogger3
        {
            public static bool Prefix(MyLog __instance, LogSeverity severity, string format, object[] args)
            {
                __instance.Logger.Log(in ShimLog.LoggerLegacy, severity, FormattableStringFactory.Create(format, args));
                return false;
            }
        }
        
        [HarmonyPatch(typeof(MyLog), "WriteLineAndConsole", typeof(string))]
        public static class PatchLogger4
        {
            public static bool Prefix(MyLog __instance, string msg)
            {
                __instance.Logger.Log(in ShimLog.LoggerLegacy, LogSeverity.Info, msg);
                return false;
            }
        }

        [HarmonyPatch(typeof(MyScriptCompiler), MethodType.Constructor, typeof(MyScriptCompilerConfig))]
        [AlwaysPatch(Late = false)]
        public static class PatchScriptCompiler
        {
            public static bool Prefix(MyScriptCompiler __instance)
            {
                __instance.AddConditionalCompilationSymbols("MEDS_API");
                __instance.AddReferencedAssemblies(typeof(PatchScriptCompiler).Assembly);
                var debug = Entrypoint.Config.Install.Adjustments.ModDebug;
                if (debug.HasValue)
                    __instance.EnableDebugInformation = debug.Value;
                using (var batch = __instance.Whitelist.OpenWhitelistBatch())
                {
                    batch.AddRecursiveNamespaceOfTypes(typeof(MetricRegistry));
                    batch.AddTypes(typeof(MedsModApi));
                }
                return false;
            }
        }
    }
}