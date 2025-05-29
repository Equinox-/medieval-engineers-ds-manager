using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;
using Medieval.World.Persistence;
using Meds.Wrapper.Metrics;
using Meds.Wrapper.Utils;
using VRage.Components;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.Scene;
using VRage.Session;
using ZLogger;

// ReSharper disable InconsistentNaming

namespace Meds.Wrapper.Shim
{
    [HarmonyPatch(typeof(MyUpdateScheduler), "ReportError")]
    [AlwaysPatch]
    public static class VerboseUpdateSchedulerCrash
    {
        private readonly struct LoggingPayloadConsumer : IPayloadConsumer
        {
            private readonly Delegate _action;
            private readonly Exception _error;

            public LoggingPayloadConsumer(Delegate action, Exception error)
            {
                _action = action;
                _error = error;
            }

            public void Consume<T>(in T payload)
            {
                Entrypoint
                    .LoggerFor(_action.Method.DeclaringType ?? typeof(VerboseUpdateSchedulerCrash))
                    .ZLogErrorWithPayload(_error, payload, "Update method failed: {0} on {1}",
                        _action.Method.FullDescription(), _action.Target ?? "null");
            }
        }

        public static void Prefix(Delegate action, Exception error) => LoggingPayloads.VisitPayload(action, new LoggingPayloadConsumer(action, error));
    }


    [HarmonyPatch]
    [AlwaysPatch]
    public static class VerboseEntityComponentError
    {
        private static void Report(
            string function,
            MyEntityComponent ec,
            MyEntityComponentContainer container,
            Exception error)
        {
            Entrypoint
                .LoggerFor(ec.GetType())
                .ZLogErrorWithPayload(error,
                    new EntityComponentPayload(ec),
                    "Failed to invoke {0} on {1}",
                    function,
                    container?.Entity?.ToString());
        }

        private static void OnAddedToScene(MyEntityComponent ec, MyEntityComponentContainer container)
        {
            try
            {
                ec.OnAddedToScene();
            }
            catch (Exception error)
            {
                Report("OnAddedToScene", ec, container, error);
                throw;
            }
        }

        private static void OnRemovedFromScene(MyEntityComponent ec, MyEntityComponentContainer container)
        {
            try
            {
                ec.OnRemovedFromScene();
            }
            catch (Exception error)
            {
                Report("OnRemovedFromScene", ec, container, error);
                throw;
            }
        }

        private static void OnAddedToContainer(MyEntityComponent ec, MyEntityComponentContainer container)
        {
            try
            {
                ec.OnAddedToContainer();
            }
            catch (Exception error)
            {
                Report("OnAddedToContainer", ec, container, error);
                throw;
            }
        }

        private static void OnBeforeRemovedFromContainer(MyEntityComponent ec, MyEntityComponentContainer container)
        {
            try
            {
                ec.OnBeforeRemovedFromContainer();
            }
            catch (Exception error)
            {
                Report("OnBeforeRemovedFromContainer", ec, container, error);
                throw;
            }
        }


        private static readonly Dictionary<MethodInfo, MethodInfo> AliasedMethods = new[]
        {
            "OnAddedToScene",
            "OnRemovedFromScene",
            "OnAddedToContainer",
            "OnBeforeRemovedFromContainer"
        }.ToDictionary(
            x => AccessTools.Method(typeof(MyEntityComponent), x),
            x => AccessTools.Method(typeof(VerboseEntityComponentError), x));

