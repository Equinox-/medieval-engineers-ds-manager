using System;
using System.Collections.Generic;
using Google.FlatBuffers;
using Meds.Shared.Data;

namespace Meds.Shared
{
    public sealed class PacketDistributor
    {
        private readonly Dictionary<Message, Action<MessageToken>> _packetQueues;

        public PacketDistributor()
        {
            _packetQueues = new Dictionary<Message, Action<MessageToken>>();
        }

        public void RegisterPacketHandler(Action<MessageToken> consumer, params Message[] types)
        {
            foreach (var type in types)
                _packetQueues.Add(type, consumer);
        }

        public void Distribute(in MessageToken pkt)
        {
            _packetQueues[pkt.Type](pkt);
        }

        public struct MessageToken : IDisposable
        {
            private readonly FlatBufferPool.Token _token;
            private readonly int _id;
            private Packet _packet;

            public MessageToken(FlatBufferPool.Token token, in Packet packet, int id)
            {
                _token = token;
                _packet = packet;
                _id = id;
            }

            public MessageToken AddRef()
            {
                return new MessageToken(_token.AddRef(), in _packet, _id);
            }

            public Message Type
            {
                get
                {
                    _token.GuardAccess();
                    return _packet.DataType(_id);
                }
            }

            public T Value<T>() where T : struct, IFlatbufferObject
            {
                _token.GuardAccess();
                return _packet.Data<T>(_id) ?? throw new NullReferenceException("packet data missing");
            }

            public void Dispose()
            {
                _token.Dispose();
                _packet = default;
            }
        }
    }
}