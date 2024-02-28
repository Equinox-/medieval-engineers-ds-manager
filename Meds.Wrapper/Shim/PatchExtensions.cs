using System;
using System.Reflection;

namespace Meds.Wrapper.Shim
{
    public static class PatchExtensions
    {
        public static bool TryFindArg(this MethodBase method, Type type, out int index)
        {
            if (type.IsAssignableFrom(method.DeclaringType))
            {
                index = 0;
                return true;
            }

            var args = method.GetParameters();
            for (var i = 0; i < args.Length; i++)
            {
                if (type.IsAssignableFrom(args[i].ParameterType))
                {
                    index = i + (method.IsStatic ? 0 : 1);
                    return true;
                }
            }

            index = default;
            return false;
        }
    }
}