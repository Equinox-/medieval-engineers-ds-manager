using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using Google.FlatBuffers;

namespace Meds.Shared
{
    public sealed class SendChannel : IDisposable
    {
        public delegate void DelWriteMessage(byte[] buffer, int offset, int count);
        private readonly BlockingCollection<RefCountedObjectPool<FlatBufferBuilder>.Token> _sendQueue = new BlockingCollection<RefCountedObjectPool<FlatBufferBuilder>.Token>();
        private readonly CancellationTokenSource _cancelToken = new CancellationTokenSource();
        private readonly DelWriteMessage _dest;
        private volatile uint _threadId;
        private readonly Thread _thread;

        public SendChannel(string threadName, DelWriteMessage dest)
        {
            _dest = dest;
            _thread = new Thread(PipeUtils.WrapPipeThread(Dispatch)) {Name = threadName, IsBackground = true};
            _thread.Start();
        }

        private void Dispatch()
        {
            while (!_cancelToken.IsCancellationRequested)
            {
                using (var msg = _sendQueue.Take(_cancelToken.Token))
                {
                    var buf = msg.Value.DataBuffer;
                    var segment = buf.ToArraySegment(buf.Position, buf.Length - buf.Position);
                    _threadId = PipeUtils.CurrentNativeThreadId;
                    _dest(segment.Array, segment.Offset, segment.Count);
                }
            }
        }

        public void Send(RefCountedObjectPool<FlatBufferBuilder>.Token message)
        {
            _sendQueue.Add(message.AddRef());
        }

        public void Dispose()
        {
            _cancelToken.Cancel();
            if (!_thread.Join(500))
            {
                PipeUtils.CancelSynchronousIo(_threadId);
                _thread.Abort();
            }

            _cancelToken.Dispose();
        }
    }
}