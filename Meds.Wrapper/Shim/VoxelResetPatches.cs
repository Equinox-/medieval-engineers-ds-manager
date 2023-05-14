using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Sandbox.Engine.Voxels;
using Sandbox.Engine.Voxels.Shape;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.Voxels;
using VRage.Voxels;
using VRageMath;
using ZLogger;
using IMyStorage = VRage.ModAPI.IMyStorage;

namespace Meds.Wrapper.Shim
{
    /// <summary>
    /// Injects voxel reset access for usage by Equinox's VoxelResetTool mod.
    /// https://steamcommunity.com/sharedfiles/filedetails/?id=2975680569
    /// </summary>
    public static class VoxelResetPatches
    {
        private const BindingFlags Bindings = BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private static readonly Type OctreeNodeType = Type.GetType("Sandbox.Engine.Voxels.MyOctreeNode, Sandbox.Game");
        private static readonly FieldInfo OctreeNodeChildMask = OctreeNodeType.GetField("ChildMask", Bindings);
        private static readonly Type MicroOctreeLeafType = Type.GetType("Sandbox.Engine.Voxels.MyMicroOctreeLeaf, Sandbox.Game");
        private static readonly Type ProviderLeafType = Type.GetType("Sandbox.Engine.Voxels.MyProviderLeaf, Sandbox.Game");

        private static readonly ConstructorInfo ProviderLeafConstructor = ProviderLeafType.GetConstructor(Bindings, null, new[]
        {
            typeof(IMyStorageDataProvider),
            typeof(MyStorageDataTypeEnum),
            typeof(MyCellCoord).MakeByRefType()
        }, null);

        private static readonly FieldInfo TreeHeightField = typeof(MyOctreeStorage).GetField("m_treeHeight", Bindings);

        private static readonly FieldInfo ContentLeaves = typeof(MyOctreeStorage).GetField("m_contentLeaves", Bindings);

        private static readonly FieldInfo MaterialLeaves = typeof(MyOctreeStorage).GetField("m_materialLeaves", Bindings);

        private static readonly FieldInfo ContentNodes = typeof(MyOctreeStorage).GetField("m_contentNodes", Bindings);

        private static readonly FieldInfo MaterialNodes = typeof(MyOctreeStorage).GetField("m_materialNodes", Bindings);

        private static readonly FieldInfo StorageLock = typeof(MyStorageBase).GetField("m_storageLock", Bindings);

        private static readonly MethodInfo OnRangeChanged = typeof(MyStorageBase).GetMethod("OnRangeChanged", Bindings);
        private static readonly MethodInfo CellCoordSetUnpack = typeof(MyCellCoord).GetMethod(nameof(MyCellCoord.SetUnpack), new[] { typeof(ulong) });
        private static readonly MethodInfo OnVoxelOperationResponse = typeof(MyVoxelHands).GetMethod("OnVoxelOperationResponse", Bindings);

        private static readonly Type NodeDictionaryType = OctreeNodeType != null ? typeof(Dictionary<,>).MakeGenericType(typeof(ulong), OctreeNodeType) : null;
        private static readonly Type LeafDictionaryType = typeof(Dictionary<,>).MakeGenericType(typeof(ulong), typeof(IMyOctreeLeafNode));
        private const int LeafLodCount = 4;

        private sealed class ImplAttribute : Attribute
        {
            public string Method { get; }
            public Type[] Arguments { get; }

            public bool Prefix { get; set; }

            public ImplAttribute(params Type[] arguments)
            {
                Method = null;
                Arguments = arguments;
            }
        }

