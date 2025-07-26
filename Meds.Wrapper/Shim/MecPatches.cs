using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.ExceptionServices;
using System.Security;
using HarmonyLib;
using Medieval.World.Persistence;
using Meds.Wrapper.Utils;
using Microsoft.Extensions.Logging;
using Sandbox.Game.Entities.Entity.Stats;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.Players;
using Steamworks;
using VRage.Components;
using VRage.Components.Entity.CubeGrid;
using VRage.Definitions.Components;
using VRage.Engine;
using VRage.Game.Entity;
using VRage.ParallelWorkers;
using VRage.Physics;
using VRage.Scene;
using VRage.Steam;
using VRage.Utils;
using ZLogger;

// ReSharper disable InconsistentNaming

namespace Meds.Wrapper.Shim
{
    // https://communityedition.medievalengineers.com/mantis/view.php?id=457
    [HarmonyPatch(typeof(MyAttachmentAnimationComponent), "UpdateAnimation")]
    [AlwaysPatch]
    public static class DisableSomeAttachmentAnimations
    {
        private readonly struct AnimationKey : IEquatable<AnimationKey>
        {
            private readonly MyStringHash _def;
            private readonly MyStringHash _anim;

            public AnimationKey(string def, string anim)
            {
                _def = MyStringHash.GetOrCompute(def);
                _anim = MyStringHash.GetOrCompute(anim);
            }

            public AnimationKey(MyStringHash def, MyStringHash anim)
            {
                _def = def;
                _anim = anim;
            }

            public bool Equals(AnimationKey other) => _def.Equals(other._def) && _anim.Equals(other._anim);

            public override bool Equals(object obj) => obj is AnimationKey other && Equals(other);

            public override int GetHashCode() => (_def.GetHashCode() * 397) ^ _anim.GetHashCode();
        }

        private static readonly HashSet<AnimationKey> _disabled = new HashSet<AnimationKey>();

        static DisableSomeAttachmentAnimations()
        {
            foreach (var transmission in new[]
                     {
                         "TransmissionWood",
                         "TransmissionWoodCorner",
                         "TransmissionWoodCornerDown",
                         "TransmissionWoodCornerDownWithFrame",
                         "TransmissionWoodCornerUp",
                         "TransmissionWoodCornerUpWithFrame",
                         "TransmissionWoodCornerWithFrame",
                         "TransmissionWoodTDownWithFrame",
                         "TransmissionWoodTUpWithFrame",
                         "TransmissionWoodTVerticalWithFrame",
                         "TransmissionWoodTWithFrame",
                         "TransmissionWoodVertical",
                         "TransmissionWoodVerticalWithFrame",
                         "TransmissionWoodWithFrame",
                         "Windmill",
                         "DutchWindmill",
                         "StampMillMechanicalAnimation",
                         "SawmillMechanical",
                         "MillstoneMechanical"
                     })
            {
                _disabled.Add(new AnimationKey(transmission, "ccw"));
                _disabled.Add(new AnimationKey(transmission, "cw"));
            }
        }

        private static bool Test(MyAttachmentAnimationComponent component, MyAttachmentAnimationComponentDefinition definition)
        {
            return !_disabled.Contains(new AnimationKey(definition.Id.SubtypeId, component.ActiveAnimation));
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __original, ILGenerator ilg)
        {
            // if (!Test(this, this.m_definition)) {
            yield return new CodeInstruction(OpCodes.Ldarg_0);
            yield return new CodeInstruction(OpCodes.Ldarg_0);
            yield return CodeInstruction.LoadField(typeof(MyAttachmentAnimationComponent), "m_definition");
            yield return CodeInstruction.Call(typeof(DisableSomeAttachmentAnimations), nameof(Test));
            var normalImpl = ilg.DefineLabel();
            yield return new CodeInstruction(OpCodes.Brtrue, normalImpl);

            // RemoveFixedUpdate(UpdateAnimation);
            yield return new CodeInstruction(OpCodes.Ldarg_0);
            yield return new CodeInstruction(OpCodes.Ldarg_0);
            yield return new CodeInstruction(OpCodes.Ldftn, __original);
            yield return new CodeInstruction(OpCodes.Newobj, typeof(MyFixedUpdate)
                .GetConstructors(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.GetProperty)
                .FirstOrDefault(x => x.GetParameters().Length == 2 && x.GetParameters()[0].ParameterType == typeof(object)));
            yield return CodeInstruction.Call(typeof(MyComponentBase), "RemoveFixedUpdate");
            // m_isAnimating = false;
            yield return new CodeInstruction(OpCodes.Ldarg_0);
            yield return new CodeInstruction(OpCodes.Ldc_I4_0);
            yield return CodeInstruction.StoreField(typeof(MyAttachmentAnimationComponent), "m_isAnimating");
            // return;
            yield return new CodeInstruction(OpCodes.Ret);

            // }
            yield return new CodeInstruction(OpCodes.Nop).WithLabels(normalImpl);
            foreach (var i in instructions)
                yield return i;
        }
    }

