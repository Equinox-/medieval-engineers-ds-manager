using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using DSharpPlus.SlashCommands;
using Meds.Shared;
using Meds.Watchdog.Utils;

namespace Meds.Watchdog.Discord
{
    public class DiscordCmdConfig : DiscordCmdBase
    {
        private static readonly TimeSpan FreshBackupExpiry = TimeSpan.FromHours(1);

        private readonly Refreshable<Configuration> _cfg;
        private readonly LifecycleController _lifecycle;

        public DiscordCmdConfig(DiscordService discord, Refreshable<Configuration> cfg, LifecycleController lifecycle) : base(discord)
        {
            _cfg = cfg;
            _lifecycle = lifecycle;
        }

        public enum ConfigFile
        {
            Self,
            DedicatedServer,
            WorldConfig,
        }

        private enum ConfigAction
        {
            Read,
            Update,
            Delete,
            AppendXml,
        }

        private const string DeletionToken = "!Delete";
        private const string AppendXmlToken = "!AppendXml:";

        private static ConfigAction ActionFor(string value)
        {
            if (value == null) return ConfigAction.Read;
            if (string.Equals(value, DeletionToken, StringComparison.OrdinalIgnoreCase)) return ConfigAction.Delete;
            // ReSharper disable once ConvertIfStatementToReturnStatement
            if (value.StartsWith(AppendXmlToken, StringComparison.OrdinalIgnoreCase)) return ConfigAction.AppendXml;
            return ConfigAction.Update;
        }

        private async Task WithConfig(InteractionContext ctx, ConfigFile file, Func<string, Task> task)
        {
            var (path, mustBeShutdown) = file switch
            {
                ConfigFile.Self => (_cfg.Current.ConfigFile, false),
                ConfigFile.DedicatedServer => (_cfg.Current.DedicatedServerConfigFile, true),
                ConfigFile.WorldConfig => (_cfg.Current.WorldConfigFile, true),
                _ => throw new ArgumentOutOfRangeException(nameof(file), file, null)
            };

            // When writing, ensure the server gets shutdown.
            if (mustBeShutdown && _lifecycle.Active.State != LifecycleStateCase.Shutdown)
            {
                await ctx.EditResponseAsync("Server must be shutdown to edit this config");
                return;
            }

            using var token = mustBeShutdown ? (IDisposable)_lifecycle.PinState() : null;
            if (mustBeShutdown)
            {
                await ctx.EditResponseAsync("Preventing server from restarting...");
                await ((LifecycleController.PinStateToken)token).Task;
                if (_lifecycle.Active.State != LifecycleStateCase.Shutdown)
                {
                    await ctx.EditResponseAsync("Failed to prevent server from restarting");
                    return;
                }
            }

            await task(path);
        }

        public enum BackupRestoreAction
        {
            Backup,
            Restore,
        }

        private const string BackupSuffix = ".bak";
        
        [SlashCommand("config-backup", "Backup and restore configuration")]
        [SlashCommandPermissions(DiscordService.CommandPermission)]
        public async Task ConfigBackupRestore(
            InteractionContext ctx,
            [Option("file", "Configuration file to edit")]
            ConfigFile file,
            [Option("action", "Action to take")] BackupRestoreAction action)
        {
            await ctx.CreateResponseAsync($"{action} config {file}...");
            await WithConfig(ctx, file, async path =>
            {
                var backupPath = path + BackupSuffix;
                switch (action)
                {
                    case BackupRestoreAction.Backup:
                    {
                        File.Copy(path, backupPath, true);
                        await ctx.EditResponseAsync($"Created backup for {file} at {DateTime.UtcNow.AsDiscordTime()}");
                        return;
                    }
                    case BackupRestoreAction.Restore:
                    {
                        var info = new FileInfo(backupPath);
                        if (!info.Exists)
                        {
                            await ctx.EditResponseAsync($"No backup exists for {file}");
                            return;
                        }

                        var time = info.CreationTimeUtc;
                        FileUtils.MoveAtomic(backupPath, path);
                        await ctx.EditResponseAsync($"Restored backup for {file} taken at {time.AsDiscordTime()}");
                        return;
                    }
                    default:
                        throw new ArgumentOutOfRangeException(nameof(action), action, null);
                }
            });
        }

