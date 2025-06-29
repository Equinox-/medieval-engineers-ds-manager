using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using DiffPlex.Renderer;
using DSharpPlus.Entities;
using DSharpPlus.SlashCommands;
using Meds.Shared;

namespace Meds.Watchdog.Discord
{
    public class DiscordCmdMisc : DiscordCmdBase
    {
        private const int MaxMessages = 50;
        private readonly Refreshable<DiscordConfig> _cfg;

        private const string MessageSeparator = "<!--New Message-->";
        private const int CharacterLimit = 2000;

        public DiscordCmdMisc(DiscordService discord, Refreshable<Configuration> cfg) : base(discord)
        {
            _cfg = cfg.Map(x => x.Discord);
        }

        private async Task<(DiscordChannel Channel, IReadOnlyList<DiscordMessage> Messages, string Rules)> ResolveRulesChannel(InteractionContext context)
        {
            var channelId = _cfg.Current.RulesChannel;
            if (channelId == null)
            {
                await context.CreateResponseAsync("No rules channel configured");
                return default;
            }

            var channel = await context.Client.GetChannelAsync(channelId.Value);
            if (channel == null || channel.Guild != context.Guild)
            {
                await context.CreateResponseAsync("Rules channel does not exist");
                return default;
            }

            var messages = await channel.GetMessagesAsync(MaxMessages);
            if (messages.Count >= MaxMessages)
            {
                await context.CreateResponseAsync($"Cowardly refusing to use rules channel with more than {MaxMessages} messages.");
                return default;
            }

            var nonBotMessage = messages.FirstOrDefault(x => x.Author != context.Client.CurrentUser);
            // ReSharper disable once InvertIf
            if (nonBotMessage != null)
            {
                await context.CreateResponseAsync($"Cowardly refusing to use rules channel with message {nonBotMessage.JumpLink} not authored by the bot.");
                return default;
            }

            messages = messages.Reverse().ToList();

            var rules = new StringBuilder();
            foreach (var msg in messages)
            {
                if (rules.Length > 0) rules.AppendLine().AppendLine().Append(MessageSeparator).AppendLine().AppendLine();
                rules.Append(msg.Content);
            }

            return (channel, messages, rules.ToString());
        }

        [SlashCommand("rules-download", "Downloads raw server rules")]
        [SlashCommandPermissions(DiscordService.CommandPermission)]
        public async Task RulesDownload(InteractionContext context)
        {
            var (_, _, rules) = await ResolveRulesChannel(context);
            if (rules == null) return;
            var msg = new DiscordInteractionResponseBuilder { IsEphemeral = true };
            msg.AddLongResponse(rules, lang: "md", inlineContent: "Current rules:");
            await context.CreateResponseAsync(msg);
        }

        [SlashCommand("rules-upload", "Edits server rules")]
        [SlashCommandPermissions(DiscordService.CommandPermission)]
        public async Task RuleEdit(
            InteractionContext context,
            [Option("rules", "New rules text file")]
            DiscordAttachment newRulesFile,
            [Option("confirm-code", "Confirmation code, omit to preview changes")]
            string confirmationCode = null)
        {
            if (!newRulesFile.FileName.EndsWith(".txt"))
            {
                await context.CreateResponseAsync("New rules should be a text file");
                return;
            }

            using var wc = new WebClient();
            var newRules = await wc.DownloadStringTaskAsync(new Uri(newRulesFile.Url));
            var newMessages = SplitToMessages(newRules);
            foreach (var msg in newMessages)
                if (msg.Length > CharacterLimit)
                {
                    const int split = CharacterLimit / 4;
                    await context.CreateResponseAsync(
                        $"Message is too long without any splits, insert a `{MessageSeparator}` at last every {CharacterLimit} characters.  " +
                        $"Problematic section:\n```\n{msg.Substring(0, split)}\n...\n{msg.Substring(msg.Length - split)}\n```");
                    return;
                }

            var (channel, oldMessages, oldRules) = await ResolveRulesChannel(context);
            if (oldRules == null) return;

            var diff = new UnidiffRenderer()
                .Generate(oldRules.Replace(MessageSeparator, ""), newRules.Replace(MessageSeparator, ""), "old.txt", "new.txt");
            var hash = Sha256String(diff);
            if (confirmationCode == null)
            {
                var msg = new DiscordInteractionResponseBuilder { IsEphemeral = false };
                msg.AddLongResponse(diff, lang: "diff",
                    inlineContent: $"Use confirmation code `{hash}` to commit proposed change:");
                await context.CreateResponseAsync(msg);
                return;
            }

            if (confirmationCode != hash)
            {
                await context.CreateResponseAsync("Wrong confirmation code for change");
                return;
            }

            var log = new StringBuilder();
            var shared = Math.Min(newMessages.Count, oldMessages.Count);
            for (var i = 0; i < shared; i++)
            {
                if (oldMessages[i].Content.Equals(newMessages[i])) continue;
                await AppendLog($"Modifying {oldMessages[i].JumpLink}");
                await oldMessages[i].ModifyAsync(msg => msg.Content = newMessages[i]);
            }

            if (shared < newMessages.Count)
            {
                await AppendLog($"Sending {newMessages.Count - shared} new messages");
                for (var i = shared; i < newMessages.Count; i++)
                {
                    var sent = await channel.SendMessageAsync(newMessages[i]);
                    await AppendLog($"Sent {sent.JumpLink}");
                }
            }

            if (shared < oldMessages.Count)
            {
                await AppendLog($"Deleting {oldMessages.Count - shared} old messages");
                for (var i = shared; i < oldMessages.Count; i++)
                    await oldMessages[i].DeleteAsync("Replaced");
            }

            var userVisibleChanges = diff.Count(x => x == '\n') > 2;
            if (!userVisibleChanges)
            {
                await AppendLog("Not posting changelog due to no user visible changes");
                return;
            }

            var changelogChannelId = _cfg.Current.RulesChangelogChannel;
            if (!changelogChannelId.HasValue) return;

            var changelogChannel = await context.Client.GetChannelAsync(changelogChannelId.Value);
            if (changelogChannel == null || changelogChannel.Guild != context.Guild)
            {
                await AppendLog("Not posting changelog due to no changelog channel");
                return;
            }

            await changelogChannel.SendMessageAsync(builder => builder.AddLongResponse(diff, lang: "diff", inlineContent: "Rules updated:"));
            await AppendLog($"Posted changelog to <#{changelogChannel.Id}>");
            return;

            Task AppendLog(string line)
            {
                var start = log.Length == 0;
                if (!start) log.AppendLine();
                log.Append(line);
                var content = log.ToString();
                return start ? context.CreateResponseAsync(content) : context.EditResponseAsync(content);
            }
        }

        private static List<string> SplitToMessages(string text)
        {
            var messages = new List<string>();
            var sb = new StringBuilder();
            foreach (var rawLine in text.Split('\n'))
            {
                var line = rawLine.TrimStart('\r').TrimEnd('\r');
                if (line.Equals(MessageSeparator))
                {
                    EndOfMessage();
                    continue;
                }

                if (sb.Length > 0) sb.AppendLine();
                sb.Append(line);
            }

            EndOfMessage();
            return messages;

            void EndOfMessage()
            {
                if (sb.Length > 0) messages.Add(sb.ToString().Trim());
                sb.Clear();
            }
        }

        private static string Sha256String(string str)
        {
            using var hash = SHA256.Create();
            var result = hash.ComputeHash(Encoding.UTF8.GetBytes(str));
            var sb = new StringBuilder(result.Length * 2);
            foreach (var b in result)
                sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }
}