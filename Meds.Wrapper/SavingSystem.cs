using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using Meds.Shared;
using Meds.Shared.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sandbox.Game.World;
using ZLogger;
using VRage.Collections;
using MySession = Sandbox.Game.World.MySession;

namespace Meds.Wrapper
{
    public class SavingSystem : IHostedService
    {
        private readonly ILogger<SavingSystem> _log;
        private readonly ISubscriber<SaveRequest> _subscriber;
        private readonly IPublisher<SaveResponse> _publisher;

        public SavingSystem(ISubscriber<SaveRequest> subscriber, IPublisher<SaveResponse> publisher, ILogger<SavingSystem> log)
        {
            _subscriber = subscriber;
            _publisher = publisher;
            _log = log;
        }

        private async Task HandleRequest(long id, string backupPath)
        {
            void Respond(SaveResult result)
            {
                using var token = _publisher.Publish();
                token.Send(SaveResponse.CreateSaveResponse(token.Builder, id, result));
            }

            while (MyAsyncSaving.InProgress)
                await Task.Delay(TimeSpan.FromSeconds(1));
            var completion = new TaskCompletionSource<bool>();
            MyAsyncSaving.Start(completion.SetResult);
            await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromMinutes(10)));
            if (!completion.Task.IsCompleted)
            {
                Respond(SaveResult.TimedOut);
                return;
            }

            var saveResult = completion.Task.Result;
            if (!saveResult)
            {
                Respond(SaveResult.Failed);
                return;
            }

            if (string.IsNullOrEmpty(backupPath))
            {
                Respond(SaveResult.Success);
                return;
            }

            Respond(MakeBackup(backupPath) ? SaveResult.Success : SaveResult.Failed);
        }

        private void HandleRequestBackground(SaveRequest obj)
        {
            var task = HandleRequest(obj.Id, obj.BackupPath);
            task.ContinueWith(result =>
            {
                if (result.IsFaulted)
                    _log.ZLogWarning(result.Exception, "Failed to handle save message");
            });
        }

        private IDisposable _requestSubscription;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _requestSubscription = _subscriber.Subscribe(HandleRequestBackground);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _requestSubscription.Dispose();
            return Task.CompletedTask;
        }

        private static readonly HashSetReader<string> IgnoredPaths =
            new HashSet<string>(new MySandboxSessionDelta().IgnoredPaths, StringComparer.OrdinalIgnoreCase);

        private bool MakeBackup(string target)
        {
            var dir = new DirectoryInfo(MySession.Static.CurrentPath);
            _log.ZLogInformation("Backing up session '{0}' to {1}", dir.FullName, target);
            try
            {
                var targetDir = Path.GetDirectoryName(target);
                Directory.CreateDirectory(targetDir);
                using var archive = ZipFile.Open(target, ZipArchiveMode.Create);
                var trimmedDir = dir.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                foreach (var file in RecurseDirectory(dir.FullName, IgnoredPaths))
                {
                    var sourceFilePath = Path.Combine(file.Directory.FullName, file.Name);
                    var destination = file.FullName.Substring(trimmedDir.Length + 1);
                    archive.CreateEntryFromFile(sourceFilePath, destination, CompressionLevel.Optimal);
                }

                return true;
            }
            catch (Exception err)
            {
                _log.ZLogError(err, "There were errors while backing up the save. Deleting corrupted backup data");
                try
                {
                    File.Delete(target);
                }
                catch
                {
                    _log.ZLogError("Can't even delete corrupted backups, what is this world coming to?!");
                }

                return false;
            }
        }

        private static IEnumerable<FileInfo> RecurseDirectory(string path, HashSetReader<string> blackList, List<FileInfo> currentData = null)
        {
            currentData ??= new List<FileInfo>();

            var directory = new DirectoryInfo(path);
            foreach (var file in directory.GetFiles())
            {
                if (CheckBlackList(file.Name, blackList))
                    continue;
                currentData.Add(file);
            }

            foreach (var d in directory.GetDirectories())
            {
                if (CheckBlackList(d.Name, blackList))
                    continue;
                RecurseDirectory(d.FullName, blackList, currentData);
            }

            return currentData;
        }

        private static bool CheckBlackList(string path, HashSetReader<string> blackList) => blackList.Contains(path);
    }
}