using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using Google.FlatBuffers;

namespace Meds.Shared
{
    public sealed class FlatBufferPool
    {
        public static FlatBufferPool Instance { get; } = new FlatBufferPool();

        private readonly ConcurrentBag<BufferHolder> _pool = new ConcurrentBag<BufferHolder>();

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

            public FlatBufferBuilder Builder
            {
                get
                {
                    GuardAccess();
                    return _holder.Builder;
                }
            }

            public ByteBuffer Buffer => Builder.DataBuffer;

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
            private readonly FlatBufferPool _owner;
            private volatile int _refCount;
            private volatile int _generation;
            public FlatBufferBuilder Builder { get; }

            public int Generation => _generation;

            // ReSharper disable once ParameterOnlyUsedForPreconditionCheck.Global
            public void GuardAccess(int generation)
            {
                if (_refCount <= 0)
                    throw new NullReferenceException("Attempt to access builder of pooled token");
                if (_generation != generation)
                    throw new NullReferenceException("Token is on a newer generation, and is being accessed from a previous one");
            }

            internal BufferHolder(FlatBufferPool owner)
            {
                _owner = owner;
                _refCount = 0;
                Builder = new FlatBufferBuilder(128);
            }

            internal void Resurrect()
            {
                if (Interlocked.CompareExchange(ref _refCount, 1, 0) != 0)
                    throw new Exception($"Failed to resurrect pooled token.  Is it still in use?  ref:{_refCount}");
                ResetBuilder(Builder);
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

                ResetBuilder(Builder);
                _owner._pool.Add(this);
                Interlocked.Increment(ref _generation);
            }
        }

        private static readonly Func<FlatBufferBuilder, Dictionary<string, StringOffset>> FlatBufferBuilderSharedStrings = CreateSharedStringsAccessor();

        private static void ResetBuilder(FlatBufferBuilder builder)
        {
            builder.Clear();
            FlatBufferBuilderSharedStrings(builder)?.Clear();
        }

        private static Func<FlatBufferBuilder, Dictionary<string, StringOffset>> CreateSharedStringsAccessor()
        {
            var fieldInfo = typeof(FlatBufferBuilder).GetField("_sharedStringMap", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) ??
                            throw new NullReferenceException("Failed to find _sharedStringMap in FlatBufferBuilder");
            var dyn = new DynamicMethod("getFlatBufferBuilderSharedStrings", typeof(Dictionary<string, StringOffset>), new[] {typeof(FlatBufferBuilder)},
                typeof(FlatBufferBuilder));
            var il = dyn.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, fieldInfo);
            il.Emit(OpCodes.Ret);
            return (Func<FlatBufferBuilder, Dictionary<string, StringOffset>>) dyn.CreateDelegate(
                typeof(Func<FlatBufferBuilder, Dictionary<string, StringOffset>>));
        }
    }
}