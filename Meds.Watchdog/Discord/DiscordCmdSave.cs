using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DSharpPlus.CommandsNext;
using DSharpPlus.CommandsNext.Attributes;
using DSharpPlus.Entities;
using Meds.Shared;
using Meds.Shared.Data;
using Meds.Watchdog.Utils;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace Meds.Watchdog.Discord
{
    public class DiscordCmdSave : BaseCommandModule
    {
        private readonly HealthTracker _healthTracker;
        private readonly ISubscriber<SaveResponse> _saveResponse;
        private readonly IPublisher<SaveRequest> _saveRequest;
        private readonly Configuration _configuration;
        private readonly ILogger<DiscordCmdSave> _log;

        public DiscordCmdSave(HealthTracker healthTracker, ISubscriber<SaveResponse> saveResponse, IPublisher<SaveRequest> saveRequest,
            Configuration configuration, ILogger<DiscordCmdSave> log)
        {
            _healthTracker = healthTracker;
            _saveResponse = saveResponse;
            _saveRequest = saveRequest;
            _configuration = configuration;
            _log = log;
        }

        [Command("save")]
        [Description("Saves the server's world file")]
        [RequirePermission(DiscordPermission.SavesCreate)]
        public async Task SaveCommand(CommandContext context,
            [Description("Named backup to take, optional.")]
            string name = null)
        {
            if (!_healthTracker.Readiness.State)
            {
                await context.RespondAsync("Cannot save when server is not running");
                return;
            }

            string backupName = null;
            string backupPath = null;
            if (!string.IsNullOrEmpty(name))
            {
                backupName = $"{DateTime.Now:yyyy_MM_dd_HH_mm_ss_fff}_{PathUtils.CleanFileName(name)}.zip";
                backupPath = Path.Combine(_configuration.NamedBackupsDirectory, backupName);
            }

            var message = await context.RespondAsync(backupName != null ? $"Saving and backing up to {backupName}..." : "Saving...");
            var start = DateTime.UtcNow;
            var result = await _saveResponse.AwaitResponse(
                response => response.Result,
                TimeSpan.FromMinutes(15),
                () =>
                {
                    using var t = _saveRequest.Publish();
                    t.Send(SaveRequest.CreateSaveRequest(t.Builder, Stopwatch.GetTimestamp(), t.Builder.CreateString(backupPath)));
                });
            var duration = DateTime.UtcNow - start;
            switch (result)
            {
                case SaveResult.Success:
                    if (backupName == null)
                    {
                        await message.ModifyAsync($"Saved in {duration.FormatHumanDuration()}");
                        return;
                    }

                    var backupSize = new System.IO.FileInfo(backupPath).Length;
                    await message.ModifyAsync(
                        $"Saved and backed up to {backupName} ({DiscordUtils.FormatHumanBytes(backupSize)}) in {duration.FormatHumanDuration()}");
                    break;
                case SaveResult.Failed:
                    await message.ModifyAsync("Failed to save");
                    break;
                case SaveResult.TimedOut:
                    await message.ModifyAsync("Timed out when saving");
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
    }
}