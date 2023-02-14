using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Google.FlatBuffers;
using Meds.Shared.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Meds.Shared
{
    public interface IPublisher<T> where T : struct, IFlatbufferObject
    {
        TypedPublishToken<T> Publish();
    }

    public interface ISubscriber<out T> where T : struct, IFlatbufferObject
    {
        IDisposable Subscribe(Action<T> subscriber);
    }

    public static class MessagePipeExtensions
    {
        public static async Task<TResult> AwaitResponse<TMsg, TResult>(
            this ISubscriber<TMsg> subscriber,
            Func<TMsg, TResult> converter,
            TimeSpan? timeout = null,
            Action sendRequest = null) where TMsg : struct, IFlatbufferObject
        {
            var result = new TaskCompletionSource<TResult>();
            var actualTimeout = timeout ?? TimeSpan.FromSeconds(2);
            using (subscriber.Subscribe(msg =>
                   {
                       if (result.Task.IsCompleted)
                           return;
                       result.TrySetResult(converter(msg));
                   }))
            {
                sendRequest?.Invoke();
                await Task.WhenAny(result.Task, Task.Delay(actualTimeout));
            }

            if (result.Task.IsCompleted)
                return result.Task.Result;
            throw new TimeoutException($"Request for {typeof(TMsg)} timed out after {actualTimeout}");
        }
    }

    public struct TypedPublishToken<T> : IDisposable where T : struct, IFlatbufferObject
    {
        private readonly MessagePipeFactory.UdpPublishWorker _worker;
        private readonly RefCountedObjectPool<FlatBufferBuilder>.Token _token;
        private bool _sent;
        private readonly Message _type;

        internal TypedPublishToken(Message type, MessagePipeFactory.UdpPublishWorker worker)
        {
            _type = type;
            _worker = worker;
            _token = FlatBufferPool.Instance.Borrow();
            _sent = false;
        }

        public FlatBufferBuilder Builder
        {
            get
            {
                if (_sent) throw new ArgumentException("Already sent");
                return _token.Value;
            }
        }

        public void Send(Offset<T> offset)
        {
            var packet = Packet.CreatePacket(Builder, _type, offset.Value);
            Builder.Finish(packet.Value);
            _sent = true;
            _worker.Send(_token.AddRef());
        }

        public void Dispose()
        {
            _token.Dispose();
        }
    }

    public static class MessagePipeFactory
    {
        private const int SockBufferSize = ushort.MaxValue;
        private const int SockTimeout = 500;

        public static void AddMedsMessagePipe(this IServiceCollection services, ushort subscribeToPort, ushort publishToPort)
        {
            services.AddSingleton(svc => new UdpPublishWorker(
                svc.GetRequiredService<ILogger<UdpPublishWorker>>(), publishToPort));
            services.AddSingleton(svc => new UdpSubscribeWorker(
                svc.GetRequiredService<ILogger<UdpSubscribeWorker>>(), subscribeToPort));
            services.AddHostedAlias<UdpPublishWorker>();
            services.AddHostedAlias<UdpSubscribeWorker>();


            void Register<T>(Message type) where T : struct, IFlatbufferObject
            {
                services.AddSingleton(svc =>
                    new UdpMessageQueue<T>(type,
                        svc.GetRequiredService<UdpPublishWorker>(),
                        svc.GetRequiredService<UdpSubscribeWorker>()));
                services.AddSingletonAlias<ISubscriber<T>, UdpMessageQueue<T>>();
                services.AddSingletonAlias<IPublisher<T>, UdpMessageQueue<T>>();
            }

            Register<ShutdownRequest>(Message.ShutdownRequest);
            Register<HealthState>(Message.HealthState);
            Register<PlayersRequest>(Message.PlayersRequest);
            Register<PlayersResponse>(Message.PlayersResponse);
            Register<PlayerJoinedLeft>(Message.PlayerJoinedLeft);
            Register<ChatMessage>(Message.ChatMessage);
        }

        private sealed class UdpMessageQueue<T> : ISubscriber<T>, IPublisher<T> where T : struct, IFlatbufferObject
        {
            private readonly UdpPublishWorker _publisher;
            private readonly UdpSubscribeWorker _subscriber;
            private readonly Message _type;

            public UdpMessageQueue(Message type, UdpPublishWorker publishWorker, UdpSubscribeWorker subscribeWorker)
            {
                _type = type;
                _publisher = publishWorker;
                _subscriber = subscribeWorker;
            }

            public IDisposable Subscribe(Action<T> subscriber) => new Subscription<T>(_subscriber, _type, subscriber);

            public TypedPublishToken<T> Publish() => new TypedPublishToken<T>(_type, _publisher);
        }

        internal interface ISubscription
        {
            Message Type { get; }
            void Handle(Packet packet);
        }

        internal sealed class Subscription<T> : IDisposable, ISubscription where T : struct, IFlatbufferObject
        {
            public Message Type { get; }
            private readonly Action<T> _handler;
            private readonly UdpSubscribeWorker _worker;

            public Subscription(UdpSubscribeWorker worker, Message type, Action<T> handler)
            {
                Type = type;
                _worker = worker;
                _handler = handler;
                _worker.Register(this);
            }

            public void Dispose()
            {
                _worker.Unregister(this);
            }

            public void Handle(Packet packet)
            {
                Debug.Assert(packet.MessageType == Type);
                var val = packet.Message<T>();
                Debug.Assert(val.HasValue);
                _handler(val.Value);
            }
        }

        internal sealed class UdpPublishWorker : BackgroundService
        {
            private readonly BlockingCollection<RefCountedObjectPool<FlatBufferBuilder>.Token> _messages
                = new BlockingCollection<RefCountedObjectPool<FlatBufferBuilder>.Token>();

            private readonly Socket _sock;
            private readonly EndPoint _endpoint;
            private readonly ILogger<UdpPublishWorker> _log;

            public UdpPublishWorker(ILogger<UdpPublishWorker> logger, ushort port)
            {
                _log = logger;
                _endpoint = new IPEndPoint(IPAddress.Loopback, port);
                _sock = new Socket(IPAddress.Loopback.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                _sock.SendBufferSize = SockBufferSize;
                _sock.SendTimeout = SockTimeout;
            }

            public void Send(RefCountedObjectPool<FlatBufferBuilder>.Token packet) => _messages.Add(packet);

            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                return Task.Run(() => ExecuteSync(stoppingToken), stoppingToken);
            }

            private void ExecuteSync(CancellationToken stoppingToken)
            {
                while (!stoppingToken.IsCancellationRequested || _messages.Count > 0)
                {
                    if (!_messages.TryTake(out var msg, 1000, stoppingToken))
                        continue;
                    using (msg)
                    {
                        var buf = msg.Value.DataBuffer;
                        var packet = Packet.GetRootAsPacket(buf);
                        var seg = buf.ToArraySegment(buf.Position, buf.Length - buf.Position);
                        try
                        {
                            _sock.SendTo(seg.Array!, seg.Offset, seg.Count, SocketFlags.None, _endpoint);
                            _log.LogDebug("Sent {Type} ({Bytes} bytes)", packet.MessageType, seg.Count);
                        }
                        catch (Exception err)
                        {
                            _log.LogWarning(err, "Failed to publish message {Type}", packet.MessageType);
                        }
                    }
                }
            }

            public override void Dispose()
            {
                base.Dispose();
                _sock.Dispose();
            }
        }

        internal sealed class UdpSubscribeWorker : BackgroundService
        {
            private readonly Socket _sock;
            private readonly byte[] _receiveBuffer = new byte[SockBufferSize];
            private readonly List<ISubscription>[] _messageSubscriptions = new List<ISubscription>[256];
            private readonly ILogger<UdpSubscribeWorker> _log;

            public UdpSubscribeWorker(ILogger<UdpSubscribeWorker> logger, ushort port)
            {
                _log = logger;
                _sock = new Socket(IPAddress.Loopback.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                _sock.Bind(new IPEndPoint(IPAddress.Loopback, port));
                _sock.ReceiveBufferSize = SockBufferSize;
                _sock.ReceiveTimeout = SockTimeout;
                for (var i = 0; i < _messageSubscriptions.Length; i++)
                    _messageSubscriptions[i] = new List<ISubscription>();
            }

            internal void Register(ISubscription sub)
            {
                _messageSubscriptions[(int)sub.Type].Add(sub);
            }

            internal void Unregister(ISubscription sub)
            {
                _messageSubscriptions[(int)sub.Type].Remove(sub);
            }

            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                return Task.Run(() => ExecuteSync(stoppingToken), stoppingToken);
            }

            private void ExecuteSync(CancellationToken stoppingToken)
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        var bytes = _sock.Receive(_receiveBuffer, 0, _receiveBuffer.Length, SocketFlags.None);
                        if (bytes <= 0)
                            continue;
                        using (var fb = FlatBufferPool.Instance.Borrow())
                        {
                            var buf = fb.Value.DataBuffer;
                            if (bytes > buf.Length)
                                buf.GrowFront(bytes);
                            var bufferArray = buf.ToArraySegment(0, buf.Length).Array!;
                            Array.Copy(_receiveBuffer, 0, bufferArray, buf.Length - bytes, bytes);
                            buf.Position = buf.Length - bytes;
                            var packet = Packet.GetRootAsPacket(buf);
                            _log.LogDebug("Received {Type} ({Bytes} bytes)", packet.MessageType, bytes);
                            var subscribers = _messageSubscriptions[(int)packet.MessageType];
                            var count = subscribers.Count;
                            for (var i = 0; i < count; i++)
                                subscribers[i].Handle(packet);
                        }
                    }
                    catch (SocketException se) when (se.SocketErrorCode == SocketError.TimedOut)
                    {
                        // ignore timed out, the remote server isn't up.
                    }
                    catch (Exception err)
                    {
                        _log.LogWarning(err, "Failed to read message");
                    }
                }
            }

            public override void Dispose()
            {
                base.Dispose();
                _sock.Dispose();
            }
        }
    }
}