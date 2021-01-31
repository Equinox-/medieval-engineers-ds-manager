using System;
using System.IO;
using System.Threading;
using Medieval;
using MedievalEngineersDedicated;
using Meds.Shared;
using Meds.Shared.Data;
using Meds.Wrapper.Reporter;
using Meds.Wrapper.Shim;
using Sandbox;
using Sandbox.Engine.Physics;
using Sandbox.Game;
using VRage.Dedicated;
using VRage.Engine;
using VRage.Game;
using VRage.Game.SessionComponents;
using VRage.Logging;
using LogSeverity = VRage.Logging.LogSeverity;

namespace Meds.Wrapper
{
    public class Program : IDisposable
    {
        public static Program Instance { get; private set; }

        public string RuntimeDirectory { get; }
        public PacketDistributor Distributor { get; } = new PacketDistributor();
        public PipeClient Channel { get; }

        public HealthReport HealthReport { get; }

        private Program(string runtimeDirectory, ChannelDesc desc)
        {
            Instance = this;
            RuntimeDirectory = runtimeDirectory;
            Distributor.RegisterPacketHandler(_ => MySandboxGame.ExitThreadSafe(), Message.ShutdownRequest);
            Channel = new PipeClient(desc, Distributor);
            HealthReport = new HealthReport();
        }

        private void Run()
        {
            var realArgs = new[]
            {
                "-noconsole",
                "-ignorelastsession",
                // "--unique-log-names",
                // "true",
                "--data-path",
                RuntimeDirectory,
                "--system",
                typeof(EarlyInjectorSystem).Assembly.FullName + ":" + typeof(EarlyInjectorSystem).FullName,
                "--system",
                typeof(LateInjectorSystem).Assembly.FullName + ":" + typeof(LateInjectorSystem).FullName
            };

            var info = new AppInformation("Medieval Engineers Dedicated", MyMedievalGame.ME_VERSION, "", "", "", MyMedievalGame.VersionString);
            Sandbox.Engine.Platform.Game.IsDedicated = true;
            MySessionComponentExtDebug.ForceDisable = true;
            MyMedievalGame.SetupBasicGameInfo();
            MyMedievalGame.SetupPerGameSettings();
            MyPerGameSettings.SendLogToKeen = false;
            MyPerServerSettings.GameName = MyPerGameSettings.GameName;
            MyPerServerSettings.GameNameSafe = MyPerGameSettings.GameNameSafe;
            MyPerServerSettings.GameDSName = MyPerServerSettings.GameNameSafe + "Dedicated";
            MyPerServerSettings.GameDSDescription = "Your place for medieval engineering, destruction and exploring.";
            MyPerServerSettings.AppId = MyMedievalGame.AppId;
            MyFinalBuildConstants.GAME_VERSION = MyPerGameSettings.BasicGameInfo.GameVersion;
            MyPhysicsSandbox.OutOfMemory += () =>
            {
                // TODO
            };
            new MedievalDedicatedServer(info).Run(realArgs);
        }

        public void Dispose()
        {
            HealthReport?.Dispose();
            Channel?.Dispose();
        }


        public static void Main(string[] args)
        {
            if (args.Length != 2)
                throw new Exception("Wrapper should not be invoked manually.  [dir] [channel]");

            var globalDir = Path.GetFullPath(args[0]);
            Directory.SetCurrentDirectory(Path.Combine(globalDir, "install"));
            using (var pgm = new Program(Path.Combine(globalDir, "runtime"), new ChannelDesc(args[1])))
            {
                pgm.Run();
            }
        }
    }
}