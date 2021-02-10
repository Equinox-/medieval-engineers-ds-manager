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
    public static class FlatBufferPool
    {
        public static RefCountedObjectPool<FlatBufferBuilder> Instance { get; } = new RefCountedObjectPool<FlatBufferBuilder>(
            () => new FlatBufferBuilder(128),
            ResetBuilder);

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