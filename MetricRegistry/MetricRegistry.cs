using System;
using System.Collections.Generic;
using System.Threading;
using Meds.Metrics.Group;
using VRage.Collections;
using VRage.Library.Collections;
using VRage.Library.Threading;

namespace Meds.Metrics
{
    public static class MetricRegistry
    {
        private static readonly FastResourceLock Lock = new FastResourceLock();
        private static readonly List<HelperMetric> Helpers = new List<HelperMetric>();
        private static readonly List<MetricRoot> Metrics = new List<MetricRoot>();
        private static readonly List<Action> FlushActions = new List<Action>();
        private static readonly MetricTable<MetricRoot> MetricsByName = new MetricTable<MetricRoot>();
        private static readonly MetricTable<HelperMetric> HelperByName = new MetricTable<HelperMetric>();

        private sealed class MetricTable<TV>
        {
            private const int ThreadLocalMask = (1 << 12) -1;
            private int _tlsPopulated;
            private readonly Dictionary<MetricName, TV> _shared = new Dictionary<MetricName, TV>();
            private readonly ThreadLocal<TlsStorage> _local = new ThreadLocal<TlsStorage>(() => new TlsStorage(), true);

            private sealed class TlsStorage
            {
                private int _dirty;
                private readonly (MetricName, TV)[] _storage = new (MetricName, TV)[ThreadLocalMask + 1];

                public void Invalidate() => Interlocked.Exchange(ref _dirty, 1);

                public ref (MetricName Key, TV Value) GetSlot(in MetricName name)
                {
                    if (Interlocked.Exchange(ref _dirty, 0) != 0)
                        Array.Clear(_storage, 0, _storage.Length);
                    return ref _storage[name.GetReferenceHashCode() & ThreadLocalMask];
                }
            }

            public T GetOrCreateInternal<T>(in MetricName name, Func<MetricName, T> creator) where T : TV
            {
                if (!_shared.TryGetValue(name, out var group))
                    _shared.Add(name, group = creator(name));
                return (T)group;
            }

            public bool GetAndRemoveInternal(in MetricName name, out TV metric)
            {
                if (!_shared.TryGetValue(name, out metric))
                    return false;
                _shared.Remove(name);

                if (Interlocked.Exchange(ref _tlsPopulated, 0) != 0)
                    foreach (var cached in _local.Values)
                        cached.Invalidate();
                return true;
            }

            public T GetOrCreate<T>(in MetricName name, Func<MetricName, T> creator) where T : TV
            {
                ref var tlsSlot = ref _local.Value.GetSlot(in name);
                if (tlsSlot.Key.ReferenceEquals(in name))
                {
                    return (T)tlsSlot.Value;
                }

                using (Lock.AcquireSharedUsing())
                    if (_shared.TryGetValue(name, out var group))
                    {
                        tlsSlot.Key = name;
                        tlsSlot.Value = group;
                        Interlocked.CompareExchange(ref _tlsPopulated, 1, 0);
                        return (T)group;
                    }

                using (Lock.AcquireExclusiveUsing())
                {
                    var group = GetOrCreateInternal(in name, creator);
                    tlsSlot.Key = name;
                    tlsSlot.Value = group;
                    Interlocked.CompareExchange(ref _tlsPopulated, 1, 0);
                    return group;
                }
            }
        }

        private static long _gcCounter;
        public static ulong GcCounter => (ulong) Volatile.Read(ref _gcCounter);

        private static Func<MetricName, T> WrapMetricFactory<T, TR>(List<TR> target, Func<MetricName, T> factory) where T : TR
        {
            return name =>
            {
                var metric = factory(name);
                target.Add(metric);
                return metric;
            };
        }

        private static readonly Func<MetricName, MetricGroup> MakeGroup = WrapMetricFactory(Metrics, name => new MetricGroup(name));
        private static readonly Func<MetricName, Timer> MakeTimer = WrapMetricFactory(Metrics, name => new Timer(name));
        private static readonly Func<MetricName, Histogram> MakeHistogram = WrapMetricFactory(Metrics, name => new Histogram(name));

        private static readonly Func<MetricName, PerTickTimer> MakePerTickTimer = WrapMetricFactory(Helpers, name => new PerTickTimer(
            in name,
            GetOrCreateInternal(name.WithSuffix(".tick.count"), MetricsByName, MakeHistogram),
            GetOrCreateInternal(name.WithSuffix(".tick.time"), MetricsByName, MakeTimer)));

        private static readonly Func<MetricName, PerTickTimer> MakePerTickPerRecordTimer = WrapMetricFactory(Helpers, name => new PerTickTimer(
            in name,
            GetOrCreateInternal(name.WithSuffix(".each.time"), MetricsByName, MakeTimer),
            GetOrCreateInternal(name.WithSuffix(".tick.count"), MetricsByName, MakeHistogram),
            GetOrCreateInternal(name.WithSuffix(".tick.time"), MetricsByName, MakeTimer)));

        private static readonly Func<MetricName, PerTickAdder> MakePerTickAdder = WrapMetricFactory(Helpers, name => new PerTickAdder(
            in name,
            GetOrCreateInternal(name.WithSuffix(".tick.count"), MetricsByName, MakeHistogram),
            GetOrCreateInternal(name.WithSuffix(".tick.sum"), MetricsByName, MakeHistogram)));

        private static readonly Func<MetricName, PerTickAdder> MakePerTickPerRecordAdder = WrapMetricFactory(Helpers, name => new PerTickAdder(
            in name,
            GetOrCreateInternal(name.WithSuffix(".each.sum"), MetricsByName, MakeHistogram),
            GetOrCreateInternal(name.WithSuffix(".tick.count"), MetricsByName, MakeHistogram),
            GetOrCreateInternal(name.WithSuffix(".tick.sum"), MetricsByName, MakeHistogram)));

