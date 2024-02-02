using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Meds.Shared;
using Microsoft.Extensions.Logging;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;
using SIPSorcery.Net;
using ZLogger;
using Base64 = Org.BouncyCastle.Utilities.Encoders.Base64;

namespace Meds.Watchdog.Utils
{
    public sealed class RtcFileSharing
    {
        // This is built to use SendFiles.dev as a backend.
        private const string FrontDoorHost = "sendfiles.dev";
        private const string TransfersHost = "transfers." + FrontDoorHost;
        private const string CoordHost = "coord." + FrontDoorHost;
        public static readonly string TransferPrefix = $"https://{FrontDoorHost}/receive/";

        private static readonly TimeSpan DefaultPeerTimeout = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan DefaultTransferTimeout = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan CloseTimeout = TimeSpan.FromMinutes(1);

        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() }
        };

        private readonly ILogger _logger;
        private readonly Refreshable<byte[]> _password;

        public RtcFileSharing(ILogger<RtcFileSharing> logger, Refreshable<Configuration> config) : this(logger, config.Map(x => x.Discord?.SaveSharingPassword))
        {
        }

        internal RtcFileSharing(
            ILogger logger,
            Refreshable<string> password)
        {
            _logger = logger;
            _password = password.Map(pwd => string.IsNullOrEmpty(pwd) ? null : Encoding.UTF8.GetBytes(pwd));
        }

        public bool Enabled => _password.Current != null;

        private static HttpClient CreateClient() => new HttpClient
        {
            BaseAddress = new Uri("https://" + TransfersHost),
            DefaultRequestHeaders =
            {
                Accept = { new MediaTypeWithQualityHeaderValue("application/json") }
            }
        };

        public delegate ValueTask DelOffered(string uri, long fileSize);

        public async Task Offer(
            string path,
            DelOffered offered = null,
            DelOnProgress onProgress = null)
        {
            var fileName = Path.GetFileName(path);
            var length = new FileInfo(path).Length;
            var key = GenerateKey();

            using var client = CreateClient();
            var response = await client.PostAsync("", CreateJsonContent(new TransferRequest
            {
                EncryptedLength = length + MacSizeBits / 8,
                FileName = fileName,
                PrivateKey = Base64.ToBase64String(key),
                ValidUntil = (DateTime.UtcNow + TimeSpan.FromHours(1)).ToString("O")
            })).Deserialize<TransferResponse>();
            _logger.ZLogInformation("Offering file {0} with length {1} MiB as {2}", fileName, length / 1024.0 / 1024.0, response.Id);

            var offerCompletionSource = new TaskCompletionSource<RtcSender>();
            using var offer = new SimpleWebsocket(_logger, $"wss://{CoordHost}/?role=offerer&transfer_id={response.Id}", HandleOffer);
            await offer.Connect();
            await (offered?.Invoke($"{TransferPrefix}{response.Id}", length) ?? default);

            var offerCompletion = offerCompletionSource.Task;
            await Task.WhenAny(Task.Delay(DefaultPeerTimeout), offerCompletion);
            if (!offerCompletion.IsCompleted)
                throw new RtcTimedOutException();
            using var sender = await offerCompletion;

            var senderCompletion = sender.Completion;
            await Task.WhenAny(Task.Delay(DefaultTransferTimeout), senderCompletion);
            if (!senderCompletion.IsCompleted || !senderCompletion.Result)
                throw new RtcTimedOutException();
            return;

            async ValueTask HandleOffer(Stream arg)
            {
                var msg = await JsonSerializer.DeserializeAsync<WsMessage>(arg, JsonOpts);
                if (IsNewRecipient(msg.Body))
                {
                    var sender = new RtcSender(
                        new RtcState(_logger, response.Id, onProgress),
                        msg.SenderAddress,
                        path, () => InitializeCipher(key, true));
                    await sender.Connect();
                    if (!offerCompletionSource.TrySetResult(sender))
                        sender.Dispose();
                    return;
                }

                _logger.ZLogWarning("Unknown offer message {0}", msg.Body);
            }
        }

        public delegate ValueTask DelDownloadInfo(string fileName, long fileSize);

        public delegate ValueTask DelOnProgress(string fileName, long downloadedBytes, long totalBytes);

        public async Task<string> Download(
            string directory,
            string transferId,
            DelDownloadInfo onInfo = null,
            DelOnProgress onProgress = null)
        {
            using var client = CreateClient();
            var response = await client.GetAsync($"/?id={transferId}").Deserialize<TransferResponse>();
            var key = Base64.Decode(response.PrivateKey);
            var outputFile = Path.Combine(directory, PathUtils.CleanFileName(response.FileName));
            if (File.Exists(outputFile))
                throw new RtcFileAlreadyExistsException();
            var tempFile = Path.GetTempFileName();
            try
            {
                await (onInfo?.Invoke(Path.GetFileName(outputFile), response.EncryptedLength) ?? default);
                _logger.ZLogInformation("Downloading file {0} with length {1} MiB as {2}", response.FileName, response.EncryptedLength / 1024.0 / 1024.0,
                    response.Id);

                using (var receiver = new RtcReceiver(
                           new RtcState(_logger, transferId, onProgress),
                           response.FileName,
                           tempFile, response.EncryptedLength,
                           InitializeCipher(key, false)))
                {
                    await receiver.Connect();
                    var completion = receiver.Completion;
                    await Task.WhenAny(Task.Delay(DefaultTransferTimeout), completion);
                    if (!completion.IsCompleted || !completion.Result)
                        throw new RtcTimedOutException();
                    if (File.Exists(outputFile))
                        throw new RtcFileAlreadyExistsException();
                }

                File.Move(tempFile, outputFile);
                return outputFile;
            }
            catch (InvalidCipherTextException)
            {
                throw new RtcInvalidPasswordException();
            }
            finally
            {
                File.Delete(tempFile);
            }
        }

        #region REST Api

        private class TransferRequest
        {
            [JsonPropertyName("contentLengthBytes")]
            public long EncryptedLength { get; set; }

            [JsonPropertyName("fileName")]
            public string FileName { get; set; }

            [JsonPropertyName("privateKey")]
            public string PrivateKey { get; set; }

            [JsonPropertyName("validUntil")]
            public string ValidUntil { get; set; }
        }

        // ReSharper disable once ClassNeverInstantiated.Local
        private class TransferResponse : TransferRequest
        {
            [JsonPropertyName("id")]
            public string Id { get; set; }
        }

        #endregion

        #region WS Api

        private class WsMessage
        {
            [JsonPropertyName("sender")]
            public string SenderAddress { get; set; }

            [JsonPropertyName("body")]
            public string Body { get; set; }
        }

        private class WsSendMessage
        {
            [JsonPropertyName("action")]
            public string Action
            {
                get => "SEND_MESSAGE";
                set { }
            }

            [JsonPropertyName("recipient")]
            public string Recipient { get; set; }

            [JsonPropertyName("body")]
            public string Body { get; set; }
        }

        private class WsAddIceCandidate
        {
            public static readonly string TypeConstant = "NEW_ICE_CANDIDATE";

            [JsonPropertyName("type")]
            public string Type
            {
                get => TypeConstant;
                set { }
            }

            [JsonPropertyName("candidate")]
            public RTCIceCandidateInit Candidate { get; set; }
        }

        private class WsNewOffer
        {
            public static readonly string TypeConstant = "NEW_OFFER";

            [JsonPropertyName("type")]
            public string Type
            {
                get => TypeConstant;
                set { }
            }

            [JsonPropertyName("offer")]
            public RTCSessionDescriptionInit Offer { get; set; }
        }

        private class WsNewAnswer
        {
            public static readonly string TypeConstant = "NEW_ANSWER";

            [JsonPropertyName("type")]
            public string Type
            {
                get => TypeConstant;
                set { }
            }

            [JsonPropertyName("answer")]
            public RTCSessionDescriptionInit Answer { get; set; }
        }

        private static Predicate<string> MakeTypeChecker(string type)
        {
            var encoded = $"\"type\":\"{type}\"";
            return json => json != null && json.Contains(encoded);
        }

        private static readonly Predicate<string> IsNewRecipient = MakeTypeChecker("NEW_RECIPIENT");
        private static readonly Predicate<string> IsNewIceCandidate = MakeTypeChecker(WsAddIceCandidate.TypeConstant);
        private static readonly Predicate<string> IsNewOffer = MakeTypeChecker(WsNewOffer.TypeConstant);
        private static readonly Predicate<string> IsNewAnswer = MakeTypeChecker(WsNewAnswer.TypeConstant);

        #endregion

        #region Crypto

        private static byte[] GenerateKey() => SecureRandom.GetNextBytes(new SecureRandom(), 256 / 8);
        private const int MacSizeBits = 128;

        private GcmBlockCipher InitializeCipher(byte[] key, bool forEncryption)
        {
            var parameters = new AeadParameters(new KeyParameter(key), MacSizeBits, _password.Current);
            var cipher = new GcmBlockCipher(new AesEngine());
            cipher.Init(forEncryption, parameters);
            return cipher;
        }

        private static HttpContent CreateJsonContent<T>(T data) =>
            new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(data))
            {
                Headers = { ContentType = new MediaTypeHeaderValue("application/json") }
            };

        #endregion

        #region RTC

        private sealed class RtcState
        {
            public readonly ILogger Logger;
            public readonly string TransferId;
            public readonly DelOnProgress OnProgress;

            public RtcState(ILogger logger, string transferId, DelOnProgress onProgress)
            {
                Logger = logger;
                TransferId = transferId;
                OnProgress = onProgress;
            }
        }

        private abstract class RtcClient : IDisposable
        {
            private readonly SimpleWebsocket _ws;
            protected readonly RTCPeerConnection Rtc;
            protected string Recipient { get; set; }
            protected readonly RtcState State;

            public ILogger Logger => State.Logger;

            protected RtcClient(RtcState state, bool isSender)
            {
                State = state;
                _ws = new SimpleWebsocket(
                    state.Logger,
                    $"wss://{CoordHost}/?role={(isSender ? "sender" : "receiver")}&transfer_id={state.TransferId}",
                    HandleMessageInternal);
                Rtc = new RTCPeerConnection(new RTCConfiguration
                {
                    iceServers = new List<RTCIceServer>
                    {
                        new RTCIceServer
                        {
                            urls = "stun:stun.l.google.com:19302"
                        }
                    }
                });
            }

            public async ValueTask Connect()
            {
                await _ws.Connect();
                await ConnectInternal();
                Rtc.onicecandidate += candidate =>
                {
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                    SendMessage(new WsAddIceCandidate
                    {
                        Candidate = new RTCIceCandidateInit
                        {
                            sdpMid = candidate.sdpMid ?? candidate.sdpMLineIndex.ToString(),
                            sdpMLineIndex = candidate.sdpMLineIndex,
                            usernameFragment = candidate.usernameFragment,
                            candidate = ("candidate:" + candidate)
                        }
                    });
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                };
            }

            protected abstract ValueTask ConnectInternal();

            public virtual void Dispose()
            {
                Rtc?.Dispose();
                _ws?.Dispose();
            }

            private async ValueTask HandleMessageInternal(Stream arg)
            {
                var msg = await JsonSerializer.DeserializeAsync<WsMessage>(arg, JsonOpts);
                if (IsNewIceCandidate(msg.Body))
                {
                    var iceCandidate = JsonSerializer.Deserialize<WsAddIceCandidate>(msg.Body, JsonOpts);
                    Rtc.addIceCandidate(iceCandidate.Candidate);
                    return;
                }

                await HandleMessage(msg);
            }

            protected ValueTask SendMessage<T>(T msg)
            {
                if (Recipient == null)
                    return default;
                return _ws.SendJson(new WsSendMessage
                {
                    Recipient = Recipient,
                    Body = JsonSerializer.Serialize(msg, JsonOpts)
                }, JsonOpts);
            }

            protected abstract ValueTask HandleMessage(WsMessage msg);
        }

        private const int ChunkSize = 16384;

        private class RtcSender : RtcClient
        {
            private readonly string _file;
            private readonly Func<GcmBlockCipher> _cipher;
            private readonly TaskCompletionSource<bool> _completion;
            public Task<bool> Completion => _completion.Task;

            public RtcSender(
                RtcState state,
                string recipient,
                string file,
                Func<GcmBlockCipher> cipher) : base(state, true)
            {
                _file = file;
                _cipher = cipher;
                _completion = new TaskCompletionSource<bool>();
                Recipient = recipient;
            }

            protected override async ValueTask ConnectInternal()
            {
                var channel = await Rtc.createDataChannel("sendDataChannel");
                channel.binaryType = "arraybuffer";
                channel.onopen += () =>
                {
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                    SendTask(channel);
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                };
                // Send the offer.
                var offer = Rtc.createOffer();
                await Rtc.setLocalDescription(offer);
                await SendMessage(new WsNewOffer { Offer = offer });
            }

            private async Task SendTask(RTCDataChannel channel)
            {
                const ulong waitAtBufferSize = 1024 * 1024;

                var fileName = Path.GetFileName(_file);
                try
                {
                    Logger.ZLogInformation("Sending file {0} over RTC channel", fileName);
                    var cipher = _cipher();
                    using var reader = new FileStream(_file, FileMode.Open, FileAccess.Read);

                    var rawBlock = new byte[ChunkSize];
                    var cryptBlock = new byte[cipher.GetOutputSize(ChunkSize)];
                    var totalSent = 0L;
                    while (channel.IsOpened)
                    {
                        var rawBytes = await reader.ReadAsync(rawBlock, 0, ChunkSize);
                        if (rawBytes == 0)
                            break;

                        while (channel.bufferedAmount > waitAtBufferSize)
                            await Task.Delay(TimeSpan.FromMilliseconds(100));

                        var cryptBytes = cipher.ProcessBytes(rawBlock, 0, rawBytes, cryptBlock, 0);
                        channel.Send(cryptBlock, 0, cryptBytes);
                        totalSent += cryptBytes;
                        await (State.OnProgress?.Invoke(fileName, totalSent, reader.Length) ?? default);
                    }

                    var finalBytes = cipher.DoFinal(cryptBlock, 0);
                    channel.Send(cryptBlock, 0, finalBytes);

                    // Wait for buffer to drain.
                    while (channel.bufferedAmount > 0)
                        await Task.Delay(TimeSpan.FromSeconds(1));

                    // Wait for the channel to close, or the close timeout.
                    var start = DateTime.UtcNow;
                    while (channel.IsOpened || (DateTime.UtcNow - start) > CloseTimeout)
                        await Task.Delay(TimeSpan.FromSeconds(1));
                    _completion.SetResult(true);
                }
                catch (Exception err)
                {
                    Logger.ZLogWarning(err, "Sending file {0} failed", fileName);
                }
                finally
                {
                    channel.close();
                }
            }

            protected override ValueTask HandleMessage(WsMessage arg)
            {
                if (IsNewAnswer(arg.Body))
                {
                    var answer = JsonSerializer.Deserialize<WsNewAnswer>(arg.Body, JsonOpts);
                    var result = Rtc.setRemoteDescription(answer.Answer);
                    if (result != SetDescriptionResultEnum.OK)
                        Logger.ZLogWarning("Failed to set remote RTC endpoint: {0}", result);
                    return default;
                }

                Logger.ZLogWarning("Failed to handle sender message: {0}", arg.Body);

                // Completed.
                return default;
            }
        }

        private class RtcReceiver : RtcClient
        {
            private readonly long _encryptedTotal;
            private readonly GcmBlockCipher _cipher;
            private readonly TaskCompletionSource<bool> _completion;
            private readonly string _name;
            private FileStream _writer;
            private byte[] _buffer;

            private long _encryptedRead;

            public Task<bool> Completion => _completion.Task;

            public RtcReceiver(
                RtcState state,
                string name,
                string tempFile,
                long encryptedLength,
                GcmBlockCipher cipher) : base(state, false)
            {
                _name = name;
                _writer = new FileStream(tempFile, FileMode.Create, FileAccess.Write);
                _encryptedTotal = encryptedLength;
                _cipher = cipher;
                _buffer = new byte[ChunkSize];
                _completion = new TaskCompletionSource<bool>();
            }

            protected override ValueTask ConnectInternal()
            {
                Rtc.ondatachannel += channel =>
                {
                    Logger.ZLogInformation("Receiving file {0} over RTC channel", _name);
                    channel.binaryType = "arraybuffer";
                    channel.onmessage += OnRtcMessage;
                };
                return default;
            }

            private void OnRtcMessage(
                RTCDataChannel dc,
                DataChannelPayloadProtocols protocol,
                byte[] data)
            {
                try
                {
                    var produced = _cipher.GetUpdateOutputSize(data.Length);
                    if (produced >= _buffer.Length)
                        Array.Resize(ref _buffer, produced);
                    var decryptedBytes = _cipher.ProcessBytes(data, 0, data.Length, _buffer, 0);
                    _writer.Write(_buffer, 0, decryptedBytes);

                    _encryptedRead += data.Length;
                    var task = State.OnProgress?.Invoke(_name, _encryptedRead, _encryptedTotal) ?? default;
                    if (!task.IsCompleted)
                        task.AsTask().Wait();
                    if (_encryptedRead < _encryptedTotal)
                        return;

                    decryptedBytes = _cipher.DoFinal(_buffer, 0);
                    _writer.Write(_buffer, 0, decryptedBytes);
                    _writer.Dispose();
                    _writer = null;
                    _completion.SetResult(true);
                }
                catch (Exception err)
                {
                    _writer?.Dispose();
                    _writer = null;
                    _completion.SetException(err);
                }
            }

            protected override async ValueTask HandleMessage(WsMessage arg)
            {
                if (IsNewOffer(arg.Body))
                {
                    var offset = JsonSerializer.Deserialize<WsNewOffer>(arg.Body, JsonOpts);
                    Recipient = arg.SenderAddress;
                    var result = Rtc.setRemoteDescription(offset.Offer);
                    if (result != SetDescriptionResultEnum.OK)
                    {
                        Logger.ZLogWarning("Failed to set remote RTC endpoint: {0}", result);
                        return;
                    }

                    var answer = Rtc.createAnswer();
                    await Rtc.setLocalDescription(answer);
                    await SendMessage(new WsNewAnswer { Answer = answer });
                    return;
                }

                Logger.ZLogWarning("Failed to handle receiver message: {0}", arg.Body);
            }

            public override void Dispose()
            {
                _writer?.Dispose();
                base.Dispose();
            }
        }

        #endregion
    }

    public class RtcFileAlreadyExistsException : Exception
    {
    }

    public class RtcTimedOutException : Exception
    {
    }

    public class RtcInvalidPasswordException : Exception
    {
    }

    internal static class RtcExtensions
    {
        public static async ValueTask<T> Deserialize<T>(this Task<HttpResponseMessage> task) where T : class
        {
            using var response = await task.ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var content = response.Content;
            return await JsonSerializer.DeserializeAsync<T>(await content.ReadAsStreamAsync());
        }

        public static void Send(this RTCDataChannel channel, byte[] data, int offset, int count)
        {
            var copy = new byte[count];
            Array.Copy(data, offset, copy, 0, count);
            channel.send(copy);
        }
    }
}