        public static void Register()
        {
            var missing = new List<string>();

            void Check(object obj, string name)
            {
                if (obj == null)
                    missing.Add(name);
            }

            Check(OctreeNodeType, nameof(OctreeNodeType));
            Check(OctreeNodeChildMask, nameof(OctreeNodeChildMask));
            Check(MicroOctreeLeafType, nameof(MicroOctreeLeafType));
            Check(ProviderLeafType, nameof(ProviderLeafType));
            Check(ProviderLeafConstructor, nameof(ProviderLeafConstructor));
            Check(TreeHeightField, nameof(TreeHeightField));
            Check(ContentLeaves, nameof(ContentLeaves));
            Check(MaterialLeaves, nameof(MaterialLeaves));
            Check(ContentNodes, nameof(ContentNodes));
            Check(MaterialNodes, nameof(MaterialNodes));
            Check(StorageLock, nameof(StorageLock));
            Check(OnRangeChanged, nameof(OnRangeChanged));
            Check(CellCoordSetUnpack, nameof(CellCoordSetUnpack));
            Check(OnVoxelOperationResponse, nameof(OnVoxelOperationResponse));
            Check(NodeDictionaryType, nameof(NodeDictionaryType));
            Check(LeafDictionaryType, nameof(LeafDictionaryType));
            if (missing.Count > 0)
            {
                Entrypoint.LoggerFor(typeof(VoxelResetPatches))
                    .ZLogWarning(
                        "Failed to reflect members {0}, voxel reset patches will not work",
                        string.Join(", ", missing));
                return;
            }


            var modTypes = PatchHelper.ModTypes("Equinox76561198048419394.VoxelReset.VoxelResetHooks").ToList();
            var patchedMods = new HashSet<MyModContext>();
            foreach (var method in typeof(VoxelResetPatches).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var attr = method.GetCustomAttribute<ImplAttribute>();
                if (attr == null)
                    continue;
                var methodName = attr.Method ?? method.Name;
                foreach (var (mod, type) in modTypes)
                {
                    var target = AccessTools.Method(type, methodName, attr.Arguments);
                    if (target == null)
                    {
                        Entrypoint.LoggerFor(typeof(VoxelResetPatches))
                            .ZLogWarning(
                                "When patching VoxelResetHooks type from {0} ({1}) the {2} {3} method wasn't found",
                                mod.Id, mod.Name,
                                methodName, string.Join(", ", attr.Arguments.Select(x => x.Name)));
                        continue;
                    }

                    if (attr.Prefix)
                        PatchHelper.Prefix(target, method);
                    else
                        PatchHelper.Transpile(target, method);
                    patchedMods.Add(mod);
                }
            }

            if (patchedMods.Count > 0)
            {
                Entrypoint.LoggerFor(typeof(VoxelResetPatches))
                    .ZLogInformation(
                        "Voxel reset implementations were injected into mods: {0}",
                        string.Join(", ", patchedMods.Select(x => $"{x.Id} ({x.Name})")));
            }
        }

        private static CodeInstruction ins(OpCode code, object operand = null)
        {
            return new CodeInstruction(code, operand);
        }

        [Impl(typeof(IMyStorage))]
        private static IEnumerable<CodeInstruction> IsAvailable(IEnumerable<CodeInstruction> _)
        {
            yield return ins(OpCodes.Ldarg_0);
            yield return ins(OpCodes.Isinst, typeof(MyOctreeStorage));
            yield return ins(OpCodes.Ret);
        }

        [Impl(typeof(IMyStorage))]
        private static IEnumerable<CodeInstruction> TreeHeightInternal(IEnumerable<CodeInstruction> _)
        {
            yield return ins(OpCodes.Ldarg_0);
            yield return ins(OpCodes.Castclass, typeof(MyOctreeStorage));
            yield return ins(OpCodes.Ldfld, TreeHeightField);
            yield return ins(OpCodes.Ret);
        }

        private static IEnumerable<CodeInstruction> LoadNodeDictionary(OpCode instance, OpCode type, ILGenerator ilg)
        {
            yield return ins(instance);
            yield return ins(OpCodes.Castclass, typeof(MyOctreeStorage));
            var lblMaterial = ilg.DefineLabel();
            var lblEnd = ilg.DefineLabel();
            yield return ins(type);
            yield return ins(OpCodes.Ldc_I4, (int)MyStorageDataTypeEnum.Material);
            yield return ins(OpCodes.Beq, lblMaterial);
            yield return ins(OpCodes.Ldfld, ContentNodes);
            yield return ins(OpCodes.Br, lblEnd);
            yield return ins(OpCodes.Ldfld, MaterialNodes).WithLabels(lblMaterial);
            yield return ins(OpCodes.Nop).WithLabels(lblEnd);
        }

        private static IEnumerable<CodeInstruction> LoadLeafDictionary(OpCode instance, OpCode type, ILGenerator ilg)
        {
            yield return ins(instance);
            yield return ins(OpCodes.Castclass, typeof(MyOctreeStorage));
            var lblMaterial = ilg.DefineLabel();
            var lblEnd = ilg.DefineLabel();
            yield return ins(type);
            yield return ins(OpCodes.Ldc_I4, (int)MyStorageDataTypeEnum.Material);
            yield return ins(OpCodes.Beq, lblMaterial);
            yield return ins(OpCodes.Ldfld, ContentLeaves);
            yield return ins(OpCodes.Br, lblEnd);
            yield return ins(OpCodes.Ldfld, MaterialLeaves).WithLabels(lblMaterial);
            yield return ins(OpCodes.Nop).WithLabels(lblEnd);
        }

