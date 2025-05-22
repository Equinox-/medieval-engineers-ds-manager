using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DSharpPlus.Entities;
using DSharpPlus.Interactivity;
using DSharpPlus.Interactivity.Extensions;
using DSharpPlus.SlashCommands;
using Meds.Shared.Data;
using Meds.Watchdog.Utils;

namespace Meds.Watchdog.Discord
{
    public static class DiscordUtils
    {
        public const string RepositoryUrl = "https://github.com/Equinox-/medieval-engineers-ds-manager";

        public class TableFormatter
        {
            private readonly int _monoSpacedColumns;
            private readonly List<int> _widths = new List<int>();
            private readonly List<string[]> _rows = new List<string[]>();

            public TableFormatter(int monoSpacedColumns = int.MaxValue) => _monoSpacedColumns = monoSpacedColumns;

            public void AddRow(params string[] row)
            {
                for (var i = 0; i < row.Length; i++)
                {
                    if (i < _widths.Count)
                        _widths[i] = Math.Max(_widths[i], row[i].Length);
                    else
                        _widths.Add(row[i].Length);
                }

                _rows.Add(row);
            }

            public int RowCount => _rows.Count;

            public void Clear()
            {
                _widths.Clear();
                _rows.Clear();
            }

            public IEnumerable<string> Lines()
            {
                var line = new StringBuilder();
                foreach (var row in _rows)
                {
                    line.Clear();
                    if (_monoSpacedColumns > 0)
                        line.Append('`');
                    for (var i = 0; i < row.Length; i++)
                    {
                        if (i == _monoSpacedColumns && _monoSpacedColumns > 0)
                            line.Append('`');
                        line.Append(row[i]);
                        var padding = 1 + _widths[i] - row[i].Length;
                        for (var j = 0; j < padding; j++)
                            line.Append(' ');
                    }

                    if (_monoSpacedColumns >= row.Length)
                        line.Append('`');
                    yield return line.ToString();
                }
            }

            public override string ToString()
            {
                var builder = new StringBuilder();
                foreach (var line in Lines())
                    builder.AppendLine(line);
                return builder.ToString();
            }
        }

        public static void RemoveFieldWithName(this DiscordEmbedBuilder embed, string field)
        {
            for (var i = 0; i < embed.Fields.Count; i++)
                if (embed.Fields[i].Name.Equals(field))
                {
                    embed.RemoveFieldAt(i);
                    return;
                }
        }

        private const int EmbedDescriptionLimit = 4096;
        private const int MessageLengthLimit = 2000;

        public static Task RespondLongText(this InteractionContext context, IEnumerable<string> lines)
        {
            var msg = new DiscordWebhookBuilder();
            using (var enumerator = lines.GetEnumerator())
            {
                var initialBuffer = new StringBuilder();
                var end = false;
                while (!end && initialBuffer.Length < MessageLengthLimit - 32)
                {
                    if (enumerator.MoveNext())
                        initialBuffer.AppendLine(enumerator.Current);
                    else
                        end = true;
                }

                if (end)
                {
                    initialBuffer.Insert(0, "```\n");
                    initialBuffer.Append("```");
                    msg.Content = initialBuffer.ToString();
                }
                else
                {
                    var memory = new MemoryStream();
                    using (var writer = new StreamWriter(memory, Encoding.UTF8, 1024, true))
                    {
                        writer.Write(initialBuffer.ToString());
                        while (enumerator.MoveNext())
                            writer.WriteLine(enumerator.Current);
                    }

                    memory.Position = 0;
                    msg.AddFile("response.txt", memory);
                }
            }

            return context.EditResponseAsync(msg);
        }

        public delegate Page DelCreatePage<in T>(IEnumerable<T> pageItems, int firstInclusive, int lastExclusive);

        private const int DefaultPageSize = 10;
        private const string DefaultNoPages = "No results";

