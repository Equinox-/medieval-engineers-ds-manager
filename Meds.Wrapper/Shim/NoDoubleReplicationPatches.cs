using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization.Formatters;
using HarmonyLib;
using Medieval.World.Persistence;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.WorldEnvironment;
using Sandbox.Game.WorldEnvironment.ObjectBuilders;
using VRage.Game.Entity;
using VRage.Session;
using ZLogger;

// ReSharper disable InconsistentNaming

namespace Meds.Wrapper.Shim
{
    public static class NoDoubleReplication
    {
        [ThreadStatic]
        public static int SerializingDepth;

        public static bool IsSerializing => SerializingDepth > 0;
    }

    [HarmonyPatch(typeof(MyMultiplayerSandbox), "OnWorldRequest")]
    [AlwaysPatch]
    public static class NoDoubleReplicationWorld
    {
        public static void Prefix() => NoDoubleReplication.SerializingDepth++;

        public static void Postfix() => NoDoubleReplication.SerializingDepth--;
    }

    [HarmonyPatch("VRage.Replication.MyReplicableStreamingPreparation", "SubmitReplicables")]
    [AlwaysPatch]
    public static class NoDoubleReplicationStreaming
    {
        public static void Prefix() => NoDoubleReplication.SerializingDepth++;

        public static void Postfix() => NoDoubleReplication.SerializingDepth--;
    }

    [HarmonyPatch(typeof(MySessionPersistence), "GetSnapshot")]
    [HarmonyPatch(typeof(MyInfiniteWorldPersistence), "UpdateChunks")]
    [AlwaysPatch]
    public static class NoDoubleReplicationGuardSaving
    {
        public static void Prefix(ref int __state)
        {
            ref var count = ref NoDoubleReplication.SerializingDepth;
            __state = count;
            if (count <= 0)
                return;
            count = 0;
            Entrypoint.LoggerFor(typeof(NoDoubleReplication)).ZLogError("Was marked as in replication during save. Reverting to be safe.");
        }

        public static void Postfix(ref int __state)
        {
            NoDoubleReplication.SerializingDepth = __state;
        }
    }

    [HarmonyPatch(typeof(MyProceduralEnvironmentProvider), nameof(MyProceduralEnvironmentProvider.GetObjectBuilder))]
    [AlwaysPatch]
    public static class NoDoubleReplicationEnvironmentSectors
    {
        public static bool Prefix(ref MyObjectBuilder_EnvironmentDataProvider __result)
        {
            if (!NoDoubleReplication.IsSerializing)
                return true;
            __result = new MyObjectBuilder_ProceduralEnvironmentProvider();
            return false;
        }
    }

    [HarmonyPatch(typeof(MyInventory), nameof(MyInventory.Serialize))]
    // [AlwaysPatch]
    // For this to work correctly the client also needs to be updated so that it always sends the inventory changed event, even on the initial deserialization.
    public static class NoDoubleReplicationInventory
    {
        private static readonly List<MyInventoryItem> NoItems = new List<MyInventoryItem>();

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> isn, ILGenerator ilg)
        {
            foreach (var instruction in isn)
            {
                if (instruction.opcode == OpCodes.Ldfld && instruction.operand is FieldInfo field && field.FieldType == typeof(List<MyInventoryItem>))
                {
                    // var itemsToSave = NoReplication.IsSerializing ? NoItems : m_items;
                    var skipItems = ilg.DefineLabel();
                    var originalCode = ilg.DefineLabel();
                    yield return CodeInstruction.Call(typeof(NoDoubleReplication), "get_" + nameof(NoDoubleReplication.IsSerializing))
                        .WithLabels(instruction.labels)
                        .WithBlocks(instruction.blocks);
                    yield return new CodeInstruction(OpCodes.Brtrue, skipItems);
                    yield return new CodeInstruction(instruction.opcode, instruction.operand);
                    yield return new CodeInstruction(OpCodes.Br, originalCode);

                    yield return new CodeInstruction(OpCodes.Pop).WithLabels(skipItems);
                    yield return CodeInstruction.LoadField(typeof(NoDoubleReplicationInventory), nameof(NoItems));

                    yield return new CodeInstruction(OpCodes.Nop).WithLabels(originalCode);
                }
                else
                {
                    yield return instruction;
                }
            }
        }
    }

    [HarmonyPatch("Sandbox.Game.Replication.MyVoxelReplicable+SerializeData", "Store")]
    [AlwaysPatch]
    public static class NoDoubleReplicationVoxels_AlwaysAsync
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> isn)
        {
            foreach (var instruction in isn)
            {
                // Always send the voxel data through the VoxelReplicable.
                if (instruction.opcode == OpCodes.Stfld && instruction.operand is FieldInfo field &&
                    (field.Name == "m_contentChanged" || field.Name == "m_isFromPrefab"))
                {
                    yield return new CodeInstruction(OpCodes.Pop);
                    yield return new CodeInstruction(OpCodes.Ldc_I4_1);
                }

                yield return instruction;
            }
        }
    }

    [HarmonyPatch(typeof(MyVoxelMaps), nameof(MyVoxelMaps.GetVoxelMapsArray))]
    [AlwaysPatch]
    public static class NoDoubleReplicationVoxels_NeverSync
    {
        public static bool Prefix(ref Dictionary<string, byte[]> __result)
        {
            if (!NoDoubleReplication.IsSerializing)
                return true;
            __result = new Dictionary<string, byte[]>();
            return false;
        }
    }

    [HarmonyPatch(typeof(MyVoxelMaps), "GetVoxelMapsArrayAsync")]
    [AlwaysPatch(VersionRange = "[0.7.4,)")]
    public static class NoDoubleReplicationVoxels_NeverSync074
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator ilg, MethodBase __original)
        {
            var originalCode = ilg.DefineLabel();
            yield return CodeInstruction.Call(typeof(NoDoubleReplication), "get_" + nameof(NoDoubleReplication.IsSerializing));
            yield return new CodeInstruction(OpCodes.Brfalse, originalCode);
            yield return new CodeInstruction(OpCodes.Newobj, AccessTools.Constructor(((MethodInfo)__original).ReturnType, Array.Empty<Type>()));
            yield return new CodeInstruction(OpCodes.Ret);
            yield return new CodeInstruction(OpCodes.Nop).WithLabels(originalCode);
            foreach (var instruction in instructions)
                yield return instruction;
        }
    }
}