using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Havok;
using Medieval.Entities.Components;
using Medieval.Entities.Components.Crafting;
using Medieval.GameSystems;
using Medieval.World.Persistence;
using Meds.Wrapper.Utils;
using Microsoft.Extensions.Logging;
using Sandbox.Engine.Multiplayer;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Entity.Stats;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.Players;
using VRage.Components;
using VRage.Definitions.Components;
using VRage.Engine;
using VRage.Game.Entity;
using VRage.Library.Collections;
using VRage.Network;
using VRage.ParallelWorkers;
using VRage.Physics;
using VRage.Session;
using VRage.Utils;
using VRageRender;
using VRageRender.Messages;
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
    [AlwaysPatch(ByRequest = nameof(SynchronousMoppBuild))]
    public static class SynchronousMoppBuild
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

    [HarmonyPatch(typeof(Workers), nameof(Workers.Do), typeof(IWork), typeof(WorkerGroupId?), typeof(int))]
    [AlwaysPatch(ByRequest = nameof(ParallelMoppBuild))]
    public static class ParallelMoppBuild
    {
        public static bool Prefix(IWork iWork, ref int maxWorkers)
        {
            var type = iWork?.GetType();
            if (type?.DeclaringType == typeof(HierarchicalMoppShape<>) && type.Name != "ImmediateUpdateWork")
                maxWorkers = -1;
            return false;
        }
    }
}