using System;
using System.Collections.Generic;
using Google.FlatBuffers;
using Meds.Shared.Data;

namespace Meds.Shared
{
    public sealed class PacketBuffer
    {
        private readonly Action<RefCountedObjectPool<FlatBufferBuilder>.Token> _sender;
        private List<KeyValuePair<Message, int>> _messagesOther = new List<KeyValuePair<Message, int>>();
        private List<KeyValuePair<Message, int>> _messages = new List<KeyValuePair<Message, int>>();
        private RefCountedObjectPool<FlatBufferBuilder>.Token _token;
        public FlatBufferBuilder Builder => _token.Value;

        public PacketBuffer(Action<RefCountedObjectPool<FlatBufferBuilder>.Token> sender)
        {
            _sender = sender;
            _token = FlatBufferPool.Instance.Borrow();
        }

        public void EndMessage(Message type)
        {
            var table = Builder.EndTable();
            _messages.Add(new KeyValuePair<Message, int>(type, table));
            if (Builder.DataBuffer.Length - Builder.DataBuffer.Position > 1024)
                Flush();
        }

        public void Flush()
        {
            if (_messages.Count == 0)
                return;
            var tok = _token;
            var messages = _messages;
            _messages = _messagesOther;
            _messages.Clear();
            _messagesOther = messages;
            _token = FlatBufferPool.Instance.Borrow();
            using (tok)
            {
                var builder = tok.Value;
                Packet.StartDataVector(builder, messages.Count);
                foreach (var msg in messages)
                    builder.AddOffset(msg.Value);
                var dataVector = builder.EndVector();
                Packet.StartDataTypeVector(builder, messages.Count);
                foreach (var msg in messages)
                    builder.AddByte((byte) msg.Key);
                var dataTypeVector = builder.EndVector();

                Packet.StartPacket(builder);
                Packet.AddData(builder, dataVector);
                Packet.AddDataType(builder, dataTypeVector);
                var packetOffset = Packet.EndPacket(builder);
                builder.FinishSizePrefixed(packetOffset.Value);
                _sender(tok);
            }
        }
    }
}