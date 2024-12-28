using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using Equ;
using Meds.Shared;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using SIPSorcery.Sys;
using ZLogger;
using Formatting = Newtonsoft.Json.Formatting;

namespace Meds.Watchdog.GrafanaAgent
{
    public class GaRenderedConfig : MemberwiseEquatable<GaRenderedConfig>
    {
        public bool Enabled;
        public string BinaryUrl;
        public string ConfigContent;
        public int HttpPort;
        public int GrpcPort;
    }

    public static class GaConfigRenderer
    {
        private class GrafanaAgentConfigInputs : MemberwiseEquatable<GrafanaAgentConfigInputs>
        {
            public GaConfig Config;
            public string PrometheusKey;

            public static GrafanaAgentConfigInputs Create(Configuration value) => new GrafanaAgentConfigInputs
            {
                Config = value.GrafanaAgent,
                PrometheusKey = value.Metrics?.PrometheusKey
            };
        }

        private class VanillaRemoteApiConfig : MemberwiseEquatable<VanillaRemoteApiConfig>
        {
            public bool Enabled;
            public uint Port;

            public static VanillaRemoteApiConfig Read(string path)
            {
                var cfg = new VanillaRemoteApiConfig();
                if (!File.Exists(path)) return cfg;
                var doc = new XmlDocument();
                doc.Load(path);
                var root = doc.DocumentElement;
                if (root == null)
                    return cfg;
                foreach (var node in root.GetElementsByTagName("RemoteApiEnabled").OfType<XmlElement>())
                    if (bool.TryParse(node.InnerText, out var enabled))
                        cfg.Enabled = enabled;
                foreach (var node in root.GetElementsByTagName("RemoteApiPort").OfType<XmlElement>())
                    if (uint.TryParse(node.InnerText, out var port))
                        cfg.Port = port;
                return cfg;
            }
        }

        public static Refreshable<GaRenderedConfig> Create(IServiceProvider svc)
        {
            var install = svc.GetRequiredService<InstallConfiguration>();
            var vanillaCfg = ConfigRefreshable<VanillaRemoteApiConfig>.FromConfigFile(
                Path.Combine(install.RuntimeDirectory, "MedievalEngineersDedicated-Dedicated.cfg"),
                VanillaRemoteApiConfig.Read
            );
            return svc.GetRequiredService<Refreshable<Configuration>>()
                .Map(GrafanaAgentConfigInputs.Create)
                .Combine(vanillaCfg, (inputs, vanilla) => Render(install, inputs, vanilla));
        }

