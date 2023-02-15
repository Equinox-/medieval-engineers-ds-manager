using System;

namespace Meds.Wrapper.Shim
{
    public sealed class AlwaysPatch : Attribute
    {
        public bool Late { get; set; }
    }
}