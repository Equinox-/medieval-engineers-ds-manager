using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace Meds.Watchdog.Utils
{
    internal sealed class SimpleWebsocket : IDisposable
    {
        private readonly ClientWebSocket _websocket;
        private Task _websocketTask;
        private readonly string _uri;
        private readonly Func<Stream, ValueTask> _handle;
        private readonly CancellationTokenSource _cts;
        private readonly ILogger _logger;

        public SimpleWebsocket(
            ILogger logger,
            string uri,
            Func<Stream, ValueTask> handle)
        {
            _logger = logger;
            _websocket = new ClientWebSocket();
            _uri = uri;
            _handle = handle;
            _cts = new CancellationTokenSource();
        }

        internal async Task Connect()
        {
            await _websocket.ConnectAsync(new Uri(_uri), _cts.Token);
            _websocketTask = StartReceiving();
        }

        private async Task StartReceiving()
        {
            const int bufferSize = 32 * 1024;
            var emptyBuffers = new Stack<byte[]>();
            var fullBuffers = new List<ArraySegment<byte>>();
            _logger.ZLogInformation("Starting websocket {0}", _uri);
            while (_websocket.State == WebSocketState.Open)
            {
                if (_cts.IsCancellationRequested)
                {
                    await _websocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Terminating", default);
                    break;
                }

                var buffer = emptyBuffers.Count > 0 ? emptyBuffers.Pop() : new byte[bufferSize];
                var msg = await _websocket.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
                if (msg.MessageType == WebSocketMessageType.Close)
                    break;
                if (msg.Count <= 0)
                {
                    emptyBuffers.Push(buffer);
                    continue;
                }

                var segment = new ArraySegment<byte>(buffer, 0, msg.Count);
                fullBuffers.Add(segment);
                if (!msg.EndOfMessage)
                    continue;
                if (msg.MessageType == WebSocketMessageType.Text)
                {
                    try
                    {
                        await _handle(new ConcatStream(fullBuffers));
                    }
                    catch (Exception err)
                    {
                        var firstBuffer = fullBuffers[0];
                        var firstBufferString = Encoding.ASCII.GetString(firstBuffer.Array, firstBuffer.Offset, firstBuffer.Count);
                        _logger.ZLogWarning(err, "Failed to handle websocket message: {0}", firstBufferString);
                    }
                }

                foreach (var buf in fullBuffers)
                    emptyBuffers.Push(buf.Array);
                fullBuffers.Clear();
            }
            _logger.ZLogInformation("Ending websocket {0}", _uri);
        }

        public async ValueTask SendJson<T>(T data, JsonSerializerOptions opts = null)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(data, opts);
            await _websocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts.Token);
        }

        public void Dispose()
        {
            _cts.Cancel();
            _websocket.Dispose();
        }

        private sealed class ConcatStream : Stream
        {
            private readonly List<ArraySegment<byte>> _segments;
            private int _segment;
            private int _segmentOffset;
            private int _absoluteOffset;

            public ConcatStream(List<ArraySegment<byte>> segments)
            {
                _segments = segments;
                foreach (var segment in segments)
                    Length += segment.Count;
            }

            public override void Flush() => throw new NotImplementedException();

            public override long Seek(long offset, SeekOrigin origin) => throw new NotImplementedException();

            public override void SetLength(long value) => throw new NotImplementedException();

            public override int Read(byte[] buffer, int offset, int count)
            {
                var read = 0;
                while (read < count)
                {
                    if (_segment >= _segments.Count)
                        break;
                    var seg = _segments[_segment];
                    var remainingInSegment = seg.Count - _segmentOffset;
                    var copied = Math.Min(remainingInSegment, count - read);
                    Array.Copy(seg.Array, seg.Offset + _segmentOffset, buffer, offset + read, copied);
                    read += copied;
                    _segmentOffset += copied;
                    _absoluteOffset += copied;

                    if (_segmentOffset < seg.Count)
                        continue;
                    _segment++;
                    _segmentOffset = 0;
                }

                return read;
            }

            public override void Write(byte[] buffer, int offset, int count) => throw new NotImplementedException();

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length { get; }

            public override long Position
            {
                get => _absoluteOffset;
                set => throw new NotImplementedException();
            }
        }
    }
}