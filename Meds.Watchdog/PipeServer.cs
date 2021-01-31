using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using Meds.Shared;

namespace Meds.Watchdog
{
    public sealed class PipeServer : IDisposable
    {
        private readonly SendChannel _sender;
        private readonly ConnectionPool<SendConnection> _sendPool;
        private readonly ConnectionPool<ReceiveConnection> _receivePool;
        private readonly ThreadLocal<PacketBuffer> _packetBuffers;

        public PipeServer(ChannelDesc desc, PacketDistributor distributor)
        {
            _receivePool = new ConnectionPool<ReceiveConnection>(desc.PipeNameToServer, PipeDirection.In,
                (owner, stream) => new ReceiveConnection(distributor, owner, stream));
            _sendPool = new ConnectionPool<SendConnection>(desc.PipeNameToClients, PipeDirection.Out,
                (owner, stream) => new SendConnection(owner, stream));
            _sender = new SendChannel("server-sender", BroadcastMessageInternal);
            _packetBuffers = new ThreadLocal<PacketBuffer>(() => new PacketBuffer(_sender.Send));
        }

        public PacketBuffer SendBuffer => _packetBuffers.Value;

        private void BroadcastMessageInternal(byte[] buffer, int offset, int count)
        {
            foreach (var con in _sendPool.Connections)
                con.Value.TrySend(buffer, offset, count);
        }

        public void Dispose()
        {
            _sender.Dispose();
            _sendPool.Dispose();
            _receivePool.Dispose();
        }

        private sealed class ConnectionPool<T> : IDisposable
        {
            private readonly string _pipeName;
            private readonly PipeDirection _pipeDirection;
            private readonly ConcurrentDictionary<NamedPipeServerStream, T> _connections;
            private readonly Func<ConnectionPool<T>, NamedPipeServerStream, T> _stateFactory;
            private readonly Thread _waiting;

            private volatile uint _waitingForClientThread;
            private volatile NamedPipeServerStream _waitingForClient;
            private volatile bool _disposed;

            public ConnectionPool(string pipeName, PipeDirection pipeDirection, Func<ConnectionPool<T>, NamedPipeServerStream, T> stateFactory)
            {
                _pipeName = pipeName;
                _pipeDirection = pipeDirection;
                _stateFactory = stateFactory;
                _connections = new ConcurrentDictionary<NamedPipeServerStream, T>();

                _waiting = new Thread(PipeUtils.WrapPipeThread(WaitForConnection)) {Name = "server-await-connection", IsBackground = true};
                _waiting.Start();
            }

            public IEnumerable<KeyValuePair<NamedPipeServerStream, T>> Connections => _connections;

            private void WaitForConnection()
            {
                while (!_disposed)
                {
                    _waitingForClient = new NamedPipeServerStream(_pipeName,
                        _pipeDirection,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.None,
                        ChannelDesc.InBufferSize,
                        ChannelDesc.OutBufferSize);
                    _waitingForClientThread = PipeUtils.CurrentNativeThreadId;
                    _waitingForClient.WaitForConnection();

                    var stream = Interlocked.Exchange(ref _waitingForClient, null);
                    _connections[stream] = _stateFactory(this, stream);
                }
            }

            internal void DisposeState(NamedPipeServerStream stream)
            {
                if (!_connections.TryRemove(stream, out var val)) return;
                stream.Dispose();
                (val as IDisposable)?.Dispose();
            }

            public void Dispose()
            {
                _disposed = true;
                var stream = Interlocked.Exchange(ref _waitingForClient, null);
                stream?.Dispose();

                foreach (var con in _connections.Keys)
                    DisposeState(con);

                if (!_waiting.Join(500))
                {
                    PipeUtils.CancelSynchronousIo(_waitingForClientThread);
                    _waiting.Abort();
                }
            }
        }

        private sealed class SendConnection
        {
            private readonly ConnectionPool<SendConnection> _owner;
            private readonly NamedPipeServerStream _stream;

            public SendConnection(ConnectionPool<SendConnection> owner, NamedPipeServerStream stream)
            {
                _owner = owner;
                _stream = stream;
            }

            public void TrySend(byte[] buffer, int offset, int count)
            {
                try
                {
                    _stream.Write(buffer, offset, count);
                }
                catch (Exception ex) when (PipeUtils.IsPipeClosedError(ex))
                {
                    _owner.DisposeState(_stream);
                }
            }
        }

        private sealed class ReceiveConnection : IDisposable
        {
            private readonly ConnectionPool<ReceiveConnection> _owner;
            private readonly NamedPipeServerStream _stream;
            private readonly ReceiveChannel _channel;
            private volatile uint _threadId;
            private readonly Thread _thread;

            public ReceiveConnection(PacketDistributor distributor, ConnectionPool<ReceiveConnection> owner, NamedPipeServerStream stream)
            {
                _owner = owner;
                _stream = stream;
                _channel = new ReceiveChannel(distributor, _stream);
                _thread = new Thread(PipeUtils.WrapPipeThread(Run)) {Name = "server-client-recieve", IsBackground = true};
                _thread.Start();
            }

            private void Run()
            {
                try
                {
                    _threadId = PipeUtils.CurrentNativeThreadId;
                    _channel.Poll();
                }
                finally
                {
                    _owner.DisposeState(_stream);
                }
            }

            public void Dispose()
            {
                if (!_thread.Join(500))
                {
                    PipeUtils.CancelSynchronousIo(_threadId);
                    _thread.Abort();
                }
            }
        }
    }
}