        [Impl(typeof(IMyStorage), typeof(ulong), typeof(MyStorageDataTypeEnum))]
        private static IEnumerable<CodeInstruction> NodeChildMaskInternal(IEnumerable<CodeInstruction> _, ILGenerator ilg)
        {
            foreach (var i in LoadNodeDictionary(OpCodes.Ldarg_0, OpCodes.Ldarg_2, ilg))
                yield return i;

            var local = ilg.DeclareLocal(OctreeNodeType);
            var noNode = ilg.DefineLabel();
            yield return ins(OpCodes.Ldarg_1);
            yield return ins(OpCodes.Ldloca, local);
            yield return CodeInstruction.Call(NodeDictionaryType, "TryGetValue");
            yield return ins(OpCodes.Brfalse, noNode);
            yield return ins(OpCodes.Ldloca, local);
            yield return ins(OpCodes.Ldfld, OctreeNodeChildMask);
            yield return ins(OpCodes.Ret);
            yield return ins(OpCodes.Ldc_I4_0).WithLabels(noNode);
            yield return ins(OpCodes.Ret);
        }

        private const int LeafTypeMissing = 0;
        private const int LeafTypeProvider = 1;
        private const int LeafTypeMicroOctree = 2;

        [Impl(typeof(IMyStorage), typeof(ulong), typeof(MyStorageDataTypeEnum))]
        private static IEnumerable<CodeInstruction> TryGetLeafTypeInternal(IEnumerable<CodeInstruction> _, ILGenerator ilg)
        {
            foreach (var i in LoadLeafDictionary(OpCodes.Ldarg_0, OpCodes.Ldarg_2, ilg))
                yield return i;

            var local = ilg.DeclareLocal(typeof(IMyOctreeLeafNode));
            var fallback = ilg.DefineLabel();
            yield return ins(OpCodes.Ldarg_1);
            yield return ins(OpCodes.Ldloca, local);
            yield return CodeInstruction.Call(LeafDictionaryType, "TryGetValue");
            yield return ins(OpCodes.Brfalse, fallback);

            var notProvider = ilg.DefineLabel();

            // if (leaf is Provider) { return LeafTypeProvider; }
            yield return ins(OpCodes.Ldloc, local);
            yield return ins(OpCodes.Isinst, ProviderLeafType);
            yield return ins(OpCodes.Brfalse, notProvider);
            yield return ins(OpCodes.Ldc_I4, LeafTypeProvider);
            yield return ins(OpCodes.Ret);

            // if (leaf is MicroOctree) { return LeafTypeMicroOctree; }
            yield return ins(OpCodes.Ldloc, local).WithLabels(notProvider);
            yield return ins(OpCodes.Isinst, MicroOctreeLeafType);
            yield return ins(OpCodes.Brfalse, fallback);
            yield return ins(OpCodes.Ldc_I4, LeafTypeMicroOctree);
            yield return ins(OpCodes.Ret);

            // return LeafTypeMissing;
            yield return ins(OpCodes.Ldc_I4, LeafTypeMissing).WithLabels(fallback);
            yield return ins(OpCodes.Ret);
        }

        [Impl(typeof(IMyStorage), typeof(ulong), typeof(MyStorageDataTypeEnum))]
        private static IEnumerable<CodeInstruction> SetLeafToProvider(IEnumerable<CodeInstruction> _, ILGenerator ilg)
        {
            foreach (var i in LoadLeafDictionary(OpCodes.Ldarg_0, OpCodes.Ldarg_2, ilg))
                yield return i;
            yield return ins(OpCodes.Ldarg_1);


            yield return ins(OpCodes.Ldarg_0);
            yield return ins(OpCodes.Castclass, typeof(MyOctreeStorage));
            yield return CodeInstruction.Call(typeof(MyOctreeStorage), "get_" + nameof(MyOctreeStorage.DataProvider));
            yield return ins(OpCodes.Ldarg_2);

            // var coord = new MyCellCoord();
            var coord = ilg.DeclareLocal(typeof(MyCellCoord));
            yield return ins(OpCodes.Ldloca, coord);
            yield return ins(OpCodes.Initobj, typeof(MyCellCoord));

            // coord.Unpack(leafId);
            yield return ins(OpCodes.Ldloca, coord);
            yield return ins(OpCodes.Ldarg_1);
            yield return ins(OpCodes.Call, CellCoordSetUnpack);

            // coord.Lod += LeafLodCount;
            yield return ins(OpCodes.Ldloca, coord);
            yield return CodeInstruction.LoadField(typeof(MyCellCoord), nameof(MyCellCoord.Lod), true);
            yield return ins(OpCodes.Dup);
            yield return ins(OpCodes.Ldind_I4);
            yield return ins(OpCodes.Ldc_I4, LeafLodCount);
            yield return ins(OpCodes.Add);
            yield return ins(OpCodes.Stind_I4);

            // new ProviderLeaf(storage.DataProvider, dataType, ref coord);
            yield return ins(OpCodes.Ldloca, coord);
            yield return ins(OpCodes.Newobj, ProviderLeafConstructor);

            // leafDictionary[leafId] = new ProviderLeaf(...)
            yield return CodeInstruction.Call(LeafDictionaryType, "set_Item");
            yield return ins(OpCodes.Ret);
        }

