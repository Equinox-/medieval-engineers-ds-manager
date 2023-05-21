using System;

namespace Meds.Shared
{
    [AttributeUsage(AttributeTargets.Assembly)]
    public class VersionInfoAttribute : Attribute
    {
        public DateTime CompiledAt { get; }
        public string GitHash { get; }

        public VersionInfoAttribute(long compiledAtTicks, string gitHash)
        {
            CompiledAt = new DateTime(compiledAtTicks, DateTimeKind.Utc);
            GitHash = gitHash;
        }
    }
}