        [SlashCommand("config-access", "Reads and modifies configuration")]
        [SlashCommandPermissions(DiscordService.CommandPermission)]
        public async Task ConfigAccess(
            InteractionContext ctx,
            [Option("file", "Configuration file to edit")]
            ConfigFile file,
            [Option("xpath", "Path to the node or attribute to edit")]
            string xpath,
            [Option("value", "Value to assign to the attribute or element, or omit to read the value.")]
            string value = null)
        {
            var action = ActionFor(value);
            await ctx.CreateResponseAsync($"{action} config {file} at `{xpath}`...");
            await WithConfig(ctx, file, async path =>
            {
                if (action != ConfigAction.Read)
                {
                    var info = new FileInfo(path + BackupSuffix);
                    if (!info.Exists)
                    {
                        await ctx.EditResponseAsync($"Refusing to edit config {file} without a backup");
                        return;
                    }

                    if (info.CreationTimeUtc + FreshBackupExpiry < DateTime.UtcNow)
                    {
                        await ctx.EditResponseAsync($"Refusing to edit config {file} without a recent backup, the most recent one was at {info.CreationTimeUtc.AsDiscordTime()}.");
                        return;
                    }
                }

                // Read the current config value.
                var doc = new XmlDocument
                {
                    PreserveWhitespace = true
                };
                doc.Load(path);

                var nav = doc.CreateNavigator();
                if (nav == null)
                {
                    await ctx.EditResponseAsync("Failed to create XPath navigator");
                    return;
                }

                object result;
                {
                    var itr = nav.Select(xpath);
                    if (!itr.MoveNext())
                    {
                        await ctx.EditResponseAsync("XPath did not return any results");
                        return;
                    }

                    result = itr.Current.UnderlyingObject;
                    if (itr.MoveNext())
                    {
                        await ctx.EditResponseAsync(
                            $"XPath returned multiple results, {NodePath(result as XmlNode)} and {NodePath(itr.Current.UnderlyingObject as XmlNode)}.");
                        return;
                    }
                }

                bool modified;
                switch (result)
                {
                    case XmlElement element:
                        var hasChildren = element.ChildNodes.OfType<XmlElement>().Any();
                        if (action == ConfigAction.AppendXml)
                        {
                            VerifyNotSecret(element);
                            element.InnerXml += value!.Substring(AppendXmlToken.Length);
                            modified = true;
                        }
                        else
                        {
                            if (hasChildren)
                            {
                                await ctx.EditResponseAsync("Accessing XML elements with child elements is not supported.");
                                return;
                            }

                            modified = await ApplyChange(element, () => element.InnerText, val => element.InnerText = val);
                        }

                        break;
                    case XmlAttribute attribute:
                        modified = await ApplyChange(attribute, () => attribute.Value, val => attribute.Value = val);
                        break;
                    default:
                        await ctx.EditResponseAsync($"XPath returned unexpected result {result?.GetType().Name}, expected element or attribute.");
                        return;
                }

                if (modified) FileUtils.WriteAtomic(path, stream => doc.Save(stream));
            });
            return;

            async Task<bool> ApplyChange(XmlNode node, Func<string> read, Action<string> write)
            {
                VerifyNotSecret(node);
                switch (action)
                {
                    case ConfigAction.Read:
                        await ctx.EditResponseAsync($"{NodePath(node)} is `{read()}`");
                        return false;
                    case ConfigAction.Update:
                        await ctx.EditResponseAsync($"Setting {NodePath(node)} to `{value}`");
                        write(value);
                        return true;
                    case ConfigAction.Delete:
                        await ctx.EditResponseAsync($"Removing {NodePath(node)}");
                        node.ParentNode?.RemoveChild(node);
                        return true;
                    case ConfigAction.AppendXml:
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }
        }

        private static void VerifyNotSecret(XmlNode node)
        {
            while (node != null)
            {
                if (node.Name.IndexOf("Auth", StringComparison.OrdinalIgnoreCase) >= 0
                    || node.Name.IndexOf("Secret", StringComparison.OrdinalIgnoreCase) >= 0
                    || node.Name.IndexOf("Key", StringComparison.OrdinalIgnoreCase) >= 0
                    || node.Name.IndexOf("Token", StringComparison.OrdinalIgnoreCase) >= 0
                    || node.Name.IndexOf("Password", StringComparison.OrdinalIgnoreCase) >= 0)
                    throw new ArgumentException($"Node {NodePath(node)} contains secret material, cannot be accessed over discord.");
                node = node.ParentNode;
            }
        }

        private static string NodePath(XmlNode node)
        {
            if (node == null) return "null";
            var path = new StringBuilder();
            if (node is XmlAttribute)
                path.Append("@").Append(node.Name);
            else
                path.Append("/").Append(node.Name);
            node = node.ParentNode;

            while (node != null)
            {
                path.Insert(0, node.Name);
                path.Insert(0, "/");
                node = node.ParentNode;
            }

            return path.ToString();
        }
    }
}