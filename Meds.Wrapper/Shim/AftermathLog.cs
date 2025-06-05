using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Havok;
using Medieval.World.Persistence;
using Sandbox.Engine.Physics;
using Sandbox.Game.Components;
using Sandbox.Game.EntityComponents.Grid;
using Sandbox.Game.World;
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
        // Meds.Wrapper.Shim.AftermathLog._events
        private const int MaxEventCount = 8192;
        private static readonly Queue<AftermathEvent> _events = new Queue<AftermathEvent>();

        [DebuggerDisplay("{Tick}, {Type}, {ObjOneName}, {ObjTwoName}")]
        private struct AftermathEvent
        {
            public int Tick;

            public AftermathType Type;

            public ulong ObjOne;
            public string ObjOneDef;
            public string ObjOneName;

            public ulong ObjTwo;
            public string ObjTwoDef;
            public string ObjTwoName;
        }

        private enum AftermathType
        {
            HavokSetShape,
            HavokConstraintAdd,
            HavokConstraintRemove,
            HavokBodyAdd,
            HavokBodyRemove,

            IwpEntityAdd,
            IwpEntityRemove,
            IwpGroupAdd,
            IwpGroupRemove,
        }

        private static AftermathEvent CreateEvent(AftermathType type, MyEntity a, MyEntity b = null) => new AftermathEvent
        {
            Tick = MySession.Static?.GameplayFrameCounter ?? 0,
            Type = type,
            ObjOne = a?.Id.Value ?? 0,
            ObjOneDef = a?.DefinitionId?.SubtypeName,
            ObjOneName = a?.DebugName,
            ObjTwo = b?.Id.Value ?? 0,
            ObjTwoDef = b?.DefinitionId?.SubtypeName,
            ObjTwoName = b?.DebugName,
        };

        private static AftermathEvent CreateEvent(AftermathType type, MyGroup group) => new AftermathEvent
        {
            Tick = MySession.Static?.GameplayFrameCounter ?? 0,
            Type = type,
            ObjOne = group?.Id.Value ?? 0,
            ObjOneDef = group?.GetType().Name,
        };

        private static AftermathEvent CreateEvent(AftermathType type, HkRigidBody a, HkRigidBody b = null) => CreateEvent(
            type,
            (a?.UserObject as MyEntityComponent)?.Entity,
            (b?.UserObject as MyEntityComponent)?.Entity);

        private static void Add(in AftermathEvent evt)
        {
            lock (_events)
            {
                while (_events.Count >= MaxEventCount)
                    _events.Dequeue();
                _events.Enqueue(evt);
            }
        }

        [HarmonyPatch(typeof(MyGridRigidBodyComponent), nameof(MyClusterTree.IMyActivationHandler.Activate))]
        [AlwaysPatch]
        public static class AddGridBodyToWorld
        {
            public static void Postfix(MyGridRigidBodyComponent __instance) => Add(CreateEvent(AftermathType.HavokBodyAdd, __instance.Entity));
        }

        [HarmonyPatch(typeof(MyGridRigidBodyComponent), nameof(MyClusterTree.IMyActivationHandler.Deactivate))]
        [AlwaysPatch]
        public static class RemoveGridBodyFromWorld
        {
            public static void Prefix(MyGridRigidBodyComponent __instance) => Add(CreateEvent(AftermathType.HavokBodyRemove, __instance.Entity));
        }

        [HarmonyPatch(typeof(MyGridRigidBodyComponent), "SetShape")]
        [AlwaysPatch]
        public static class SetGridShape
        {
            public static void Prefix(MyGridRigidBodyComponent __instance) => Add(CreateEvent(AftermathType.HavokSetShape, __instance.Entity));
        }

        [HarmonyPatch(typeof(MyPhysicsBody), nameof(MyClusterTree.IMyActivationHandler.Activate))]
        [AlwaysPatch]
        public static class AddGenericBodyToWorld
        {
            public static void Postfix(MyPhysicsBody __instance)
            {
                // Ignore detector physics
                if ((__instance.Flags & RigidBodyFlag.RBF_DISABLE_COLLISION_RESPONSE) != 0
                    || __instance.Container?.Get<MyUseObjectsComponent>()?.DetectorPhysics == __instance)
                    return;
                Add(CreateEvent(AftermathType.HavokBodyAdd, __instance.Entity));
            }
        }

        [HarmonyPatch(typeof(MyPhysicsBody), nameof(MyClusterTree.IMyActivationHandler.Deactivate))]
        [AlwaysPatch]
        public static class RemoveGenericBodyFromWorld
        {
            public static void Prefix(MyPhysicsBody __instance)
            {
                // Ignore detector physics
                if ((__instance.Flags & RigidBodyFlag.RBF_DISABLE_COLLISION_RESPONSE) != 0
                    || __instance.Container?.Get<MyUseObjectsComponent>()?.DetectorPhysics == __instance)
                    return;
                Add(CreateEvent(AftermathType.HavokBodyRemove, __instance.Entity));
            }
        }

        [HarmonyPatch(typeof(HkWorld), nameof(HkWorld.AddConstraint))]
        [AlwaysPatch]
        public static class AddConstraintToWorld
        {
            public static void Prefix(HkConstraint constraint) =>
                Add(CreateEvent(AftermathType.HavokConstraintAdd, constraint.RigidBodyA, constraint.RigidBodyB));
        }

        [HarmonyPatch(typeof(HkWorld), nameof(HkWorld.RemoveConstraint))]
        [AlwaysPatch]
        public static class RemoveConstraintFromWorld
        {
            public static void Prefix(HkConstraint constraint) =>
                Add(CreateEvent(AftermathType.HavokConstraintRemove, constraint.RigidBodyA, constraint.RigidBodyB));
        }

        [HarmonyPatch("Medieval.World.Persistence.MyEntityGridDatabase+EntityTracker", "Add")]
        [AlwaysPatch]
        public static class AddEntityToDatabase
        {
            public static void Prefix(MyEntity entity) => Add(CreateEvent(AftermathType.IwpEntityAdd, entity));
        }

        [HarmonyPatch("Medieval.World.Persistence.MyEntityGridDatabase+EntityTracker", "Remove")]
        [AlwaysPatch]
        public static class RemoveEntityFromDatabase
        {
            public static void Prefix(MyEntity entity) => Add(CreateEvent(AftermathType.IwpEntityRemove, entity));
        }

        [HarmonyPatch("Medieval.World.Persistence.MyEntityGridDatabase+GroupTracker", "Add")]
        [AlwaysPatch]
        public static class AddGroupToDatabase
        {
            public static void Prefix(MyGroup group) => Add(CreateEvent(AftermathType.IwpGroupAdd, group));
        }

        [HarmonyPatch("Medieval.World.Persistence.MyEntityGridDatabase+GroupTracker", "Remove")]
        [AlwaysPatch]
        public static class RemoveGroupFromDatabase
        {
            public static void Prefix(MyGroup group) => Add(CreateEvent(AftermathType.IwpGroupRemove, group));
        }
    }
}