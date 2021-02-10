using Google.FlatBuffers;
using Meds.Shared.Data;

namespace Meds.Wrapper.Metrics
{
    public abstract class LeafMetric
    {
        public string Name { get; }

        protected LeafMetric(string name)
        {
            Name = name;
        }

        public abstract bool WriteTo(FlatBufferBuilder builder, out LeafMetricData type, out int offset);
    }
}