using System;
using System.Linq;
using System.Reflection;

namespace Meds.Standalone
{
    public static class Entrypoint
    {
        public static void Main(string[] args)
        {
            var allArgs = args.Concat(new[]
            {
                "--system",
                $"{typeof(MedsCoreSystem).Assembly.FullName}:{typeof(MedsCoreSystem).FullName}",
            }).ToArray();
            var type = Type.GetType("MedievalEngineersDedicated.MyProgram, MedievalEngineersDedicated")
                       ?? throw new NullReferenceException("MyProgram is missing");
            var method = type.GetMethod("Main", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                         ?? throw new NullReferenceException("MyProgram#Main is missing");
            method.Invoke(null, new object[] {allArgs});
        }
    }
}