        private static readonly Func<MetricName, PerTickTimerAdder> MakePerTickTimerAdder = WrapMetricFactory(Helpers, name => new PerTickTimerAdder(
            in name,
            GetOrCreateInternal(name.WithSuffix(".tick.count"), MetricsByName, MakeHistogram),
            GetOrCreateInternal(name.WithSuffix(".tick.time"), MetricsByName, MakeTimer),
            GetOrCreateInternal(name.WithSuffix(".tick.sum"), MetricsByName, MakeHistogram)));

        private static readonly Func<MetricName, PerTickTimerAdder> MakePerTickPerRecordTimerAdder = WrapMetricFactory(Helpers, name => new PerTickTimerAdder(
            in name,
            GetOrCreateInternal(name.WithSuffix(".each.time"), MetricsByName, MakeTimer),
            GetOrCreateInternal(name.WithSuffix(".each.sum"), MetricsByName, MakeHistogram),
            GetOrCreateInternal(name.WithSuffix(".tick.count"), MetricsByName, MakeHistogram),
            GetOrCreateInternal(name.WithSuffix(".tick.time"), MetricsByName, MakeTimer),
            GetOrCreateInternal(name.WithSuffix(".tick.sum"), MetricsByName, MakeHistogram)));

        private static readonly Func<MetricName, PerTickCounter> MakePerTickCounter = WrapMetricFactory(Helpers, name => new PerTickCounter(
            in name,
            GetOrCreateInternal(name, MetricsByName, MakeHistogram)));

        private static T GetOrCreateInternal<T, TR>(in MetricName name, MetricTable<TR> table, Func<MetricName, T> creator) where T : TR
        {
            return table.GetOrCreateInternal(in name, creator);
        }

        private static T GetOrCreate<T, TR>(in MetricName name, MetricTable<TR> table, Func<MetricName, T> creator) where T : TR
        {
            return table.GetOrCreate(in name, creator);
        }

        public static bool RemoveMetric(in MetricName name)
        {
            using (Lock.AcquireExclusiveUsing())
            {
                if (!MetricsByName.GetAndRemoveInternal(name, out var metric))
                    return false;
                Metrics.Remove(metric);
                return true;
            }
        }

        public static bool RemoveHelper(in MetricName name)
        {
            using (Lock.AcquireExclusiveUsing())
            {
                if (!HelperByName.GetAndRemoveInternal(name, out var helper)) return false;
                Helpers.Remove(helper);
                foreach (var metric in helper.GetOutputMetrics())
                {
                    Metrics.Remove(metric);
                    MetricsByName.GetAndRemoveInternal(metric.Name, out _);
                }

                return true;
            }
        }

        public static MetricGroup Group(in MetricName name) => GetOrCreate(in name, MetricsByName, MakeGroup);
        public static Timer Timer(in MetricName name) => GetOrCreate(in name, MetricsByName, MakeTimer);
        public static Histogram Histogram(in MetricName name) => GetOrCreate(in name, MetricsByName, MakeHistogram);

        public static PerTickCounter PerTickCounter(in MetricName name) => GetOrCreate(in name, HelperByName, MakePerTickCounter);

        public static PerTickTimer PerTickTimer(in MetricName name, bool perRecord = false) =>
            GetOrCreate(in name, HelperByName, perRecord ? MakePerTickPerRecordTimer : MakePerTickTimer);

        public static PerTickAdder PerTickAdder(in MetricName name, bool perRecord = false) =>
            GetOrCreate(in name, HelperByName, perRecord ? MakePerTickPerRecordAdder : MakePerTickAdder);

        public static PerTickTimerAdder PerTickTimerAdder(in MetricName name, bool perRecord = false) =>
            GetOrCreate(in name, HelperByName, perRecord ? MakePerTickPerRecordTimerAdder : MakePerTickTimerAdder);

        public static void RegisterFlushAction(Action action)
        {
            using (Lock.AcquireExclusiveUsing())
                FlushActions.Add(action);
        }

        public static void CollectGarbage(ulong age)
        {
            var gcTime = (ulong)Interlocked.Increment(ref _gcCounter);
            if (gcTime < age) return;
            var deleteBefore = gcTime - age;
            using (PoolManager.Get(out List<object> toRemove))
            {
                using (Lock.AcquireSharedUsing())
                {
                    foreach (var helper in Helpers)
                        if (helper.LastModification < deleteBefore)
                            toRemove.Add(helper);
                    foreach (var metric in Metrics)
                        if (metric.LastModification < deleteBefore)
                            toRemove.Add(metric);
                }

                if (toRemove.Count == 0) return;
                using (Lock.AcquireExclusiveUsing())
                    foreach (var remove in toRemove)
                        switch (remove)
                        {
                            case HelperMetric helper:
                                Helpers.Remove(helper);
                                HelperByName.GetAndRemoveInternal(helper.Name, out _);
                                break;
                            case MetricRoot root:
                                Metrics.Remove(root);
                                MetricsByName.GetAndRemoveInternal(root.Name, out _);
                                break;
                        }
            }
        }

        public static ListReader<MetricRoot> Read()
        {
            ListReader<HelperMetric> helpers = Helpers;
            // ReSharper disable once ForCanBeConvertedToForeach
            for (var i = 0; i < helpers.Count; i++)
                try
                {
                    helpers[i].Flush();
                }
                catch
                {
                    // ignored
                }

            ListReader<Action> flushActions = FlushActions;
            // ReSharper disable once ForCanBeConvertedToForeach
            for (var i = 0; i < flushActions.Count; i++)
                try
                {
                    flushActions[i]();
                }
                catch
                {
                    // ignored
                }

            return Metrics;
        }
    }
}