    // https://communityedition.medievalengineers.com/mantis/view.php?id=480
    [HarmonyPatch(typeof(MyEntityStatComponent.DelayedEffect), "HandleTick")]
    [AlwaysPatch]
    public static class SkipUnloadedDelayedEffects
    {
        public static bool Prefix(MyEntityStatComponent.DelayedEffect __instance) => __instance.StatComponent?.Entity?.InScene ?? false;
    }

    [HarmonyPatch(typeof(Workers), nameof(Workers.Do), typeof(IWork), typeof(WorkerGroupId?), typeof(int))]
    [AlwaysPatch]
    public static class SynchronousMoppBuild_Update
    {
        public static bool Prefix(IWork iWork, ref WorkHandle __result)
        {
            var type = iWork?.GetType();
            if (type?.DeclaringType != typeof(HierarchicalMoppShape<>) || type.Name != "ImmediateUpdateWork")
                return true;
            iWork.DoWork();
            __result = default;
            return false;
        }
    }

    [HarmonyPatch(typeof(MyGridPhysicsShapeComponent), nameof(MyGridPhysicsShapeComponent.Deserialize))]
    [AlwaysPatch]
    public static class SynchronousMoppBuild_Deserialize
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> stream)
        {
            foreach (var i in stream)
                if (i.operand is MethodInfo { DeclaringType: { IsGenericType: true } } method
                    && method.DeclaringType.GetGenericTypeDefinition() == typeof(HierarchicalMoppShape<>)
                    && method.Name == nameof(HierarchicalMoppShape<string>.ImmediateUpdate))
                    yield return i.ChangeInstruction(OpCodes.Pop);
                else
                    yield return i;
        }
    }


    // https://communityedition.medievalengineers.com/mantis/view.php?id=419
    [HarmonyPatch(typeof(WorkerManager), "NotifyWorkStart")]
    [AlwaysPatch(ByRequest = "Mec419")]
    public static class Mec419RareDeadlock_NotifyWorkStart
    {
        private static ILogger Log => Entrypoint.LoggerFor(typeof(Mec419RareDeadlock_NotifyWorkStart));
        private static readonly Type TrackerData = AccessTools.TypeByName("VRage.ParallelWorkers.WorkerManager+TrackerData, VRage.Library");
        private static readonly FieldInfo ExecutionReferenceCount = AccessTools.Field(TrackerData, "ExecutionReferenceCount");

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> stream, ILGenerator ilg)
        {
            var instructions = stream.ToList();
            var hash = instructions
                .Aggregate(19L, (a, b) => a * 31 + b.opcode.Value.GetHashCode());
            if (hash != 4003687741625888343 || ExecutionReferenceCount == null)
            {
                Log.ZLogInformation("Not patching NotifyWorkStart since the hash doesn't match ({0})", hash);
                return instructions;
            }

            // arg1 is "workId"
            // arg2 is "dequeue"
            // local2 is "tracker data"
            // local3 is "should run"

            var labelReturnShouldRun = ilg.DefineLabel();
            var labelSkipUpdate = ilg.DefineLabel();
            var labelMaybeUpdate = ilg.DefineLabel();

            instructions.AddRange(new[]
            {
                new CodeInstruction(OpCodes.Ldloc_3).WithLabels(labelReturnShouldRun),
                new CodeInstruction(OpCodes.Ret)
            });

            // 80-85 (ldarg.0, ldfld, ldarg.1, ldloc.2, callvirt, leave) -- original "m_trackedWork[workId] = track;"
            instructions[80].WithLabels(labelMaybeUpdate);
            instructions.InsertRange(85, new[]
            {
                new CodeInstruction(OpCodes.Leave, labelReturnShouldRun),
                // pop the ldarg.0 or dummy value
                new CodeInstruction(OpCodes.Pop).WithLabels(labelSkipUpdate),
                new CodeInstruction(OpCodes.Leave, labelReturnShouldRun),
            });
            instructions.InsertRange(81, new[]
            {
                // if (!(shouldRun || dequeue)) { goto skipUpdate; }
                new CodeInstruction(OpCodes.Ldloc_3),
                new CodeInstruction(OpCodes.Ldarg_2),
                new CodeInstruction(OpCodes.Or),
                new CodeInstruction(OpCodes.Brfalse, labelSkipUpdate),
            });

            // 77-79 (ldc.i4.0, stloc.3, leave.s) -- original "return false"
            instructions.InsertRange(79, ReturnFalseReplacement());

            // 65-67 (ldc.i4.0, stloc.3, leave.s) -- original "return false"
            instructions.InsertRange(67, ReturnFalseReplacement());

            // Adding: var shouldRun = true;
            // 33... -- original "track.Queue != WorkerGroupId.Null"
            instructions.InsertRange(34, new[]
            {
                new CodeInstruction(OpCodes.Ldc_I4_1),
                new CodeInstruction(OpCodes.Stloc_3)
            });

            return instructions;

            IEnumerable<CodeInstruction> ReturnFalseReplacement() => new[]
            {
                // track.ExecutionReferenceCount--;
                new CodeInstruction(OpCodes.Ldloca_S, 2),
                new CodeInstruction(OpCodes.Ldflda, ExecutionReferenceCount),
                new CodeInstruction(OpCodes.Dup),
                new CodeInstruction(OpCodes.Ldind_I4),
                new CodeInstruction(OpCodes.Ldc_I4_1),
                new CodeInstruction(OpCodes.Sub),
                new CodeInstruction(OpCodes.Stind_I4),
                // goto maybe update
                new CodeInstruction(OpCodes.Br, labelMaybeUpdate),
            };
        }
    }

    // https://communityedition.medievalengineers.com/mantis/view.php?id=419
    [HarmonyPatch(typeof(WorkerManager), "VRage.ParallelWorkers.IWorkerManager.ExecutePendingWork")]
    [AlwaysPatch(ByRequest = "Mec419")]
    public static class Mec419RareDeadlock_ExecutePendingWork
    {
        private static ILogger Log => Entrypoint.LoggerFor(typeof(Mec419RareDeadlock_ExecutePendingWork));

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> stream)
        {
            var instructions = stream.ToList();
            var hash = instructions
                .Aggregate(19L, (a, b) => a * 31 + b.opcode.Value.GetHashCode());
            if (hash != -4260687172009927681L)
            {
                Log.ZLogInformation("Not patching ExecutePendingWork since the hash doesn't match ({0})", hash);
                return instructions;
            }

            // Replace "track.Queue != WorkerGroupId.Null" with "track.Queue == WorkerGroupId.Null"
            instructions[42].opcode = OpCodes.Brtrue;
            return instructions;
        }
    }

    // Fixes access to rtnIdentity.DisplayName when rtnIdentity is always null.
    [HarmonyPatch(typeof(MyPlayers), nameof(MyPlayers.GetIdentity), typeof(MyPlayer))]
    [AlwaysPatch]
    public static class IdentityAccessNre
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> stream)
        {
            var identityDisplayName = AccessTools.PropertyGetter(typeof(MyIdentity), nameof(MyIdentity.DisplayName));
            foreach (var i in stream)
            {
                if (i.Calls(identityDisplayName))
                {
                    i.opcode = OpCodes.Pop;
                    i.operand = null;
                    yield return i;
                    yield return new CodeInstruction(OpCodes.Ldstr, "null");
                    continue;
                }

                yield return i;
            }
        }
    }

    [HarmonyPatch(typeof(MyEntityGridDatabase.ObjectLoader), "TryLoadEntityInternal")]
    [AlwaysPatch]
    public static class ReloadEntitiesMarkedForClose
    {
        private static bool TryGetEntityReplacement(MyScene scene, EntityId id, out MyEntity entity) =>
            scene.TryGetEntity(id, out entity) && !entity.MarkedForClose;

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> stream)
        {
            foreach (var i in stream)
                if (i.operand is MethodInfo method && method.DeclaringType == typeof(MyScene) && method.Name == "TryGetEntity")
                    yield return i.ChangeInstruction(OpCodes.Call, AccessTools.Method(typeof(ReloadEntitiesMarkedForClose), nameof(TryGetEntityReplacement)));
                else
                    yield return i;
        }
    }
}
