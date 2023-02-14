using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using VRage.Library.Extensions;
using VRageMath;

namespace Meds.Standalone.Output
{
    public sealed class StringContentBuilder
    {
        private static readonly Encoder Encoder = Encoding.UTF8.GetEncoder();

        private readonly char[] _temp = new char[4096];
        private byte[] _buffer;
        public int Position { get; private set; }

        private void EnsureCapacity(int count)
        {
            if (_buffer != null && _buffer.Length >= Position + count)
                return;
            Array.Resize(ref _buffer, MathHelper.GetNearestBiggerPowerOfTwo(Position + count));
        }

        public void Append(StringBuilder sb)
        {
            var offset = 0;
            EnsureCapacity(Encoding.UTF8.GetMaxByteCount(sb.Length));
            while (offset < sb.Length)
            {
                var count = Math.Min(_temp.Length, sb.Length - offset);
                sb.CopyTo(offset, _temp, 0, count);
                Position += Encoder.GetBytes(_temp, 0, count, _buffer, Position, true);
                offset += count;
            }
        }

        public void Append(string str)
        {
            if (str.Length == 0)
                return;
            if (str.Length == 1 && str[0] < 128)
            {
                EnsureCapacity(1);
                _buffer[Position++] = (byte) str[0];
                return;
            }

            EnsureCapacity(Encoding.UTF8.GetMaxByteCount(str.Length));
            unsafe
            {
                fixed (char* chars = str)
                fixed (byte* bytes = _buffer)
                {
                    Position += Encoder.GetBytes(chars, str.Length, bytes + Position, _buffer.Length - Position, true);
                }
            }
        }

        public void FlushTo(Stream target)
        {
            target.Write(_buffer, 0, Position);
            Position = 0;
        }
    }
}