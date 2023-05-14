using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.ExceptionServices;
using HarmonyLib;
using Medieval.ObjectBuilders;
using MedievalEngineersDedicated;
using Meds.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sandbox;
using Sandbox.Engine.Analytics;
using VRage.Dedicated;
using VRage.Game;
using VRage.Scripting;
using VRage.Session;
using VRage.Systems;
using VRage.Utils;
using ZLogger;

// ReSharper disable RedundantAssignment
// ReSharper disable InconsistentNaming

namespace Meds.Wrapper.Shim
{
    public static class PatchHelper
    {
        private static readonly Harmony _harmony = new Harmony("meds.wrapper.core");
        private static bool ReplaceLogger;

        static PatchHelper()
        {
        }

        public static void PatchAlways(bool late)
        {
            foreach (var type in typeof(PatchHelper).Assembly.GetTypes())
            {
                var attr = type.GetCustomAttribute<AlwaysPatch>();
                if (attr == null || attr.Late != late) continue;
                Patch(type);
            }

            if (!late && ReplaceLogger)
            {
                Patch(typeof(LoggerPatches.PatchLogger1));
                Patch(typeof(LoggerPatches.PatchLogger2));
                Patch(typeof(LoggerPatches.PatchLogger3));
                Patch(typeof(LoggerPatches.PatchLogger4));
            }
        }

        public static void PatchStartup(bool replaceLogger)
        {
            Patch(typeof(PatchWaitForKey));
            Patch(typeof(PatchConfigSetup));
            Patch(typeof(PatchMinidump));
            ReplaceLogger = replaceLogger;
            if (replaceLogger)
                Patch(typeof(LoggerPatches.PatchReplaceLogger));
        }

        public static void Transpile(MethodBase target, MethodInfo transpiler)
        {
            try
            {
                var processor = _harmony.CreateProcessor(target);
                processor.AddTranspiler(transpiler);
                processor.Patch();
            }
            catch (Exception err)
            {
                throw new Exception($"Failed to transpile {target} with {transpiler}", err);
            }
        }

        public static void Prefix(MethodBase target, MethodInfo prefix)
        {
            try
            {
                var processor = _harmony.CreateProcessor(target);
                processor.AddPrefix(prefix);
                processor.Patch();
            }
            catch (Exception err)
            {
                throw new Exception($"Failed to transpile {target} with {prefix}", err);
            }
        }

        public static void Patch(Type type)
        {
            var results = _harmony.CreateClassProcessor(type).Patch().Select(x => x?.Name).Where(x => x != null)
                .ToList();
            if (results.Count > 0)
            {
                Entrypoint.LoggerFor(typeof(PatchHelper))?
                    .ZLogInformationWithPayload(results, "Applied patch {0} ", type.FullName);
            }
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
            private static readonly FieldInfo IsConsoleVisible =
                AccessTools.Field(typeof(MySandboxGame), "IsConsoleVisible");

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

        [HarmonyPatch(typeof(MinidumpSystem), nameof(MinidumpSystem.Init), typeof(MinidumpSystem.Params))]
        public static class PatchMinidump
        {
            private const string AccessViolation = "exception.accessViolation";

            static PatchMinidump()
            {
                AppDomain.CurrentDomain.FirstChanceException += (sender, args) =>
                {
                    if (args.Exception is AccessViolationException ||
                        args.Exception?.InnerException is AccessViolationException)
                        MinidumpSystem.MaybeCollectStatic(AccessViolation, out _);
                };
            }

            public static void Prefix(MinidumpSystem.Params configuration)
            {
                var dir = Entrypoint.Config.Install.DiagnosticsDirectory;
                if (dir != null)
                    configuration.Directory = dir;
                configuration.DefaultAction = MinidumpSystem.MinidumpAction.DumpThreads;
                configuration.MaximumSpaceMb = 50 * 1024;
                configuration.Cases ??= new List<MinidumpSystem.MinidumpCase>();
                configuration.Cases.InsertRange(0, new[]
                {
                    new MinidumpSystem.MinidumpCase
                    {
                        Trigger = "crash",
                        Action = MinidumpSystem.MinidumpAction.None,
                    },
                    new MinidumpSystem.MinidumpCase
                    {
                        Trigger = "exception.any",
                        Action = MinidumpSystem.MinidumpAction.None,
                    },
                    new MinidumpSystem.MinidumpCase
                    {
                        Trigger = AccessViolation,
                        Action = MinidumpSystem.MinidumpAction.DumpHeap
                    }
                });
            }
        }

        [HarmonyPatch(typeof(MyAnalyticsManager), nameof(MyAnalyticsManager.RegisterAnalyticsTracker))]
        [AlwaysPatch]
        public static class DisableAnalytics
        {
            public static bool Prefix() => false;
        }

        [HarmonyPatch(typeof(MyScriptCompiler), MethodType.Constructor, typeof(MyScriptCompilerConfig))]
        [AlwaysPatch(Late = false)]
        public static class PatchScriptCompiler
        {
            public static void Postfix(MyScriptCompiler __instance)
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
            }
        }

        public static string SubtypeOrDefault(MyDefinitionId id) => id.SubtypeId == MyStringHash.NullOrEmpty ? "default" : id.SubtypeId.String;
    }
}