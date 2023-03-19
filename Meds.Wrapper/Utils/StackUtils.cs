using System;
using System.Diagnostics;
using System.Text;
using Meds.Shared;
using Sandbox;
using Sandbox.Engine.Platform;
using VRage.Components;

namespace Meds.Wrapper.Utils
{
    public static class StackUtils
    {
        public static StackTracePayload CaptureGameLogicStackPayload() => new StackTracePayload { StackTrace = CaptureGameLogicStack() };

        public static string CaptureGameLogicStack()
        {
            var stack = new StackTrace(1);
            var frames = stack.GetFrames();
            if (frames == null)
                return "";
            var sb = new StringBuilder();
            foreach (var frame in frames)
            {
                var method = frame.GetMethod();
                if (method == null)
                    continue;
                var declaringType = method.DeclaringType;
                // Ignore stack trace elements inside the wrapper.
                if (declaringType?.Assembly == typeof(StackUtils).Assembly || declaringType?.Assembly == typeof(MessagePipe).Assembly)
                    continue;
                // Terminate the stack once we get to the update scheduler.
                if (declaringType == typeof(MyUpdateScheduler) &&
                    (method.Name == "RunUpdates" || method.Name == "RunTimedUpdates" || method.Name == "RunFixedUpdates"))
                    break;
                if (declaringType == typeof(MySandboxUpdate) && method.Name == nameof(MySandboxUpdate.RunLoop))
                    break;
                if (declaringType == typeof(MySandboxGame) && method.Name == "Update")
                    break;
                if (declaringType == typeof(Game) && method.Name == "UpdateInternal")
                    break;
                
                if (declaringType != null)
                {
                    sb.Append(declaringType.FullName);
                    sb.Append(".");
                }
                sb.Append(method.Name);
                sb.Append("(");
                var parameters = method.GetParameters();
                for (var i = 0; i < parameters.Length; ++i)
                {
                    if (i > 0)
                        sb.Append(", ");
                    sb.Append(parameters[i].ParameterType?.Name);
                }
                sb.Append(")");
                sb.Append(Environment.NewLine);
            }

            return sb.ToString();
        }

        public struct StackTracePayload
        {
            public string StackTrace;
        }
    }
}