        [Impl(typeof(IMyStorage), typeof(ulong), typeof(MyStorageDataTypeEnum))]
        private static IEnumerable<CodeInstruction> DeleteLeafInternal(IEnumerable<CodeInstruction> _, ILGenerator ilg)
        {
            foreach (var i in LoadLeafDictionary(OpCodes.Ldarg_0, OpCodes.Ldarg_2, ilg))
                yield return i;
            yield return ins(OpCodes.Ldarg_1);
            yield return CodeInstruction.Call(LeafDictionaryType, "Remove");
            yield return ins(OpCodes.Pop);
            yield return ins(OpCodes.Ret);
        }

        [Impl(typeof(IMyStorage), typeof(ulong), typeof(MyStorageDataTypeEnum))]
        private static IEnumerable<CodeInstruction> DeleteNodeInternal(IEnumerable<CodeInstruction> _, ILGenerator ilg)
        {
            foreach (var i in LoadNodeDictionary(OpCodes.Ldarg_0, OpCodes.Ldarg_2, ilg))
                yield return i;
            yield return ins(OpCodes.Ldarg_1);
            yield return CodeInstruction.Call(NodeDictionaryType, "Remove");
            yield return ins(OpCodes.Pop);
            yield return ins(OpCodes.Ret);
        }

        [Impl(typeof(IMyStorage), typeof(Vector3I), typeof(Vector3I), typeof(MyStorageDataTypeEnum))]
        private static IEnumerable<CodeInstruction> RaiseResetRangeInternal(IEnumerable<CodeInstruction> _)
        {
            yield return ins(OpCodes.Ldarg_0);
            yield return ins(OpCodes.Castclass, typeof(MyOctreeStorage));
            // min
            yield return ins(OpCodes.Ldarg_1);
            // max
            yield return ins(OpCodes.Ldarg_2);

            // 1 << type
            yield return ins(OpCodes.Ldc_I4_1);
            yield return ins(OpCodes.Ldarg_3);
            yield return ins(OpCodes.Shl);

            yield return ins(OpCodes.Callvirt, OnRangeChanged);
            yield return ins(OpCodes.Ret);
        }

        [Impl(typeof(IMyStorage))]
        private static IEnumerable<CodeInstruction> StorageLockInternal(IEnumerable<CodeInstruction> _)
        {
            yield return ins(OpCodes.Ldarg_0);
            yield return ins(OpCodes.Castclass, typeof(MyOctreeStorage));
            yield return ins(OpCodes.Ldfld, StorageLock);
            yield return ins(OpCodes.Ret);
        }

        [Impl(typeof(MyVoxelBase), typeof(BoundingBoxD), Prefix = true)]
        // ReSharper disable once InconsistentNaming
        private static bool TryResetClientsInternal(MyVoxelBase voxel, BoundingBoxD worldBox, out bool __result)
        {
            var shape = new MyShapeBox
            {
                HalfExtents = (Vector3)worldBox.HalfExtents,
                Rotation = Quaternion.Identity,
                Position = worldBox.Center,
            };
            Func<MyVoxelBase, Action<MySignedDistanceShape, MyVoxelOperationType, byte, bool>> endpoint = x => (Action<MySignedDistanceShape, MyVoxelOperationType, byte, bool>)
                Delegate.CreateDelegate(typeof(Action<MySignedDistanceShape, MyVoxelOperationType, byte, bool>), x, OnVoxelOperationResponse);

            MyAPIGateway.Multiplayer?.RaiseEvent(voxel, endpoint, shape, MyVoxelOperationType.Cut, (byte) 0, true);
            MyAPIGateway.Multiplayer?.RaiseEvent(voxel, endpoint, shape, MyVoxelOperationType.Fill, (byte) 0, true);
            MyAPIGateway.Multiplayer?.RaiseEvent(voxel, endpoint, shape, MyVoxelOperationType.Paint, (byte) 0, true);
            __result = true;
            return false;
        }
    }
}