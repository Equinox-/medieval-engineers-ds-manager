using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Havok;
using Sandbox.Engine.Physics;
using Sandbox.Game.EntityComponents.Grid;
using VRage.Components.Physics;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.Scene;
using VRageMath.Spatial;

// ReSharper disable InconsistentNaming

namespace Meds.Wrapper.Shim
{
    public static class AftermathLog
    {
        private const int MaxEventCount = 1024;
        private static readonly Queue<AftermathEvent> _events = new Queue<AftermathEvent>();

        private struct AftermathEvent
        {
            public DateTime Time;

            public AftermathType Type;

            public EntityId EntityOne;
            public string EntityOneDef;
            public string EntityOneName;

            public EntityId EntityTwo;
            public string EntityTwoDef;
            public string EntityTwoName;
        }

        private enum AftermathType
        {
            SetShape,
            AddConstraint,
            RemoveConstraint,
            AddBodyToWorld,
            RemoveBodyFromWorld,
        }

        private static AftermathEvent CreateEvent(AftermathType type, MyEntity a, MyEntity b = null) => new AftermathEvent
        {
            Time = DateTime.Now,
            Type = type,
            EntityOne = a?.Id ?? default,
            EntityOneDef = a?.DefinitionId?.SubtypeName,
            EntityOneName = a?.DebugName,
            EntityTwo = b?.Id ?? default,
            EntityTwoDef = b?.DefinitionId?.SubtypeName,
            EntityTwoName = b?.DebugName,
        };

        private static AftermathEvent CreateEvent(AftermathType type, HkRigidBody a, HkRigidBody b = null) => CreateEvent(
            type,
            (a?.UserObject as MyEntityComponent)?.Entity,
            (b?.UserObject as MyEntityComponent)?.Entity);

        [MethodImpl(MethodImplOptions.Synchronized)]
        private static void Add(in AftermathEvent evt)
        {
            while (_events.Count >= MaxEventCount)
                _events.Dequeue();
            _events.Enqueue(evt);
        }

        [HarmonyPatch(typeof(MyGridRigidBodyComponent), nameof(MyClusterTree.IMyActivationHandler.Activate))]
        [AlwaysPatch]
        public static class AddGridBodyToWorld
        {
            public static void Postfix(MyGridRigidBodyComponent __instance)
            {
                Add(CreateEvent(AftermathType.AddBodyToWorld, __instance.Entity));
            }
        }

        [HarmonyPatch(typeof(MyGridRigidBodyComponent), nameof(MyClusterTree.IMyActivationHandler.Deactivate))]
        [AlwaysPatch]
        public static class RemoveGridBodyFromWorld
        {
            public static void Prefix(MyGridRigidBodyComponent __instance)
            {
                Add(CreateEvent(AftermathType.RemoveBodyFromWorld, __instance.Entity));
            }
        }

        [HarmonyPatch(typeof(MyGridRigidBodyComponent), "SetShape")]
        [AlwaysPatch]
        public static class SetGridShape
        {
            public static void Prefix(MyGridRigidBodyComponent __instance)
            {
                Add(CreateEvent(AftermathType.SetShape, __instance.Entity));
            }
        }

        [HarmonyPatch(typeof(MyPhysicsBody), nameof(MyClusterTree.IMyActivationHandler.Activate))]
        [AlwaysPatch]
        public static class AddGenericBodyToWorld
        {
            public static void Postfix(MyPhysicsBody __instance)
            {
                // Ignore detector physics
                if (!(__instance.Entity is { InScene: true }))
                    return;
                Add(CreateEvent(AftermathType.AddBodyToWorld, __instance.Entity));
            }
        }

        [HarmonyPatch(typeof(MyPhysicsBody), nameof(MyClusterTree.IMyActivationHandler.Deactivate))]
        [AlwaysPatch]
        public static class RemoveGenericBodyFromWorld
        {
            public static void Prefix(MyPhysicsBody __instance)
            {
                // Ignore detector physics
                if (!(__instance.Entity is { InScene: true }))
                    return;
                Add(CreateEvent(AftermathType.RemoveBodyFromWorld, __instance.Entity));
            }
        }

        [HarmonyPatch(typeof(HkWorld), nameof(HkWorld.AddConstraint))]
        [AlwaysPatch]
        public static class AddConstraintToWorld
        {
            public static void Prefix(HkConstraint constraint)
            {
                Add(CreateEvent(AftermathType.AddConstraint, constraint.RigidBodyA, constraint.RigidBodyB));
            }
        }

        [HarmonyPatch(typeof(HkWorld), nameof(HkWorld.RemoveConstraint))]
        [AlwaysPatch]
        public static class RemoveConstraintFromWorld
        {
            public static void Prefix(HkConstraint constraint)
            {
                Add(CreateEvent(AftermathType.RemoveConstraint, constraint.RigidBodyA, constraint.RigidBodyB));
            }
        }
    }
}