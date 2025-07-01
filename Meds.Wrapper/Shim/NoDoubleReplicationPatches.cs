using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Sandbox.Engine.Multiplayer;
using VRage.ModAPI;

// ReSharper disable InconsistentNaming

namespace Meds.Wrapper.Shim
{
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

    [HarmonyPatch]
    [AlwaysPatch]
    public static class NoDoubleReplicationVoxels_NeverSync
    {
        public static IEnumerable<MethodBase> TargetMethods() =>
            typeof(MyMultiplayerSandbox).GetNestedType("SendWorldPayload", BindingFlags.Public | BindingFlags.NonPublic)!.GetConstructors();

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> isn)
        {
            foreach (var instruction in isn)
            {
                // Never send the voxel data through the initial world.
                if (instruction.operand is MethodBase { Name: "GetVoxelMapsArrayAsync" } method)
                {
                    for (var i = 0; i < (method.IsStatic ? 0 : 1) + method.GetParameters().Length; i++)
                        yield return new CodeInstruction(OpCodes.Pop);
                    yield return new CodeInstruction(OpCodes.Newobj, AccessTools.Constructor(typeof(Dictionary<string, IMyStorageSaveTask>), Type.EmptyTypes));
                    continue;
                }

                yield return instruction;
            }
        }
    }
}