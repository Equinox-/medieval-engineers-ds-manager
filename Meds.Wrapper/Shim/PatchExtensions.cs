using System;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

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

        public static bool LoadsArg(this CodeInstruction isn, int arg)
        {
            var op = isn.opcode;
            if (op == OpCodes.Ldarg || op == OpCodes.Ldarg_S)
            {
                return isn.operand switch
                {
                    int int32 => int32 == arg,
                    short int16 => int16 == arg,
                    byte int8 => int8 == arg,
                    _ => false
                };
            }

            return arg switch
            {
                0 => op == OpCodes.Ldarg_0,
                1 => op == OpCodes.Ldarg_1,
                2 => op == OpCodes.Ldarg_2,
                3 => op == OpCodes.Ldarg_3,
                _ => false
            };
        }

        public static CodeInstruction ChangeInstruction(this CodeInstruction ins, OpCode code, object operand = null)
        {
            ins.opcode = code;
            ins.operand = operand;
            return ins;
        }
    }
}