using Meds.Wrapper.Utils;

namespace Meds.Wrapper.Audit
{
    public struct AuditPayload
    {
        public PlayerPayload Player;
        public PositionPayload Position;
    }
}