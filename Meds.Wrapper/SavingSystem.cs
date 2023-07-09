using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Meds.Shared;
using Meds.Shared.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sandbox;
using Sandbox.Game.World;
using VRage.Collections;
using VRage.ObjectBuilders.Scene;
using VRage.ParallelWorkers;
using VRage.Scene;
using ZLogger;

namespace Meds.Wrapper
{
    public sealed class SavingSystem : IHostedService
    {
        private readonly ILogger<SavingSystem> _log;
        private readonly ISubscriber<SaveRequest> _saveSubscriber;
        private readonly IPublisher<SaveResponse> _savePublisher;
        private readonly ISubscriber<RestoreSceneRequest> _restoreSubsetSubscriber;
        private readonly IPublisher<RestoreSceneResponse> _restoreSubsetPublisher;

        public SavingSystem(
            ISubscriber<SaveRequest> saveSubscriber, IPublisher<SaveResponse> savePublisher,
            ISubscriber<RestoreSceneRequest> restoreSubsetSubscriber, IPublisher<RestoreSceneResponse> restoreSubsetPublisher,
            ILogger<SavingSystem> log)
        {
            _saveSubscriber = saveSubscriber;
            _savePublisher = savePublisher;
            _restoreSubsetSubscriber = restoreSubsetSubscriber;
            _restoreSubsetPublisher = restoreSubsetPublisher;
            _log = log;
        }

        private async Task HandleSave(long id, string backupPath)
        {
            void Respond(SaveResult result)
            {
                using var token = _savePublisher.Publish();
                token.Send(SaveResponse.CreateSaveResponse(token.Builder, id, result));
            }

            void HandleStartFailed(Exception err)
            {
                _log.ZLogError("Failed to save", err);
                Respond(SaveResult.Failed);
            }

            void HandleSaveCompleted(bool saveResult)
            {
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

            var timeoutAt = DateTime.UtcNow + TimeSpan.FromMinutes(10);

            bool IsTimedOut()
            {
                if (DateTime.UtcNow < timeoutAt)
                    return false;
                Respond(SaveResult.TimedOut);
                return true;
            }

            bool TryStart()
            {
                try
                {
                    if (MyAsyncSaving.InProgress)
                        return false;
                    MyAsyncSaving.Start(HandleSaveCompleted);
                    return true;
                }
                catch (Exception err)
                {
                    HandleStartFailed(err);
                    return true;
                }
            }

            while (true)
            {
                while (MyAsyncSaving.InProgress)
                {
                    if (IsTimedOut())
                        return;
                    await Task.Delay(TimeSpan.FromSeconds(1));
                }

                if (IsTimedOut())
                    return;

                var started = new TaskCompletionSource<bool>();
                MySandboxGame.Static.Invoke(() => started.SetResult(TryStart()));
                if (await started.Task)
                    break;
            }
        }

        private void HandleSaveBackground(SaveRequest obj)
        {
            var task = HandleSave(obj.Id, obj.BackupPath);
            task.ContinueWith(result =>
            {
                if (result.IsFaulted)
                    _log.ZLogWarning(result.Exception, "Failed to handle save message");
            });
        }

        private IDisposable _saveSubscription;
        private IDisposable _restoreSubsetSubscription;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _saveSubscription = _saveSubscriber.Subscribe(HandleSaveBackground);
            _restoreSubsetSubscription = _restoreSubsetSubscriber.Subscribe(HandleRestoreSceneBackground);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _saveSubscription.Dispose();
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

        private static IEnumerable<FileInfo> RecurseDirectory(string path, HashSetReader<string> blackList,
            List<FileInfo> currentData = null)
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

        private void HandleRestoreSceneBackground(RestoreSceneRequest request)
        {
            Workers.Do(request.SceneFile, sceneFile =>
            {
                try
                {
                    HandleRestoreScene(sceneFile);
                }
                catch (Exception err)
                {
                    _log.ZLogWarning(err, "Failed to restore scene");
                }
            });
        }

        private void HandleRestoreScene(string tempScene)
        {
            MyObjectBuilder_Scene restoredOb;
            using (var stream = File.OpenRead(tempScene))
            {
                restoredOb = ((RestoredScene)RestoredScene.Serializer.Deserialize(stream)).Scene;
            }

            var restored = new MyStagingScene("restored-" + Path.GetFileNameWithoutExtension(tempScene));
            restored.Load(restoredOb, null);
            MySandboxGame.Static.Invoke(() =>
            {
                var target = MySession.Static.Scene;
                var replacedEntities = 0u;
                foreach (var entity in restored.TopLevelEntities)
                    if (target.TryGetEntity(entity.Id, out var targetEntity))
                    {
                        replacedEntities++;
                        target.Destroy(targetEntity);
                    }

                var replacedGroups = 0u;
                foreach (var group in restored.GetAllGroups())
                    if (target.ContainsGroup(group.Id))
                    {
                        replacedGroups++;
                    }

                target.Merge(restored);
                using var token = _restoreSubsetPublisher.Publish();
                token.Send(RestoreSceneResponse.CreateRestoreSceneResponse(token.Builder,
                    (uint)(restoredOb.Entities?.Count ?? 0),
                    replacedEntities,
                    (uint)(restoredOb.Groups?.Count ?? 0),
                    replacedGroups));
            });
        }

        [XmlRoot("RestoreScene")]
        public class RestoredScene
        {
            public static readonly XmlSerializer Serializer = new XmlSerializer(typeof(RestoredScene));

            [XmlElement]
            public MyObjectBuilder_Scene Scene;
        }
    }
}