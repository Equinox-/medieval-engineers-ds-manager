using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Meds.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sandbox.Game.World;
using ZLogger;

// ReSharper disable InconsistentNaming

namespace Meds.Wrapper.Shim
{
    public sealed class TieredBackups
    {
        // Dummy tier to keep the latest 5 saves in all circumstances.
        private static readonly Tier TierZero = new Tier(5, TimeSpan.FromTicks(1));
        private static TieredBackups Instance => Entrypoint.Instance.Services.GetRequiredService<TieredBackups>();

        private readonly Refreshable<Tier[]> _tiers;
        private readonly ILogger<TieredBackups> _log;

        public TieredBackups(Refreshable<RenderedRuntimeConfig> cfg, ILogger<TieredBackups> log)
        {
            _tiers = cfg
                .Map(x => x.Backup ?? new BackupConfig())
                .Map(CreateTiers);
            _log = log;
        }

        private static Tier[] CreateTiers(BackupConfig x)
        {
            if (x.Tiers != null && x.Tiers.Count > 0)
            {
                var tiers = x.Tiers.Where(y => y.Count > 0)
                    .Select(y => new Tier(y.Count, y.Interval))
                    .Where(y => y.Interval > TimeSpan.Zero)
                    .OrderBy(y => y.Interval)
                    .ToList();
                if (tiers.Count > 0)
                {
                    tiers.Insert(0, TierZero);
                    return tiers.ToArray();
                }
            }

            if (x.DefaultTiers)
            {
                return new[]
                {
                    TierZero,
                    new Tier(30, TimeSpan.FromMinutes(5)),
                    new Tier(10, TimeSpan.FromHours(1)),
                    new Tier(10, TimeSpan.FromHours(3)),
                    new Tier(10, TimeSpan.FromHours(6)),
                    new Tier(10, TimeSpan.FromHours(12)),
                    new Tier(10, TimeSpan.FromHours(24)),
                };
            }

            return Array.Empty<Tier>();
        }

        private sealed class Tier
        {
            public readonly int Count;
            public readonly TimeSpan Interval;

            public Tier(int count, TimeSpan interval)
            {
                Count = count;
                Interval = interval;
            }
        }

        private void ApplyRetention(List<MySessionBackup.Backup> backups)
        {
            var tiers = _tiers.Current;
            if (tiers.Length == 0 || backups.Count == 0)
                return;
            var partOfTier = new Tier[backups.Count];

            foreach (var tier in tiers)
            {
                var currWindow = long.MaxValue;
                var remaining = tier.Count;
                for (var j = backups.Count - 1; j >= 0 && remaining > 0; j--)
                {
                    var backup = backups[j];
                    var window = backup.Time.Ticks / tier.Interval.Ticks;
                    if (window == currWindow)
                        continue;

                    currWindow = window;
                    partOfTier[j] ??= tier;
                    --remaining;
                }
            }

            for (var j = 0; j < backups.Count; j++)
            {
                var backup = backups[j];
                var tier = partOfTier[j];
                if (tier != null)
                    continue;

                _log.ZLogInformation("Removing backup {0}", backup.DirectoryPath);

                if (backup.IsArchive)
                    File.Delete(backup.DirectoryPath);
                else
                    Directory.Delete(backup.DirectoryPath, true);
            }
        }


        internal static bool Enabled => Instance._tiers.Current.Length > 0;
        internal static void HookApplyRetention(List<MySessionBackup.Backup> backups) => Instance.ApplyRetention(backups);
    }


    [HarmonyPatch(typeof(MySessionBackup), nameof(MySessionBackup.MakeBackup), typeof(MySession), typeof(IEnumerable<string>), typeof(int))]
    [AlwaysPatch]
    public static class TieredBackupsDisableInternalLimit
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator ilg)
        {
            foreach (var instruction in instructions)
            {
                if (instruction.LoadsArg(2))
                {
                    var originalArg = ilg.DefineLabel();
                    var nextCode = ilg.DefineLabel();
                    yield return CodeInstruction.Call(typeof(TieredBackups), "get_" + nameof(TieredBackups.Enabled))
                        .WithLabels(instruction.labels)
                        .WithBlocks(instruction.blocks);
                    yield return new CodeInstruction(OpCodes.Brfalse, originalArg);
                    yield return new CodeInstruction(OpCodes.Ldc_I4, int.MaxValue);
                    yield return new CodeInstruction(OpCodes.Br, nextCode);
                    yield return new CodeInstruction(instruction.opcode, instruction.operand).WithLabels(originalArg);
                    yield return new CodeInstruction(OpCodes.Nop).WithLabels(nextCode);
                    continue;
                }

                yield return instruction;
            }
        }
    }

    [HarmonyPatch(typeof(MySessionBackup), "MakeBackup")]
    public static class TieredBackupsHookRetention
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var instruction in instructions)
            {
                yield return instruction;
                if (!(instruction.operand is MethodInfo { Name: "GetBackups" }))
                    continue;
                yield return new CodeInstruction(OpCodes.Dup);
                yield return CodeInstruction.Call(typeof(TieredBackups), nameof(TieredBackups.HookApplyRetention));
            }
        }
    }
}