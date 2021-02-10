using System;
using System.Collections.Concurrent;
using System.Threading;
using Google.FlatBuffers;

namespace Meds.Shared
{
    public sealed class RefCountedObjectPool<T>
    {
        private readonly ConcurrentBag<BufferHolder> _pool = new ConcurrentBag<BufferHolder>();
        
        private readonly Func<T> _factory;
        private readonly Action<T> _reset;

        public RefCountedObjectPool(Func<T> factory, Action<T> reset)
        {
            _factory = factory;
            _reset = reset;
        }

        public Token Borrow()
        {
            if (!_pool.TryTake(out var holder))
                holder = new BufferHolder(this);
            return new Token(holder);
        }

        public readonly struct Token : IDisposable
        {
            private readonly BufferHolder _holder;
            private readonly int _generation;

            internal Token(BufferHolder holder)
            {
                holder.Resurrect();
                _holder = holder;
                _generation = holder.Generation;
            }

            public bool Valid => _holder != null && _holder.Generation == _generation;

            public T Value
            {
                get
                {
                    GuardAccess();
                    return _holder.Value;
                }
            }

            public void GuardAccess() => _holder.GuardAccess(_generation);

            public Token AddRef()
            {
                _holder.AddRef();
                return this;
            }

            public void Dispose()
            {
                _holder?.Dispose();
            }
        }

        internal sealed class BufferHolder : IDisposable
        {
            private readonly RefCountedObjectPool<T> _owner;
            private volatile int _refCount;
            private volatile int _generation;
            public T Value { get; }

            public int Generation => _generation;

            // ReSharper disable once ParameterOnlyUsedForPreconditionCheck.Global
            public void GuardAccess(int generation)
            {
                if (_refCount <= 0)
                    throw new NullReferenceException("Attempt to access builder of pooled token");
                if (_generation != generation)
                    throw new NullReferenceException("Token is on a newer generation, and is being accessed from a previous one");
            }

            internal BufferHolder(RefCountedObjectPool<T> owner)
            {
                _owner = owner;
                _refCount = 0;
                Value = owner._factory();
            }

            internal void Resurrect()
            {
                if (Interlocked.CompareExchange(ref _refCount, 1, 0) != 0)
                    throw new Exception($"Failed to resurrect pooled token.  Is it still in use?  ref:{_refCount}");
                _owner._reset(Value);
            }

            public void AddRef()
            {
                var inc = Interlocked.Increment(ref _refCount);
                if (inc > 1)
                    return;
                Interlocked.Decrement(ref _refCount);
                throw new Exception("Failed to add a ref to an already returned pool token");
            }

            public void Dispose()
            {
                var dec = Interlocked.Decrement(ref _refCount);
                if (dec > 0)
                    return;
                if (dec < 0)
                {
                    Interlocked.Increment(ref _refCount);
                    throw new Exception("Tried to double dispose a pool token");
                }

                _owner._reset(Value);
                _owner._pool.Add(this);
                Interlocked.Increment(ref _generation);
            }
        }
    }
}