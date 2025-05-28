using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Medieval.ObjectBuilders;
using MedievalEngineersDedicated;
using Meds.Metrics;
using Meds.Shared;
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
        private static readonly HashSet<string> SuppressedPatches = new HashSet<string>();
        private static readonly HashSet<string> RequestedPatches = new HashSet<string>();
        private static ILogger Log => Entrypoint.LoggerFor(typeof(PatchHelper));

        static PatchHelper()
        {
        }

        public static void PatchAlways(bool late)
        {
            foreach (var type in typeof(PatchHelper).Assembly.GetTypes())
            {
                var attr = type.GetCustomAttribute<AlwaysPatchAttribute>();
                if (attr == null || attr.Late != late) continue;
                if (!attr.CanUse()) continue;
                if (!string.IsNullOrEmpty(attr.ByRequest) && !RequestedPatches.Contains(attr.ByRequest))
                {
                    Log?.ZLogInformation("Skipping patch {0} ({1} was not requested)", type.FullName, attr.ByRequest);
                    continue;
                }

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

        public static void PatchStartup(RenderedInstallConfig cfg)
        {
            ReplaceLogger = cfg.Adjustments.ReplaceLogger ?? false;
            SuppressedPatches.Clear();
            RequestedPatches.Clear();
            if (cfg.Adjustments.SuppressPatch != null)
                foreach (var patch in cfg.Adjustments.SuppressPatch)
                    SuppressedPatches.Add(patch);
            if (cfg.Adjustments.RequestPatch != null)
                foreach (var patch in cfg.Adjustments.RequestPatch)
                    RequestedPatches.Add(patch);

            Patch(typeof(PatchWaitForKey));
            Patch(typeof(PatchConfigSetup));
            Patch(typeof(PatchMinidump));
            if (ReplaceLogger)
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
            var declaringType = prefix.DeclaringType;
            if (declaringType != null && (SuppressedPatches.Contains(declaringType.Name) || SuppressedPatches.Contains(declaringType.FullName)))
            {
                Log?.ZLogInformation("Suppressing patch {0}", declaringType.FullName);
                return;
            }

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
            if (SuppressedPatches.Contains(type.Name) || SuppressedPatches.Contains(type.FullName))
            {
                Log?.ZLogInformation("Suppressing patch {0}", type.FullName);
                return;
            }

            var results = _harmony.CreateClassProcessor(type).Patch().Select(x => x?.Name).Where(x => x != null).ToList();
            if (results.Count > 0)
                Log?.ZLogInformationWithPayload(results, "Applied patch {0} ", type.FullName);
            else
                Log?.ZLogInformationWithPayload(results, "Applying patch {0} produced no results", type.FullName);
        }

        public static IEnumerable<(MyModContext mod, Type type)> ModTypes(string typeName)
        {
            var mods = MySession.Static?.ModManager;
            if (mods == null)
            {
                Entrypoint.LoggerFor(typeof(PatchHelper))?
                    .ZLogWarning("Tried to fetch mod types {0} but mod manager isn't initialized", typeName);
                yield break;
            }

            foreach (var kv in mods.Assemblies)
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
            public static void Prefix(MinidumpSystem.Params configuration)
            {
                var cfg = Entrypoint.Config.Install.Adjustments?.Minidump;
                var dir = Entrypoint.Config.Install.DiagnosticsDirectory;
                if (dir != null)
                    configuration.Directory = dir;
                configuration.DefaultAction = ToVRage(cfg?.DefaultAction ?? MinidumpConfig.Action.DumpThreads);
                configuration.MaximumSpaceMb = cfg?.MaximumSpaceMb ?? 50 * 1024;
                configuration.Cases ??= new List<MinidumpSystem.MinidumpCase>();
                configuration.Cases.InsertRange(0, new[]
                {
                    new MinidumpSystem.MinidumpCase
                    {
                        Trigger = "crash",
                        Action = MinidumpSystem.MinidumpAction.None,
                    }
                });
                if (cfg?.Cases != null)
                    configuration.Cases.InsertRange(0, cfg.Cases.Select(ToVRage));
            }

            private static MinidumpSystem.MinidumpCase ToVRage(MinidumpConfig.Case val) => new MinidumpSystem.MinidumpCase
            {
                Trigger = val.Trigger,
                Action = ToVRage(val.Action),
            };

            private static MinidumpSystem.MinidumpAction ToVRage(MinidumpConfig.Action val)
            {
                switch (val)
                {
                    case MinidumpConfig.Action.DumpThreads:
                        return MinidumpSystem.MinidumpAction.DumpThreads;
                    case MinidumpConfig.Action.DumpHeap:
                        return MinidumpSystem.MinidumpAction.DumpHeap;
                    case MinidumpConfig.Action.None:
                    default:
                        return MinidumpSystem.MinidumpAction.None;
                }
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