        public static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(MyEntityComponentContainer), "OnAddedToScene");
            yield return AccessTools.Method(typeof(MyEntityComponentContainer), "OnRemovedFromScene");
            yield return AccessTools.Method(typeof(MyEntityComponent), "SetContainer");
        }

        public static IEnumerable<CodeInstruction> Transpiler(
            MethodBase __original,
            IEnumerable<CodeInstruction> instructions)
        {
            // Find the container
            bool TryFindContainerArg(out int index)
            {
                if (typeof(MyEntityComponentContainer).IsAssignableFrom(__original.DeclaringType))
                {
                    index = 0;
                    return true;
                }

                var args = __original.GetParameters();
                for (var i = 0; i < args.Length; i++)
                {
                    if (typeof(MyEntityComponentContainer).IsAssignableFrom(args[i].ParameterType))
                    {
                        index = i + (__original.IsStatic ? 0 : 1);
                        return true;
                    }
                }

                index = default;
                return false;
            }

            if (!TryFindContainerArg(out var containerArg))
            {
                Entrypoint.LoggerFor(typeof(VerboseEntityComponentError))
                    .ZLogInformation("Failed to find container arg in {0}", __original.FullDescription());
                foreach (var instruction in instructions)
                    yield return instruction;
                yield break;
            }

            foreach (var instruction in instructions)
            {
                if (instruction.operand is MethodInfo method &&
                    AliasedMethods.TryGetValue(method, out var alias))
                {
                    Entrypoint.LoggerFor(typeof(VerboseEntityComponentError))
                        .ZLogInformation("Intercepting call to {0} from {1}",
                            method.Name, __original.FullDescription());
                    yield return new CodeInstruction(OpCodes.Ldarg, containerArg);
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = alias;
                }

                yield return instruction;
            }
        }
    }

    [HarmonyPatch(typeof(MySessionPersistence), "VRage.Session.IMyScenePersistence.AddEntity")]
    [AlwaysPatch]
    public static class EntityPersistencePatch
    {
        private static bool AttemptRepair = false;

        private static readonly FieldInfo EntityPersistence = AccessTools.Field(typeof(MySessionPersistence), "m_entityPersistence");
        private static readonly MethodInfo EntityId = AccessTools.PropertyGetter(typeof(MyEntity), nameof(MyEntity.Id));

        private static readonly MethodInfo EntityPersistenceRemove =
            AccessTools.Method(EntityPersistence.FieldType, "Remove", new[] { typeof(EntityId) });

        private static readonly MethodInfo GetComponent = AccessTools.Method(typeof(MySessionPersistence), "GetComponent", new[] { typeof(int) });

        private static readonly MethodInfo GetEntityPersistence =
            AccessTools.Method(typeof(MySessionPersistence), "VRage.Session.IMyScenePersistence.GetEntityPersistence", new[] { typeof(MyEntity) });

        private static readonly MethodInfo MonitorEnter = AccessTools.Method(typeof(Monitor), nameof(Monitor.Enter), new[] { typeof(object) });
        private static readonly MethodInfo MonitorExit = AccessTools.Method(typeof(Monitor), nameof(Monitor.Exit), new[] { typeof(object) });

        private static readonly FieldInfo EntityTrackerLoaded = AccessTools.Field(GridDatabaseMetrics.EntitiesField.FieldType, "Loaded");
        private static readonly Type ChunkObjectData = EntityTrackerLoaded.FieldType.GenericTypeArguments[1];

        private static readonly MethodInfo EntityTrackerLoadedRemove = AccessTools.Method(EntityTrackerLoaded.FieldType,
            nameof(IDictionary<int, int>.Remove),
            new[] { typeof(EntityId) });

        private static readonly MethodInfo EntityTrackerLoadedTryGetValue = AccessTools.Method(EntityTrackerLoaded.FieldType,
            nameof(IDictionary<int, int>.TryGetValue),
            new[] { typeof(EntityId), ChunkObjectData.MakeByRefType() });

        private static readonly MethodInfo ChunkTrackerOnObjectRemoved = AccessTools.Method(GridDatabaseMetrics.ChunksField.FieldType, "OnObjectRemove", new[]
        {
            typeof(EntityId),
            ChunkObjectData.MakeByRefType()
        });

        private static readonly MethodInfo LogWarningMethod = AccessTools.Method(
            typeof(EntityPersistencePatch),
            nameof(LogWarning),
            new[] { typeof(MyEntity), typeof(IMyPersistenceComponent) });

        private static readonly FieldInfo AttemptRepairField = AccessTools.Field(typeof(EntityPersistencePatch), nameof(AttemptRepairField));

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator ilg)
        {
            var normalCode = ilg.DefineLabel();
            if (EntityPersistence != null && GetComponent != null && GetEntityPersistence != null)
            {
                var currComp = ilg.DeclareLocal(typeof(IMyPersistenceComponent));
                // var currComp = this.GetEntityPersistence(entity)
                yield return new CodeInstruction(OpCodes.Ldarg_0);
                yield return new CodeInstruction(OpCodes.Ldarg_1);
                yield return new CodeInstruction(OpCodes.Callvirt, GetEntityPersistence);
                yield return new CodeInstruction(OpCodes.Dup);
                yield return new CodeInstruction(OpCodes.Stloc, currComp);
                // if (currComp != null) {
                yield return new CodeInstruction(OpCodes.Brfalse, normalCode);

                // LogWarning(entity, currComp)
                yield return new CodeInstruction(OpCodes.Ldarg_1);
                yield return new CodeInstruction(OpCodes.Ldloc, currComp);
                yield return new CodeInstruction(OpCodes.Call, LogWarningMethod);

                // if (AttemptRepair) {
                yield return new CodeInstruction(OpCodes.Ldsfld, AttemptRepairField);
                yield return new CodeInstruction(OpCodes.Brfalse, normalCode);

                // var iwp = currComp as MyInfiniteWorldPersistence;
                var iwp = ilg.DeclareLocal(typeof(MyInfiniteWorldPersistence));
                yield return new CodeInstruction(OpCodes.Ldloc, currComp);
                yield return new CodeInstruction(OpCodes.Isinst, typeof(MyInfiniteWorldPersistence));
                yield return new CodeInstruction(OpCodes.Dup);
                yield return new CodeInstruction(OpCodes.Stloc, iwp);
                // if (iwp != null) {
                yield return new CodeInstruction(OpCodes.Brfalse, normalCode);

                // m_entityPersistence.Remove(entity.Id);
                yield return new CodeInstruction(OpCodes.Ldarg_0);
                yield return new CodeInstruction(OpCodes.Ldfld, EntityPersistence);
                yield return new CodeInstruction(OpCodes.Ldarg_1);
                yield return new CodeInstruction(OpCodes.Callvirt, EntityId);
                yield return new CodeInstruction(OpCodes.Callvirt, EntityPersistenceRemove.GetBaseDefinition());
                yield return new CodeInstruction(OpCodes.Pop);

                // var db = iwp.Database;
                var db = ilg.DeclareLocal(typeof(MyEntityGridDatabase));
                yield return new CodeInstruction(OpCodes.Ldloc, iwp);
                yield return new CodeInstruction(OpCodes.Callvirt, GridDatabaseMetrics.DatabaseProperty);
                yield return new CodeInstruction(OpCodes.Stloc, db);

                // Monitor.Enter(db);
                yield return new CodeInstruction(OpCodes.Ldloc, db);
                yield return new CodeInstruction(OpCodes.Call, MonitorEnter);
                // try {
                yield return new CodeInstruction(OpCodes.Nop).WithBlocks(new ExceptionBlock(ExceptionBlockType.BeginExceptionBlock));

                // var entities = db.Entities;
                var entities = ilg.DeclareLocal(GridDatabaseMetrics.EntitiesField.FieldType);
                yield return new CodeInstruction(OpCodes.Ldloc, db);
                yield return new CodeInstruction(OpCodes.Ldfld, GridDatabaseMetrics.EntitiesField);
                yield return new CodeInstruction(OpCodes.Stloc, entities);

                // ChunkObjectData chunkObjectData;
                var chunkObjectData = ilg.DeclareLocal(ChunkObjectData);

                // if (entities.Loaded.TryGetValue(entity.Id, out chunkObjectData)) {
                var labelWasNotLoaded = ilg.DefineLabel();
                yield return new CodeInstruction(OpCodes.Ldloc, entities);
                yield return new CodeInstruction(OpCodes.Ldfld, EntityTrackerLoaded);
                yield return new CodeInstruction(OpCodes.Ldarg_1);
                yield return new CodeInstruction(OpCodes.Callvirt, EntityId);
                yield return new CodeInstruction(OpCodes.Ldloca, chunkObjectData);
                yield return new CodeInstruction(OpCodes.Callvirt, EntityTrackerLoadedTryGetValue.GetBaseDefinition());
                yield return new CodeInstruction(OpCodes.Brfalse, labelWasNotLoaded);

                // db.Chunks.OnObjectRemove(entity.Id, ref chunkObjectData);
                yield return new CodeInstruction(OpCodes.Ldloc, db);
                yield return new CodeInstruction(OpCodes.Ldfld, GridDatabaseMetrics.ChunksField);
                yield return new CodeInstruction(OpCodes.Ldarg_1);
                yield return new CodeInstruction(OpCodes.Callvirt, EntityId);
                yield return new CodeInstruction(OpCodes.Ldloca, chunkObjectData);
                yield return new CodeInstruction(OpCodes.Callvirt, ChunkTrackerOnObjectRemoved);

                // entities.Loaded.Remove(entity.Id);
                yield return new CodeInstruction(OpCodes.Ldloc, entities);
                yield return new CodeInstruction(OpCodes.Ldfld, EntityTrackerLoaded);
                yield return new CodeInstruction(OpCodes.Ldarg_1);
                yield return new CodeInstruction(OpCodes.Callvirt, EntityId);
                yield return new CodeInstruction(OpCodes.Callvirt, EntityTrackerLoadedRemove.GetBaseDefinition());
                yield return new CodeInstruction(OpCodes.Pop);

                // } // if (loaded.TryGetValue(...))
                yield return new CodeInstruction(OpCodes.Nop).WithLabels(labelWasNotLoaded);

                // } finally {
                yield return new CodeInstruction(OpCodes.Ldloc, db).WithBlocks(new ExceptionBlock(ExceptionBlockType.BeginFinallyBlock));
                // Monitor.Exit(db);
                yield return new CodeInstruction(OpCodes.Call, MonitorExit);
                yield return new CodeInstruction(OpCodes.Endfinally).WithBlocks(new ExceptionBlock(ExceptionBlockType.EndExceptionBlock));
                // } // lock(db)

                // } // if (iwp != null);
                // } // if (currComp != null);
                // } // if (AttemptRepair)
            }

            yield return new CodeInstruction(OpCodes.Nop).WithLabels(normalCode);
            foreach (var instruction in instructions)
                yield return instruction;
        }

        // ReSharper disable once UnusedMember.Local
        private static void LogWarning(MyEntity entity, IMyPersistenceComponent curr)
        {
            var resurrect = curr is MyInfiniteWorldPersistence && AttemptRepair;
            Entrypoint.LoggerFor(typeof(EntityPersistencePatch))
                .ZLogWarningWithPayload(
                    new EntityPayload(entity),
                    "Entity {0} ({1}) is already persisted in {2}. {3}",
                    entity.EntityId,
                    entity.DefinitionId?.SubtypeName,
                    curr.GetType().Name,
                    resurrect ? "It will be removed and re-added." : "No repair will take place.");
        }
    }
}