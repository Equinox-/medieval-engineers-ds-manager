using System;
using System.Collections.Generic;
using Google.FlatBuffers;
using Meds.Shared;
using Meds.Shared.Data;
using Microsoft.Extensions.DependencyInjection;
using VRage.Engine;
using VRage.Game;
using VRage.Library.Collections;

namespace Meds.Wrapper
{
    public static class MedsModApi
    {
        public static ModEventBuilder SendModEvent(string channel, IApplicationPackage mod)
        {
            var publisher = Entrypoint.Instance.Services.GetRequiredService<IPublisher<ModEventMessage>>();
            return new ModEventBuilder(publisher.Publish(), channel, mod);
        }
        
        public struct ModEventBuilder : IDisposable
        {
            private readonly StringOffset _channelOffset;
            private readonly IApplicationPackage _source;
            private TypedPublishToken<ModEventMessage> _token;
            private List<Offset<ModEventField>> _embedFields;
            private StringOffset _embedTitle;
            private StringOffset _embedDescription;
            private StringOffset _message;

            public ModEventBuilder(TypedPublishToken<ModEventMessage> token, string channel, IApplicationPackage source)
            {
                _token = token;
                _channelOffset = token.Builder.CreateString(channel);
                _embedFields = PoolManager.Get<List<Offset<ModEventField>>>();
                _source = source;
                _embedTitle = default;
                _embedDescription = default;
                _message = default;
            }

            public void SetMessage(string value)
            {
                _message = _token.Builder.CreateString(value);
            }

            public void SetEmbedTitle(string value)
            {
                _embedTitle = _token.Builder.CreateString(value);
            }

            public void SetEmbedDescription(string value)
            {
                _embedDescription = _token.Builder.CreateString(value);
            }

            public void AddField(string key, string value, bool inline = false)
            {
                var b = _token.Builder;
                _embedFields.Add(ModEventField.CreateModEventField(b,
                    b.CreateString(key),
                    b.CreateString(value),
                    inline));
            }

            public void AddInlineField(string key, string value) => AddField(key, value, true);

            public void Send()
            {
                var b = _token.Builder;
                VectorOffset embedFieldsTable = default;
                if (_embedFields.Count > 0)
                {
                    b.NotNested();
                    b.StartVector(4, _embedFields.Count, 4);
                    for (var index = _embedFields.Count - 1; index >= 0; --index)
                        b.AddOffset(_embedFields[index].Value);
                    embedFieldsTable = b.EndVector();
                }

                Offset<ModEventEmbed> embed = default;
                if (_embedFields.Count > 0 || _embedTitle.Value != 0 || _embedDescription.Value != 0)
                    embed = ModEventEmbed.CreateModEventEmbed(b, _embedTitle, _embedDescription, embedFieldsTable);

                _token.Send(ModEventMessage.CreateModEventMessage(b,
                    b.CreateString(_source?.Name),
                    (_source as MyModContext)?.WorkshopItem?.Id ?? 0,
                    _channelOffset,
                    _message,
                    embed));
            }

            public void Dispose()
            {
                _token.Dispose();
                PoolManager.Return(ref _embedFields);
            }
        }
    }
}