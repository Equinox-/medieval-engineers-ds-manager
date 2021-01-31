using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Meds.Shared
{
    public sealed class ChannelDesc
    {
        public const int InBufferSize = 1024 * 8;
        public const int OutBufferSize = InBufferSize;

        public string PipeNameToClients { get; }
        public string PipeNameToServer { get; }

        public ChannelDesc(string pipeName)
        {
            PipeNameToClients = pipeName + "-clients";
            PipeNameToServer = pipeName + "-servers";
        }
    }
}