        private static GaRenderedConfig Render(InstallConfiguration install, GrafanaAgentConfigInputs inputs, VanillaRemoteApiConfig vanillaRemote)
        {
            var ga = inputs.Config;

            var staticTags = new Dictionary<string, string>();
            foreach (var kv in ga.StaticTags)
                staticTags[kv.Key.ToLowerInvariant()] = kv.Value.ToLowerInvariant();
            const string instanceTag = "instance";
            if (!string.IsNullOrEmpty(install.Instance) && !staticTags.ContainsKey(instanceTag))
                staticTags.Add(instanceTag, install.Instance);

            var enabled = false;
            var config = new Dictionary<string, object>();

            if (!string.IsNullOrEmpty(inputs.PrometheusKey) && !string.IsNullOrEmpty(ga.Prometheus?.Url) && vanillaRemote.Enabled && vanillaRemote.Port != 0)
            {
                enabled = true;

                var promTenant = ga.Prometheus.TenantId ?? ga.TenantId;
                var promOAuth = ga.Prometheus.OAuth ?? ga.OAuth;
                var promBasicAuth = promOAuth != null ? null : ga.Prometheus.BasicAuth ?? ga.BasicAuth;

                config["metrics"] = new Dictionary<string, object>
                {
                    ["wal_directory"] = Path.Combine(install.GrafanaAgentDirectory, "metrics-wal"),
                    ["global"] = new Dictionary<string, object>
                    {
                        ["scrape_interval"] = ga.Prometheus.ScrapeInterval ?? "1m",
                        ["remote_write"] = new object[]
                        {
                            new Dictionary<string, object>
                            {
                                ["url"] = ga.Prometheus.Url,
                                ["headers"] = new Dictionary<string, object>
                                {
                                    ["X-Scope-OrgID"] = promTenant,
                                },
                                ["oauth2"] = RenderOAuth(promOAuth, "vm-write"),
                                ["basic_auth"] = RenderBasic(promBasicAuth)
                            }
                        }
                    },
                    ["configs"] = new object[]
                    {
                        new Dictionary<string, object>
                        {
                            ["name"] = "meds",
                            ["scrape_configs"] = new object[]
                            {
                                new Dictionary<string, object>
                                {
                                    ["job_name"] = "meds",
                                    ["metrics_path"] = "/vrageremote/metrics",
                                    ["authorization"] = new Dictionary<string, object> { ["credentials"] = inputs.PrometheusKey },
                                    ["static_configs"] = new object[]
                                    {
                                        new Dictionary<string, object>
                                        {
                                            ["targets"] = new object[] { $"127.0.0.1:{vanillaRemote.Port}" },
                                            ["labels"] = staticTags
                                        }
                                    }
                                }
                            }
                        }
                    }
                };
            }

            if (!string.IsNullOrEmpty(ga.Loki?.Url))
            {
                enabled = true;

                var lokiTenant = ga.Loki.TenantId ?? ga.TenantId;
                var lokiOAuth = ga.Loki.OAuth ?? ga.OAuth;
                var lokiBasicAuth = lokiOAuth != null ? null : ga.Loki.BasicAuth ?? ga.BasicAuth;

                config["logs"] = new Dictionary<string, object>
                {
                    ["positions_directory"] = Path.Combine(install.GrafanaAgentDirectory, "log-positions"),
                    ["configs"] = new object[]
                    {
                        new Dictionary<string, object>
                        {
                            ["name"] = "meds",
                            ["clients"] = new object[]
                            {
                                new Dictionary<string, object>
                                {
                                    ["url"] = ga.Loki.Url,
                                    ["tenant_id"] = lokiTenant,
                                    ["oauth2"] = RenderOAuth(lokiOAuth, "loki-write"),
                                    ["basic_auth"] = RenderBasic(lokiBasicAuth),
                                    ["external_labels"] = staticTags,
                                }
                            },
                            ["scrape_configs"] = new object[]
                            {
                                new Dictionary<string, object>
                                {
                                    ["job_name"] = "meds",
                                    ["static_configs"] = new object[]
                                    {
                                        new Dictionary<string, object>
                                        {
                                            ["targets"] = new object[] { "localhost" },
                                            ["labels"] = new Dictionary<string, string>
                                            {
                                                ["__path__"] = $"{install.LogsDirectory.Replace('\\', '/').TrimEnd('/')}/*/*.log"
                                            }
                                        }
                                    },
                                    ["pipeline_stages"] = new object[]
                                    {
                                        SingleEntry("regex", new Dictionary<string, object>
                                        {
                                            ["source"] = "filename",
                                            ["expression"] = @"^.*[\\\/](?P<tool>[^\\\/]+)[\\\/][^\\\/]*\.log$",
                                        }),
                                        SingleEntry("json", SingleEntry("expressions", new Dictionary<string, string>
                                        {
                                            ["logger"] = nameof(LogInfo.CategoryName),
                                            ["level"] = nameof(LogInfo.LogLevel),
                                            ["time"] = nameof(LogInfo.Timestamp),
                                            ["thread"] = "ThreadName",
                                            ["mod_id"] = "Payload.Package.ModId",
                                        })),
                                        SingleEntry("timestamp", new Dictionary<string, string>
                                        {
                                            ["source"] = "time",
                                            ["format"] = "RFC3339Nano",
                                        }),
                                        SingleEntry("labels", new Dictionary<string, string>
                                        {
                                            ["tool"] = "tool"
                                        }),
                                        SingleEntry("labeldrop", new object[] { "filename" })
                                    }
                                }
                            }
                        }
                    }
                };
            }

            var json = JsonConvert.SerializeObject(DropNullsFromDict(config), settings: new JsonSerializerSettings
            {
                Formatting = Formatting.Indented,
            });


            var binaryUrl = ga.Version == null || ga.Version.Contains("://")
                ? ga.Version
                : $"https://github.com/grafana/agent/releases/download/{ga.Version}/grafana-agent-windows-amd64.exe.zip";

            return new GaRenderedConfig
            {
                Enabled = enabled && binaryUrl != null && ga.Enabled,
                BinaryUrl = binaryUrl,
                ConfigContent = json,
                HttpPort = ga.HttpPort,
                GrpcPort = ga.GrpcPort,
            };

            Dictionary<string, object> SingleEntry(string key, object value) => new Dictionary<string, object> { [key] = value };

            object RenderOAuth(GaConfig.OAuthConfig cfg, params string[] defaultScopes) => cfg == null
                ? null
                : new Dictionary<string, object>
                {
                    ["client_id"] = cfg.Id,
                    ["client_secret"] = cfg.Secret,
                    ["scopes"] = (cfg.Scopes?.Split(',') ?? defaultScopes).Select(x => x.Trim()).ToArray(),
                    ["token_url"] = cfg.TokenUrl
                };

            object RenderBasic(GaConfig.BasicAuthConfig cfg) => cfg == null
                ? null
                : new Dictionary<string, object>
                {
                    ["username"] = cfg.Username,
                    ["password"] = cfg.Password
                };
        }

        private static object DropNulls(object value) => value switch
        {
            Dictionary<string, object> dict => DropNullsFromDict(dict),
            object[] array => DropNullsFromList(array),
            _ => value
        };

        private static object[] DropNullsFromList(object[] withNulls)
        {
            List<object> copy = null;
            for (var i = withNulls.Length - 1; i >= 0; --i)
            {
                var original = withNulls[i];
                var repaired = DropNulls(original);
                if (repaired != null && repaired == original) continue;
                copy ??= new List<object>(withNulls);
                if (repaired == null)
                {
                    copy.RemoveAt(i);
                    continue;
                }

                copy[i] = repaired;
            }

            return copy?.ToArray() ?? withNulls;
        }

        private static Dictionary<string, object> DropNullsFromDict(Dictionary<string, object> withNulls)
        {
            Dictionary<string, object> copy = null;
            foreach (var (key, original) in withNulls)
            {
                var repaired = DropNulls(original);
                if (repaired != null && repaired == original) continue;
                copy ??= new Dictionary<string, object>(withNulls);
                if (repaired == null)
                {
                    copy.Remove(key);
                    continue;
                }

                copy[key] = repaired;
            }

            return copy ?? withNulls;
        }
    }
}