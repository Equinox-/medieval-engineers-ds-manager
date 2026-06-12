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
            var objParm = Expression.Parameter(typeof(FlatBufferBuilder), "obj");
            var field = Expression.Field(objParm, "_sharedStringMap");
            return Expression.Lambda<Func<FlatBufferBuilder, Dictionary<string, StringOffset>>>(field, objParm).Compile();
        }
    }
}