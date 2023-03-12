using Meds.Wrapper.Utils;

namespace Meds.Wrapper.Audit
{
    public struct AuditPayload
    {
        public ulong SteamId;
        public PositionPayload? Position;
    }
}