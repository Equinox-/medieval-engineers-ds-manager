using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Meds.Metrics;
using Meds.Metrics.Group;
using Meds.Wrapper.Shim;
using VRage.Collections;
using VRage.Library.Threading;
using VRage.Logging;
using VRage.ParallelWorkers;
using ZLogger;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace Meds.Wrapper.Metrics
{
    public static class WorkerMetrics
    {
        private static readonly MetricName RootName = MetricName.Of("me.workers.group");
        private static readonly IHelper HelperInstance;
        private static ILogger Log => Entrypoint.LoggerFor(typeof(WorkerMetrics));

        private interface IHelper
        {
            void Update();

            Type WorkerType { get; }

            void SawWork(object worker);
        }

        private class Helper<TGroup, TWorker, TWorkItem> : IHelper where TWorker : IWorker
        {
            private readonly Func<WorkerManager, FastResourceLock> _groupsLock;
            private readonly Func<WorkerManager, Dictionary<WorkerGroupId, TGroup>> _groups;
            private readonly Func<TGroup, WorkerGroupId> _groupId;
            private readonly Func<TGroup, MyQueue<TWorkItem>> _groupQueue;
            private readonly Func<TGroup, TWorker[]> _groupWorkers;
            private readonly Func<TWorker, MyQueue<TWorkItem>> _workerQueue;
            private readonly Func<TWorker, TGroup> _workerGroup;

            public Type WorkerType => typeof(TWorker);

            public Helper()
            {
                _groupsLock = AccessTools.Field(typeof(WorkerManager), "m_internalsLock").CreateGetter<WorkerManager, FastResourceLock>();
                _groups = AccessTools.Field(typeof(WorkerManager), "m_groups").CreateGetter<WorkerManager, Dictionary<WorkerGroupId, TGroup>>();
                _groupQueue = AccessTools.Field(typeof(TGroup), "m_workQueue").CreateGetter<TGroup, MyQueue<TWorkItem>>();
                _groupWorkers = AccessTools.Field(typeof(TGroup), "Workers").CreateGetter<TGroup, TWorker[]>();
                _workerQueue = AccessTools.Field(typeof(TWorker), "m_personalQueue").CreateGetter<TWorker, MyQueue<TWorkItem>>();
                _groupId = AccessTools.Field(typeof(TGroup), "Id").CreateGetter<TGroup, WorkerGroupId>();
                _workerGroup = AccessTools.Field(typeof(TWorker), "m_group").CreateGetter<TWorker, TGroup>();
            }

            private MetricGroup GroupFor(WorkerGroupId id) => MetricRegistry.Group(RootName.WithTag("group", id.Name));

            public void SawWork(object worker)
            {
                try
                {
                    if (worker is TWorker workerImpl)
                    {
                        var group = _workerGroup(workerImpl);
                        var groupId = _groupId(group);
                        GroupFor(groupId).Counter("jobs").Inc();
                    }
                }
                catch (Exception err)
                {
                    Log.ZLogWarning(err, "Failed to report work start");
                }
            }

            void IHelper.Update()
            {
                var manager = (WorkerManager)Workers.Manager;
                using var token = _groupsLock(manager).AcquireSharedUsing();
                var groups = _groups(manager);
                foreach (var kv in groups)
                {
                    var id = kv.Key;
                    var group = kv.Value;
                    var groupMetric = MetricRegistry.Group(RootName.WithTag("group", id.Name));
                    var groupQueue = _groupQueue(group);
                    var workers = _groupWorkers(group);
                    var totalQueued = 0;
                    lock (groupQueue)
                        totalQueued += groupQueue.Count;
                    var idle = 0;
                    foreach (var worker in workers)
                    {
                        var workerQueue = _workerQueue(worker);
                        lock (workerQueue)
                            totalQueued += workerQueue.Count;
                        if (worker.Idle)
                            idle++;
                    }

                    groupMetric.SetGauge("workers.total", workers.Length);
                    groupMetric.SetGauge("workers.idle", idle);
                    groupMetric.SetGauge("queue", totalQueued);
                }
            }
        }

        static WorkerMetrics()
        {
            try
            {
                var groupType = typeof(WorkerManager).GetNestedType("WorkerGroup", BindingFlags.Public | BindingFlags.NonPublic);
                var workerType = Type.GetType("VRage.ParallelWorkers.Worker, VRage.Library");
                var workItemType = Type.GetType("VRage.ParallelWorkers.WorkItem, VRage.Library");
                var helperType = typeof(Helper<,,>).MakeGenericType(groupType, workerType, workItemType);
                HelperInstance = (IHelper)Activator.CreateInstance(helperType);
            }
            catch (Exception err)
            {
                Log.ZLogWarning(err, "Failed to construct worker metrics");
                HelperInstance = null;
            }
        }

        [HarmonyPatch]
        [AlwaysPatch]
        private static class SawWorkPatch
        {
            private static MethodBase _method;

            public static bool Prepare()
            {
                _method = HelperInstance != null ? AccessTools.Method(HelperInstance.WorkerType, "TryDequeueWork") : null;
                return _method != null;
            }

            public static IEnumerable<MethodBase> TargetMethods()
            {
                yield return _method;
            }

            // ReSharper disable InconsistentNaming
            public static void Postfix(object __instance, ref bool __result)
            {
                if (__result)
                    HelperInstance.SawWork(__instance);
            }
            // ReSharper restore InconsistentNaming
        }

        public static void Update() => HelperInstance?.Update();
    }
}