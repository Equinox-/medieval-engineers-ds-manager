namespace Meds.Metrics.Group
{
    public abstract class LeafMetric
    {
        public string Name { get; }
        
        public virtual LeafMetricValue CurrentValue { get; }

        protected LeafMetric(string name)
        {
            Name = name;
        }
    }
}