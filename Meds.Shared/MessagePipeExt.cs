using Meds.Shared.Data;

namespace Meds.Shared
{
    public static class MessagePipeExt
    {
        public static void SendGenericMessage(this IPublisher<ChatMessage> chat, string channel, string message, ulong sender = 0)
        {
            using var token = chat.Publish();
            var builder = token.Builder;
            token.Send(ChatMessage.CreateChatMessage(builder,
                ChatChannel.GenericChatChannel,
                GenericChatChannel.CreateGenericChatChannel(builder, builder.CreateString(channel)).Value,
                builder.CreateString(message),
                sender));
        }
    }
}