using System;
using System.Collections.Generic;
using Meds.Metrics.Group;
using VRage.Collections;
using VRage.Library.Threading;

namespace Meds.Metrics
{
    public static class MetricRegistry
    {
        private static readonly FastResourceLock Lock = new FastResourceLock();
        private static readonly List<IHelperMetric> Helpers = new List<IHelperMetric>();
        private static readonly List<MetricRoot> Metrics = new List<MetricRoot>();
        private static readonly List<Action> FlushActions = new List<Action>();
        private static readonly Dictionary<MetricName, MetricRoot> MetricsByName = new Dictionary<MetricName, MetricRoot>();
        private static readonly Dictionary<MetricName, IHelperMetric> HelperByName = new Dictionary<MetricName, IHelperMetric>();

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
            GetOrCreateInternal(name.WithSuffix(".each.time"), MetricsByName, MakeTimer),
            GetOrCreateInternal(name.WithSuffix(".tick.count"), MetricsByName, MakeHistogram),
            GetOrCreateInternal(name.WithSuffix(".tick.time"), MetricsByName, MakeTimer)));

        private static readonly Func<MetricName, PerTickAdder> MakePerTickAdder = WrapMetricFactory(Helpers, name => new PerTickAdder(
            GetOrCreateInternal(name.WithSuffix(".each.sum"), MetricsByName, MakeHistogram),
            GetOrCreateInternal(name.WithSuffix(".tick.count"), MetricsByName, MakeHistogram),
            GetOrCreateInternal(name.WithSuffix(".tick.sum"), MetricsByName, MakeHistogram)));

        private static readonly Func<MetricName, PerTickTimerAdder> MakePerTickTimerAdder = WrapMetricFactory(Helpers, name => new PerTickTimerAdder(
            GetOrCreateInternal(name.WithSuffix(".each.time"), MetricsByName, MakeTimer),
            GetOrCreateInternal(name.WithSuffix(".each.sum"), MetricsByName, MakeHistogram),
            GetOrCreateInternal(name.WithSuffix(".tick.count"), MetricsByName, MakeHistogram),
            GetOrCreateInternal(name.WithSuffix(".tick.time"), MetricsByName, MakeTimer),
            GetOrCreateInternal(name.WithSuffix(".tick.sum"), MetricsByName, MakeHistogram)));

        private static readonly Func<MetricName, PerTickCounter> MakePerTickCounter = WrapMetricFactory(Helpers, name => new PerTickCounter(
            GetOrCreateInternal(name, MetricsByName, MakeHistogram)));

        private static T GetOrCreateInternal<T, TR>(in MetricName name, Dictionary<MetricName, TR> table, Func<MetricName, T> creator) where T : TR
        {
            if (!table.TryGetValue(name, out var group))
                table.Add(name, group = creator(name));
            return (T) group;
        }

        private static T GetOrCreate<T, TR>(in MetricName name, Dictionary<MetricName, TR> table, Func<MetricName, T> creator) where T : TR
        {
            using (Lock.AcquireSharedUsing())
                if (table.TryGetValue(name, out var group))
                    return (T) group;
            using (Lock.AcquireExclusiveUsing())
                return GetOrCreateInternal(in name, table, creator);
        }

        public static MetricGroup Group(in MetricName name) => GetOrCreate(in name, MetricsByName, MakeGroup);
        public static Timer Timer(in MetricName name) => GetOrCreate(in name, MetricsByName, MakeTimer);
        public static Histogram Histogram(in MetricName name) => GetOrCreate(in name, MetricsByName, MakeHistogram);

        public static PerTickTimer PerTickTimer(in MetricName name) => GetOrCreate(in name, HelperByName, MakePerTickTimer);
        public static PerTickCounter PerTickCounter(in MetricName name) => GetOrCreate(in name, HelperByName, MakePerTickCounter);
        public static PerTickAdder PerTickAdder(in MetricName name) => GetOrCreate(in name, HelperByName, MakePerTickAdder);
        public static PerTickTimerAdder PerTickTimerAdder(in MetricName name) => GetOrCreate(in name, HelperByName, MakePerTickTimerAdder);

        public static void RegisterFlushAction(Action action)
        {
            using (Lock.AcquireExclusiveUsing())
                FlushActions.Add(action);
        }

        public static ListReader<MetricRoot> Read()
        {
            ListReader<IHelperMetric> helpers = Helpers;
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