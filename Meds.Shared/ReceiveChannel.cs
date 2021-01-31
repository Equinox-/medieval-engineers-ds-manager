using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Google.FlatBuffers;
using Meds.Shared.Data;

namespace Meds.Shared
{
    public sealed class ReceiveChannel
    {
        private readonly Stream _src;
        private readonly FlatBufferPool _pool;
        private readonly byte[] _sizeBuf = new byte[4];
        private readonly ByteBuffer _sizeBufAccessor;
        private readonly PacketDistributor _distributor;

        public ReceiveChannel(PacketDistributor distributor, Stream src)
        {
            _src = src;
            _pool = FlatBufferPool.Instance;
            _sizeBufAccessor = new ByteBuffer(_sizeBuf);
            _distributor = distributor;
        }

        private void Read(byte[] target, int offset, int count)
        {
            while (count > 0)
            {
                var read = _src.Read(target, offset, count);
                if (read < 0)
                    throw new OperationCanceledException();
                offset += read;
                count -= read;
            }
        }

        public void Poll()
        {
            while (true)
            {
                Read(_sizeBuf, 0, 4);
                var size = _sizeBufAccessor.GetInt(0);
                using (var pooled = _pool.Borrow())
                {
                    var buf = pooled.Buffer;
                    if (buf.Length < size)
                    {
                        var len = buf.Length;
                        while (len < size)
                            len *= 2;
                        buf.GrowFront(len);
                    }

                    var array = buf.ToArraySegment(0, buf.Length).Array;
                    var offset = buf.Length - size;
                    buf.Position = offset;
                    Read(array, offset, size);
                    var packet = Packet.GetRootAsPacket(buf);
                    for (var i = 0; i < packet.DataLength; i++)
                        _distributor.Distribute(new PacketDistributor.MessageToken(pooled, in packet, i));
                }
            }
        }
    }
}