        public static Task RespondPaginated<T>(
            this InteractionContext context,
            List<T> items,
            TableFormatter table,
            Action<TableFormatter, T> addRow,
            string objectType = "Result",
            int pageSize = DefaultPageSize,
            string noPagesMessage = null,
            string[] headers = null)
        {
            return context.RespondPaginated(items, (pageItems, inclusive, exclusive) =>
                {
                    table.Clear();
                    if (headers != null)
                        table.AddRow(headers);
                    foreach (var item in pageItems)
                        addRow(table, item);

                    var embed = new DiscordEmbedBuilder();
                    var desc = inclusive + 1 == exclusive
                        ? $"{objectType} {inclusive + 1} of {items.Count}"
                        : $"{objectType}s {inclusive + 1}-{exclusive} of {items.Count}";
                    embed.AddField(desc, table.ToString());
                    return new Page(embed: embed);
                }, pageSize, noPagesMessage ?? $"No {objectType.ToLower()}s");
        }

        public static Task RespondPaginated<T>(
            this InteractionContext context,
            List<T> items,
            DelCreatePage<T> pageCreator,
            int pageSize = DefaultPageSize,
            string noPagesMessage = DefaultNoPages)
        {
            var pages = new List<Page>((items.Count + pageSize - 1) / pageSize);
            for (var i = 0; i < items.Count; i += pageSize)
            {
                var right = Math.Min(i + pageSize, items.Count);
                pages.Add(pageCreator(items.Skip(i).Take(pageSize), i, right));
            }

            return context.RespondPaginated(pages, noPagesMessage);
        }

        public static Task<DiscordMessage> EditResponseAsync(this InteractionContext context, string content)
        {
            return context.EditResponseAsync(new DiscordWebhookBuilder { Content = content });
        }

        public static Task<DiscordMessage> EditResponseAsync(this InteractionContext context, DiscordEmbed embed)
        {
            return context.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(embed));
        }

        public static async Task RespondPaginated(this InteractionContext context, List<Page> pages, string noPagesMessage = DefaultNoPages)
        {
            switch (pages.Count)
            {
                case 0:
                    await context.EditResponseAsync(noPagesMessage);
                    break;
                case 1:
                    if (pages[0].Embed != null)
                        await context.EditResponseAsync(pages[0].Embed);
                    else
                        await context.EditResponseAsync(pages[0].Content);
                    break;
                default:
                    await context.DeleteResponseAsync();
                    await context.Channel.SendPaginatedMessageAsync(context.Member, pages);
                    break;
            }
        }

        public static string FormatHumanBytes(long bytes)
        {
            const long kibi = 1024;
            const long mebi = kibi * 1024;
            const long gibi = mebi * 1024;
            if (bytes >= gibi)
                return $"{bytes / (double)gibi:F1} GiB";
            if (bytes >= mebi)
                return $"{bytes / (double)mebi:F1} MiB";
            if (bytes >= kibi)
                return $"{bytes / (double)kibi:F1} KiB";
            return $"{bytes} B";
        }

        public static string FormatHumanDuration(this TimeSpan time)
        {
            if (time.TotalDays >= 2)
                return $"{Math.Round(time.TotalDays)} days";
            if (time.TotalHours >= 2)
                return $"{Math.Round(time.TotalHours)} hours";
            if (time.TotalMinutes >= 2)
                return $"{Math.Round(time.TotalMinutes)} minutes";
            var seconds = (int)Math.Round(time.TotalSeconds);
            return $"{seconds} second{(seconds != 1 ? "s" : "")}";
        }

        public static string RenderPlayerForDiscord(this PlayerResponse player)
        {
            var response = new StringBuilder();
            if (player.FactionTag != null)
                response.Append("[").Append(player.FactionTag).Append("] ");
            response.Append(player.Name);
            switch (player.Promotion)
            {
                case PlayerPromotionLevel.Moderator:
                    response.Append(" (Mod)");
                    break;
                case PlayerPromotionLevel.Admin:
                    response.Append(" (Admin)");
                    break;
                case PlayerPromotionLevel.None:
                default:
                    break;
            }

            return response.ToString();
        }
    }
}