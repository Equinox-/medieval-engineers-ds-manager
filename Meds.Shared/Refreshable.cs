using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace Meds.Shared
{
    public class Refreshable<T>
    {
        private readonly IEqualityComparer<T> _equality;
        private readonly List<IObserver> _observers = new List<IObserver>();
        private readonly Action<T> _disposer;

        public T Current { get; private set; }

        protected Refreshable(T initial, Action<T> disposer = null, IEqualityComparer<T> equality = null)
        {
            _disposer = disposer;
            _equality = equality ?? EqualityComparer<T>.Default;
            Current = initial;
        }

        protected bool Update(T newValue)
        {
            lock (_observers)
            {
                var oldValue = Current;
                if (_equality.Equals(oldValue, newValue))
                    return false;
                Current = newValue;
                var removed = 0;
                for (var i = 0; i < _observers.Count; i++)
                {
                    if (!_observers[i].Changed(newValue))
                    {
                        removed++;
                        continue;
                    }

                    if (removed > 0)
                        _observers[i - removed] = _observers[i];
                }

                if (removed > 0)
                    _observers.RemoveRange(_observers.Count - removed, removed);
                if (_disposer != null)
                    _disposer.Invoke(oldValue);
                else if (oldValue is IDisposable disposable)
                    disposable.Dispose();
                return true;
            }
        }

        public Refreshable<TOut> Map<TOut>(Func<T, TOut> map, IEqualityComparer<TOut> equality = null, Action<TOut> disposer = null)
        {
            lock (_observers)
            {
                var target = new Refreshable<TOut>(map(Current), disposer, equality);
                _observers.Add(new Mapped<TOut>(target, map));
                return target;
            }
        }

        public Refreshable<TOut> Combine<TOther, TOut>(
            Refreshable<TOther> other,
            Func<T, TOther, TOut> map,
            IEqualityComparer<TOut> equality = null,
            Action<TOut> disposer = null)
        {
            lock (_observers)
            lock (other._observers)
            {
                var initialThis = Current;
                var initialOther = other.Current;
                var target = new Refreshable<TOut>(map(initialThis, initialOther), disposer, equality);
                var combine = new MappedCombine<TOther, TOut>(target, initialThis, initialOther, map);
                _observers.Add(new MappedCombine<TOther, TOut>.LeftObserver(combine));
                other._observers.Add(new MappedCombine<TOther, TOut>.RightObserver(combine));
                return target;
            }
        }

        public IDisposable Subscribe(Action<T> subscription)
        {
            lock (_observers)
            {
                subscription(Current);
                var subscriber = new Subscriber(this, subscription);
                _observers.Add(subscriber);
                return subscriber;
            }
        }


        private interface IObserver
        {
            /// <returns>true if the observer is still valid</returns>
            bool Changed(T newValue);
        }

        private class Mapped<TOut> : IObserver
        {
            private readonly WeakReference<Refreshable<TOut>> _target;
            private readonly Func<T, TOut> _transformer;

            public Mapped(Refreshable<TOut> target, Func<T, TOut> transformer)
            {
                _transformer = transformer;
                _target = new WeakReference<Refreshable<TOut>>(target);
            }

            public bool Changed(T newValue)
            {
                if (!_target.TryGetTarget(out var target))
                    return false;
                target.Update(_transformer(newValue));
                return true;
            }
        }

        private class MappedCombine<TOther, TOut>
        {
            private readonly WeakReference<Refreshable<TOut>> _target;
            private readonly Func<T, TOther, TOut> _transformer;

            private T _left;
            private TOther _right;

            public MappedCombine(
                Refreshable<TOut> target,
                T leftInitial,
                TOther rightInitial,
                Func<T, TOther, TOut> transformer)
            {
                _transformer = transformer;
                _left = leftInitial;
                _right = rightInitial;
                _target = new WeakReference<Refreshable<TOut>>(target);
            }

            private bool Changed()
            {
                if (!_target.TryGetTarget(out var target))
                    return false;
                target.Update(_transformer(_left, _right));
                return true;
            }


            public class LeftObserver : IObserver
            {
                private readonly MappedCombine<TOther, TOut> _owner;

                public LeftObserver(MappedCombine<TOther, TOut> owner) => _owner = owner;

                public bool Changed(T newValue)
                {
                    _owner._left = newValue;
                    return _owner.Changed();
                }
            }

            public class RightObserver : Refreshable<TOther>.IObserver
            {
                private readonly MappedCombine<TOther, TOut> _owner;

                public RightObserver(MappedCombine<TOther, TOut> owner) => _owner = owner;

                public bool Changed(TOther newValue)
                {
                    _owner._right = newValue;
                    return _owner.Changed();
                }
            }
        }

        private class Subscriber : IObserver, IDisposable
        {
            private readonly Refreshable<T> _owner;
            private readonly Action<T> _callback;

            public Subscriber(Refreshable<T> owner, Action<T> callback)
            {
                _owner = owner;
                _callback = callback;
            }

            public bool Changed(T newValue)
            {
                _callback(newValue);
                return true;
            }

            public void Dispose()
            {
                lock (_owner._observers)
                    _owner._observers.Remove(this);
            }
        }
    }

    public class ConfigRefreshable<T> : Refreshable<T> where T : IEquatable<T>
    {
        private readonly string _path;
        private readonly Func<string, T> _load;
        private DateTime _lastFileTime;

        protected ConfigRefreshable(string path, Func<string, T> load, T initial, DateTime lastFileTime) : base(initial)
        {
            _path = path;
            _load = load;
            _lastFileTime = lastFileTime;
        }


        public static ConfigRefreshable<T> FromConfigFile(string path, XmlSerializer serializer)
        {
            return FromConfigFile(
                path,
                pathCapture => (T)serializer.Deserialize(new FileStream(pathCapture, FileMode.Open, FileAccess.Read)));
        }

        public static ConfigRefreshable<T> FromConfigFile(string path, Func<string, T> load)
        {
            var initialFileTime = new FileInfo(path).LastWriteTimeUtc;
            var initialState = load(path);
            return new ConfigRefreshable<T>(path, load, initialState, initialFileTime);
        }

        public Refresher Refreshing(IServiceProvider svc) => new RefresherImpl(svc, this);

        private bool Refresh()
        {
            var fileTime = new FileInfo(_path).LastWriteTimeUtc;
            if (fileTime == _lastFileTime)
                return false;
            var newState = _load(_path);
            var result = Update(newState);
            _lastFileTime = fileTime;
            return result;
        }

        public abstract class Refresher : BackgroundService
        {
        }

        private class RefresherImpl : Refresher
        {
            private readonly ConfigRefreshable<T> _owner;
            private readonly ILogger<ConfigRefreshable<T>> _log;

            public RefresherImpl(IServiceProvider svc, ConfigRefreshable<T> owner)
            {
                _owner = owner;
                _log = svc.GetRequiredService<ILogger<ConfigRefreshable<T>>>();
            }

            protected override async Task ExecuteAsync(CancellationToken stoppingToken)
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    try
                    {
                        if (_owner.Refresh())
                            _log.ZLogInformation("Reloading configuration file {0} as {1}", _owner._path, typeof(T).Name);
                    }
                    catch (Exception err)
                    {
                        _log.ZLogWarning(err, "Failed to reload configuration file {0} as {1}", _owner._path, typeof(T).Name);
                    }
                }
            }
        }
    }
}