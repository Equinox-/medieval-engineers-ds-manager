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
using VRage.Session;
using VRage.Utils;
using VRageRender;
using VRageRender.Messages;
using ZLogger;

// ReSharper disable InconsistentNaming

namespace Meds.Wrapper.Shim
{
    // https://communityedition.medievalengineers.com/mantis/view.php?id=317
    [HarmonyPatch]
    [AlwaysPatch(Late = true, VersionRange = "[,0.7.4)")]
    public static class PatchMtuWarning
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            var type = Type.GetType("Sandbox.Engine.Multiplayer.MyTransportLayer, Sandbox.Game") ?? throw new Exception("Failed to find TransportLayer");

            var method = AccessTools.Method(type, "SendMessage", new[] { typeof(MyMessageId), typeof(BitStream), typeof(bool), typeof(EndpointId) }) ??
                         throw new Exception("Failed to find SendMessage");
            yield return method;
        }

        private const int UnreliableMaximumTransfer = 1200 - 1;
        private const int ReliableMaximumTransfer = 50 * 1024 - 1;

        public static void Prefix(MyMessageId id, BitStream stream, bool reliable)
        {
            var maxTransfer = reliable ? ReliableMaximumTransfer : UnreliableMaximumTransfer;
            if (stream != null && stream.BytePosition > maxTransfer)
                Entrypoint.LoggerFor(typeof(PatchMtuWarning))
                    .ZLogError(
                        new Exception("MTU Exceeded"),
                        "Event {0} [{1}] {2} exceeds the maximum transfer limit {3}",
                        id,
                        reliable ? "R" : "U",
                        stream.BytePosition,
                        maxTransfer);
        }
    }

    // Partial fix for https://communityedition.medievalengineers.com/mantis/view.php?id=452
    [HarmonyPatch]
    [AlwaysPatch(Late = true, VersionRange = "[,0.7.4)")]
    public static class PatchStateSyncOverflow
    {
        private static readonly MethodInfo SetBitPositionWrite = AccessTools.Method(typeof(BitStream), nameof(BitStream.SetBitPositionWrite));
        private static readonly MethodInfo GetBitPosition = AccessTools.PropertyGetter(typeof(BitStream), nameof(BitStream.BitPosition));

        public static IEnumerable<MethodBase> TargetMethods()
        {
            var type = typeof(MyReplicationServer);
            foreach (var method in type.GetMethods(BindingFlags.NonPublic | BindingFlags.Instance))
                if (method.Name == "SendStateSync" && method.GetParameters().Length == 4)
                    yield return method;
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = instructions.ToList();
            CodeInstruction loadLocalMessageSizeFound = null;
            for (var i = list.Count - 3; i >= 0; i--)
            {
                var callBitPositionRead = list[i];
                var loadLocalMessageSize = list[i + 1];
                var branchNothingToDo = list[i + 2];
                if (callBitPositionRead.Calls(GetBitPosition) && loadLocalMessageSize.IsLdloc()
                                                              && (branchNothingToDo.opcode == OpCodes.Blt || branchNothingToDo.opcode == OpCodes.Blt_S))
                {
                    loadLocalMessageSizeFound = loadLocalMessageSize;
                    break;
                }
            }

            if (loadLocalMessageSizeFound == null)
                return list;

            for (var i = list.Count - 1; i >= 0; i--)
            {
                if (!list[i].Calls(SetBitPositionWrite)) continue;
                CodeInstruction storeLoc = null;
                var loadOp = loadLocalMessageSizeFound.opcode;
                if (loadOp == OpCodes.Ldloc_0)
                    storeLoc = new CodeInstruction(OpCodes.Stloc_0);
                else if (loadOp == OpCodes.Ldloc_1)
                    storeLoc = new CodeInstruction(OpCodes.Stloc_1);
                else if (loadOp == OpCodes.Ldloc_2)
                    storeLoc = new CodeInstruction(OpCodes.Stloc_2);
                else if (loadOp == OpCodes.Ldloc_3)
                    storeLoc = new CodeInstruction(OpCodes.Stloc_3);
                else if (loadOp == OpCodes.Ldloc_S)
                    storeLoc = new CodeInstruction(OpCodes.Stloc_S, loadLocalMessageSizeFound.operand);
                else if (loadOp == OpCodes.Ldloc)
                    storeLoc = new CodeInstruction(OpCodes.Stloc, loadLocalMessageSizeFound.operand);
                else
                    return list;

                list.Insert(i, new CodeInstruction(OpCodes.Ldc_I4, 32));
                list.Insert(i + 1, storeLoc);
                Entrypoint.LoggerFor(typeof(PatchStateSyncOverflow))
                    .ZLogInformation("Patching SendStateSync to always flush on MTU overflow.  Local={0}", storeLoc);
                return list;
            }

            return list;
        }
    }

    // https://communityedition.medievalengineers.com/mantis/view.php?id=103
    [HarmonyPatch(typeof(MyBannerComponent), "OnSessionReady")]
    [AlwaysPatch(VersionRange = "[,0.7.4)")]
    public static class PatchBannerLoading
    {
        public static bool Prefix() => false;
    }

    // https://communityedition.medievalengineers.com/mantis/view.php?id=459
    [HarmonyPatch(typeof(MyPersistenceViewers), "GetIdentity")]
    [AlwaysPatch(VersionRange = "[,0.7.4)")]
    public static class PatchPersistenceViewerCleanup
    {
        // Save the identities of players so that we still know the identity once the player logs out.
        private static readonly Dictionary<ulong, MyIdentity> IdentityCache = new Dictionary<ulong, MyIdentity>();

        public static void Postfix(ref MyIdentity __result, ulong clientId)
        {
            if (__result != null)
                IdentityCache[clientId] = __result;
            else
                __result = IdentityCache.GetValueOrDefault(clientId, null);
        }
    }

    [HarmonyPatch(typeof(MyInfiniteWorldPersistence), nameof(MyInfiniteWorldPersistence.DestroyView))]
    [AlwaysPatch(VersionRange = "[,0.7.4)")]
    public static class PatchPersistenceViewerDestroy
    {
        public static bool Prefix(int viewId)
        {
            if (viewId != -1)
                return true;
            Entrypoint.LoggerFor(typeof(PatchPersistenceViewerDestroy))
                .ZLogWarningWithPayload(StackUtils.CaptureGameLogicStackPayload(), "Destroyed persistence viewer with invalid ID");
            return false;
        }
    }

    // https://communityedition.medievalengineers.com/mantis/view.php?id=461
    [HarmonyPatch(typeof(MyCraftingComponent), "SimulatePassageOfTime")]
    [AlwaysPatch(VersionRange = "[,0.7.4)")]
    public static class UnloadedCraftingBatchFix
    {
        public static bool Prefix(MyCraftingComponent __instance) => __instance.Entity?.InScene ?? false;
    }

    // https://communityedition.medievalengineers.com/mantis/view.php?id=463
    [HarmonyPatch(typeof(MyCharacterDetectorComponent), "set_DetectedEntity")]
    [AlwaysPatch(VersionRange = "[,0.7.4)")]
    public static class CharacterDetectorCloseHandlerFix
    {
        private static readonly MethodInfo OnDetectedEntityMarkForClose =
            AccessTools.Method(typeof(MyCharacterDetectorComponent), "OnDetectedEntityMarkForClose");

        private static readonly FieldInfo DetectedEntityField = AccessTools.Field(typeof(MyCharacterDetectorComponent), "m_detectedEntity");

        public static bool Prepare()
        {
            if (OnDetectedEntityMarkForClose == null)
                Entrypoint.LoggerFor(typeof(CharacterDetectorCloseHandlerFix)).ZLogWarning("Failed to find OnDetectedEntityMarkForClose method");
            if (DetectedEntityField == null)
                Entrypoint.LoggerFor(typeof(CharacterDetectorCloseHandlerFix)).ZLogWarning("Failed to find m_detectedEntity");
            return OnDetectedEntityMarkForClose != null && DetectedEntityField != null;
        }

        public static bool Prefix(MyCharacterDetectorComponent __instance, MyEntity value)
        {
            var curr = __instance.DetectedEntity;
            if (curr == value)
                return false;
            var handler = (Action<MyEntity>)Delegate.CreateDelegate(typeof(Action<MyEntity>), __instance, OnDetectedEntityMarkForClose);
            if (curr != null)
                curr.OnMarkForClose -= handler;
            DetectedEntityField.SetValue(__instance, value);
            if (value != null)
                value.OnMarkForClose += handler;
            return false;
        }
    }

    // https://communityedition.medievalengineers.com/mantis/view.php?id=463
    [HarmonyPatch(typeof(MyCharacterDetectorComponent), "GatherDetectorsInArea")]
    [AlwaysPatch(VersionRange = "[,0.7.4)")]
    public static class CharacterDetectorListAllocationFix
    {
        [ThreadStatic]
        private static List<MyEntity> ThreadSharedList;

        private static List<MyEntity> SharedList()
        {
            var list = ThreadSharedList ??= new List<MyEntity>();
            list.Clear();
            return list;
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var i in instructions)
            {
                if (i.opcode != OpCodes.Call || !(i.operand is MethodBase method) || method.DeclaringType != typeof(List<MyEntity>) || method.Name != ".ctor")
                {
                    yield return i;
                    continue;
                }

                // Remove unused parameters
                for (var j = 0; j < method.GetParameters().Length; j++)
                    yield return new CodeInstruction(OpCodes.Pop);
                // Exchange for the shared list getter
                i.operand = AccessTools.Method(typeof(CharacterDetectorCloseHandlerFix), nameof(SharedList));
                yield return i;
            }
        }
    }

    [HarmonyPatch(typeof(MyNullRender), "EnqueueMessage")]
    [AlwaysPatch(VersionRange = "[,0.7.4)")]
    public static class NullRenderMessagePoolingFix
    {
        public static bool Prefix(MyRenderMessageBase message)
        {
            MyRenderProxy.MessagePool.Return(message);
            return false;
        }
    }

    // https://communityedition.medievalengineers.com/mantis/view.php?id=464
    [HarmonyPatch]
    [AlwaysPatch(VersionRange = "[,0.7.4)")]
    public static class NullRenderSkipMethods
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            var methodsByName = typeof(MyRenderProxy).GetMethods(BindingFlags.Static | BindingFlags.Public)
                .GroupBy(x => x.Name)
                .ToDictionary(x => x.Key, x => x.ToList());
            foreach (var name in new[] { "UpdateRenderObject", "UpdateRenderObjectVisibility", "UpdateRenderEntity", "CheckMessageId" })
            {
                if (!methodsByName.TryGetValue(name, out var methods))
                {
                    Entrypoint.LoggerFor(typeof(NullRenderMessagePoolingFix)).ZLogWarning("Failed to find method MyRenderProxy.{0}", name);
                    continue;
                }

                foreach (var method in methods)
                    yield return method;
            }
        }

        public static bool Prefix() => false;
    }

    // https://communityedition.medievalengineers.com/mantis/view.php?id=465
    [HarmonyPatch]
    [AlwaysPatch(VersionRange = "[,0.7.4)")]
    public static class UpdateSchedulerFixedUpdatesAllocations
    {
        private sealed class EqualityAdapter<T> : IEqualityComparer<T>
        {
            private readonly Func<T, T, bool> _equals;
            private readonly Func<T, int> _hash;

            public EqualityAdapter(Func<T, T, bool> equals, Func<T, int> hash)
            {
                _equals = equals;
                _hash = hash;
            }

            public bool Equals(T x, T y) => _equals(x, y);

            public int GetHashCode(T obj) => _hash(obj);
        }

        private static object _equalityComparer;
        private static readonly Type _equalityType;
        private static readonly MethodBase _hashSetNew;
        private static readonly MethodBase _hashSetNewWithEquality;
        private static readonly MethodInfo _listIndexOf;
        private static readonly MethodInfo _listIndexOfWithEquality;
        private static readonly FieldInfo _equalityComparerField = AccessTools.Field(typeof(UpdateSchedulerFixedUpdatesAllocations), nameof(_equalityComparer));
        private static ILogger Log => Entrypoint.LoggerFor(typeof(UpdateSchedulerFixedUpdatesAllocations));

        private static int EqualityAwareIndexOf<T>(List<T> list, T item, IEqualityComparer<T> comparer)
        {
            for (var i = 0; i < list.Count; i++)
                if (comparer.Equals(list[i], item))
                    return i;
            return -1;
        }

        static UpdateSchedulerFixedUpdatesAllocations()
        {
            _equalityComparer = null;
            var type = Type.GetType("VRage.Components.MyUpdateScheduler+FixedUpdate, VRage");
            if (type == null)
            {
                Log.ZLogWarning("Failed to find FixedUpdate type");
                return;
            }

            _equalityType = typeof(IEqualityComparer<>).MakeGenericType(type);
            _hashSetNew = typeof(HashSet<>).MakeGenericType(type).GetConstructor(Type.EmptyTypes)
                          ?? throw new ArgumentException("Can't find empty parameter hash set constructor");
            _hashSetNewWithEquality = typeof(HashSet<>).MakeGenericType(type).GetConstructor(new[] { _equalityType })
                                      ?? throw new ArgumentException("Can't find equality parameter hash set constructor");
            _listIndexOf = typeof(List<>).MakeGenericType(type).GetMethod("IndexOf", new[] { type })
                           ?? throw new ArgumentException("Can't find empty parameter hash set constructor");
            _listIndexOfWithEquality = AccessTools.Method(typeof(UpdateSchedulerFixedUpdatesAllocations), nameof(EqualityAwareIndexOf))
                .MakeGenericMethod(type);

            if (typeof(IEquatable<>).MakeGenericType(type).IsAssignableFrom(type))
            {
                Log.ZLogInformation("Fixed update type is already equatable");
                return;
            }

            var callback = AccessTools.Field(type, "Callback");
            if (callback == null)
            {
                Log.ZLogWarning("Failed to find FixedUpdate.Callback field");
                return;
            }

            var objectEqualityMethod = AccessTools.Method(typeof(object), nameof(object.Equals), new[] { typeof(object), typeof(object) })
                                       ?? throw new ArgumentException("Failed to find object equality method");
            var objectHashMethod = AccessTools.Method(typeof(object), nameof(GetHashCode))
                                   ?? throw new ArgumentException("Failed to find object hash method");
            var equalityAdapter = typeof(EqualityAdapter<>).MakeGenericType(type);

            var hashMethod = new DynamicMethod("FixedUpdate_hash", typeof(int), new[] { type }, type);
            var hashIlg = hashMethod.GetILGenerator();
            hashIlg.Emit(OpCodes.Ldarga, 0);
            hashIlg.Emit(OpCodes.Ldfld, callback);
            hashIlg.Emit(OpCodes.Callvirt, objectHashMethod);
            hashIlg.Emit(OpCodes.Ret);
            var hashFunc = hashMethod.CreateDelegate(typeof(Func<,>).MakeGenericType(type, typeof(int)));

            var equalityMethod = new DynamicMethod("FixedUpdate_equals", typeof(bool), new[] { type, type }, type);
            var equalityIlg = equalityMethod.GetILGenerator();
            equalityIlg.Emit(OpCodes.Ldarga, 0);
            equalityIlg.Emit(OpCodes.Ldfld, callback);
            equalityIlg.Emit(OpCodes.Ldarga, 1);
            equalityIlg.Emit(OpCodes.Ldfld, callback);
            equalityIlg.Emit(OpCodes.Call, objectEqualityMethod);
            equalityIlg.Emit(OpCodes.Ret);
            var equalityFunc = equalityMethod.CreateDelegate(typeof(Func<,,>).MakeGenericType(type, type, typeof(bool)));

            var equalityAdapterCtor = AccessTools.Constructor(equalityAdapter, new[] { equalityFunc.GetType(), hashFunc.GetType() })
                                      ?? throw new ArgumentException("Failed to find equality adapter ctor");
            _equalityComparer = equalityAdapterCtor.Invoke(new object[] { equalityFunc, hashFunc });
        }

        public static bool Prepare() => _equalityComparer != null;

        public static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var ctor in typeof(MyUpdateScheduler).GetConstructors())
                yield return ctor;
            yield return AccessTools.Method(typeof(MyUpdateScheduler), "ApplyChanges") ?? throw new ArgumentException("Failed to find ApplyChanges");
            yield return AccessTools.Method(typeof(MyUpdateScheduler), "AddFixedUpdate") ?? throw new ArgumentException("Failed to find AddFixedUpdate");
            yield return AccessTools.Method(typeof(MyUpdateScheduler), "RemoveFixedUpdate") ?? throw new ArgumentException("Failed to find RemoveFixedUpdate");
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __original)
        {
            foreach (var instruction in instructions)
            {
                if (instruction.opcode == OpCodes.Newobj && instruction.operand is MethodBase ctor && ctor == _hashSetNew)
                {
                    yield return new CodeInstruction(OpCodes.Ldsfld, _equalityComparerField);
                    yield return new CodeInstruction(OpCodes.Castclass, _equalityType);
                    Log.ZLogInformation("Patching hash set constructor to be equality aware in {0}", __original.Name);
                    instruction.operand = _hashSetNewWithEquality;
                }
                else if (instruction.opcode == OpCodes.Callvirt && _listIndexOf.Equals(instruction.operand))
                {
                    yield return new CodeInstruction(OpCodes.Ldsfld, _equalityComparerField);
                    yield return new CodeInstruction(OpCodes.Castclass, _equalityType);
                    Log.ZLogInformation("Patching index of call to be equality aware in {0}", __original.Name);
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = _listIndexOfWithEquality;
                }

                yield return instruction;
            }
        }
    }

    // https://communityedition.medievalengineers.com/mantis/view.php?id=409
    [HarmonyPatch(typeof(MyClaimedAreaRespawnLocation), nameof(MyClaimedAreaRespawnLocation.IsValidForIdentity))]
    [AlwaysPatch(VersionRange = "[,0.7.4)")]
    public static class ProhibitSpawningAtUnclaimedAreas
    {
        public static void Postfix(MyClaimedAreaRespawnLocation __instance, ref bool __result)
        {
            __result &= __instance.OwnerId != 0;
        }
    }

    // https://communityedition.medievalengineers.com/mantis/view.php?id=457
    [HarmonyPatch(typeof(MyAttachmentAnimationComponent), "UpdateAnimation")]
    [AlwaysPatch(VersionRange = "[,0.7.4)")]
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
    [AlwaysPatch(VersionRange = "[,0.7.4)")]
    public static class SkipUnloadedDelayedEffects
    {
        public static bool Prefix(MyEntityStatComponent.DelayedEffect __instance) => __instance.StatComponent?.Entity?.InScene ?? false;
    }

    // https://communityedition.medievalengineers.com/mantis/view.php?id=436
    [HarmonyPatch]
    [AlwaysPatch(VersionRange = "[,0.7.4)")]
    public static class SometimesAvoidInvalidNetworkClosureCrash
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            _target = Type.GetType("VRage.Network.MyGroupClosure, VRage.Game")
                ?.GetMethod("get_Master", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            return _target != null;
        }

        public static IEnumerable<MethodBase> TargetMethods()
        {
            yield return _target;
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var i in instructions)
            {
                if (i.opcode == OpCodes.Call && i.operand is MethodBase method && method.DeclaringType == typeof(Enumerable) &&
                    method.Name == nameof(Enumerable.First))
                {
                    // Change .First() call to .FirstOrDefault() to push crashes elsewhere.
                    // This doesn't come close to actually fixing everything, but it's better than nothing.
                    i.operand = AccessTools.GetDeclaredMethods(typeof(Enumerable))
                        .First(x => x.Name == nameof(Enumerable.FirstOrDefault) && x.GetParameters().Length == 1)
                        .MakeGenericMethod(method.GetGenericArguments());
                }

                yield return i;
            }
        }
    }

    // https://communityedition.medievalengineers.com/mantis/view.php?id=419
    [HarmonyPatch(typeof(WorkerManager), "NotifyWorkStart")]
    [AlwaysPatch(VersionRange = "[,0.7.4)")]
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
    [AlwaysPatch(VersionRange = "[,0.7.4)")]
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

    [HarmonyPatch(typeof(MyPhysicsSandbox), nameof(MyPhysicsSandbox.CreateHkWorld))]
    [AlwaysPatch(VersionRange = "[,0.7.4)")]
    public static class MecUnidentifiedActiveRigidBodiesLeak
    {
        public static void Postfix(HkWorld __result)
        {
            __result.OnRigidBodyRemoved += body =>
            {
                if (body is HkRigidBody rb)
                    __result.ActiveRigidBodies.Remove(rb);
            };
        }
    }

    // Disable it entirely due to https://communityedition.medievalengineers.com/mantis/view.php?id=522
    [HarmonyPatch(typeof(MyInfiniteWorldPersistence), nameof(MyInfiniteWorldPersistence.WaitOnRange))]
    [AlwaysPatch(VersionRange = "[,0.7.4)")]
    public static class Mec522NeverWait
    {
        public static bool Prefix() => false;
    }
}