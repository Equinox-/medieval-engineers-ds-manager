using System;
using System.IO.Pipes;
using System.Threading;
using Meds.Shared;

namespace Meds.Wrapper
{
    public sealed class PipeClient : IDisposable
    {
        private readonly NamedPipeClientStream _toServer;
        private readonly NamedPipeClientStream _fromServer;
        private readonly ReceiveChannel _receiver;
        private volatile uint _receiverThreadId;
        private readonly Thread _receiverThread;
        private readonly SendChannel _sender;
        private readonly ThreadLocal<PacketBuffer> _packetBuffers;

        public PipeClient(ChannelDesc desc, PacketDistributor distributor)
        {
            _toServer = new NamedPipeClientStream(".", desc.PipeNameToServer, PipeDirection.Out);
            _fromServer = new NamedPipeClientStream(".", desc.PipeNameToClients, PipeDirection.In);
            _toServer.Connect();
            _fromServer.Connect();
            _receiver = new ReceiveChannel(distributor, _fromServer);
            _receiverThread = new Thread(PipeUtils.WrapPipeThread(Poll)) {Name = $"client-{GetHashCode():X}-reader", IsBackground = true};
            _receiverThread.Start();
            _sender = new SendChannel($"client-{GetHashCode():X}-sender", _toServer.Write);
            _packetBuffers = new ThreadLocal<PacketBuffer>(() => new PacketBuffer(_sender.Send));
        }

        private void Poll()
        {
            _receiverThreadId = PipeUtils.CurrentNativeThreadId;
            _receiver.Poll();
        }

        public PacketBuffer SendBuffer => _packetBuffers.Value;

        public void Dispose()
        {
            _toServer.Dispose();
            _fromServer.Dispose();
            if (!_receiverThread.Join(500))
            {
                PipeUtils.CancelSynchronousIo(_receiverThreadId);
                _receiverThread.Abort();
            }
            _sender.Dispose();
        }
    }
}