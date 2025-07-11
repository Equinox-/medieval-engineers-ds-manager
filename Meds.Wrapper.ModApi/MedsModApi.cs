using System;
using VRage.Engine;

// All extern methods are only to generate this skeleton API.
#pragma warning disable CS0626
namespace Meds.Wrapper
{
    public static class MedsModApi
    {
        public static extern ModEventBuilder SendModEvent(string channel, IApplicationPackage mod);

        public struct ModEventBuilder : IDisposable
        {
            /// <summary>
            /// If provided an existing message with the same reuse identifier will be edited instead of sending a new message.
            /// If no message was already sent with the same reuse identifier a new message will be sent.
            /// </summary>
            public extern void SetReuseIdentifier(string value, TimeSpan? ttl = null, uint ttlSeconds = 0);

            /// <summary>
            /// Main text content of the message.
            /// </summary>
            public extern void SetMessage(string value);

            /// <summary>
            /// Title of the embedded data.
            /// </summary>
            public extern void SetEmbedTitle(string value);

            /// <summary>
            /// Long description of the embeded data.
            /// </summary>
            public extern void SetEmbedDescription(string value);

            /// <summary>
            /// Adds a field to the embedded data.
            /// </summary>
            public extern void AddField(string key, string value, bool inline = false);

            public extern void AddInlineField(string key, string value);

            public extern void Send();

            public extern void Dispose();
        }
    }
}