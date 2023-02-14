using System;

namespace Meds.Standalone.Shim
{
    public sealed class AlwaysPatch : Attribute
    {
        public bool Late { get; set; }
    }
}