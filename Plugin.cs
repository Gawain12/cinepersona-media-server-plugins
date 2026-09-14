using System;
using System.Collections.Generic;
using System.IO;
using Emby.Plugin.CinePersona.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Emby.Plugin.CinePersona
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IHasThumbImage
    {
        private const string ThumbImageResource = "Emby.Plugin.CinePersona.assets.emby-catalog-thumb.png";

        public static Plugin Instance { get; private set; }

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
            InjectWebScript(applicationPaths);
        }

        public override string Name => "CinePersona";

        public override string Description => "自动同步 Emby 观影记录到 CinePersona";

        public override Guid Id => Guid.Parse("f62e8471-469b-43d8-b57f-f4a4d7d10001");

        public Stream GetThumbImage()
        {
            return GetType().Assembly.GetManifestResourceStream(ThumbImageResource);
        }

        public ImageFormat ThumbImageFormat => ImageFormat.Png;

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = "cinepersona",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html"
                },
                new PluginPageInfo
                {
                    Name = "CinePersonaConfigurationPageJS",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.CinePersonaConfigurationPageJS.js"
                },
                new PluginPageInfo
                {
                    Name = "CinePersonaWeb",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.CinePersonaWeb.js"
                }
            };
        }

        private static void InjectWebScript(IApplicationPaths applicationPaths)
        {
            try
            {
                var systemPath = applicationPaths?.ProgramSystemPath;
                if (string.IsNullOrWhiteSpace(systemPath))
                {
                    return;
                }

                var indexPath = Path.Combine(systemPath, "dashboard-ui", "index.html");
                if (!File.Exists(indexPath))
                {
                    return;
                }

                var contents = File.ReadAllText(indexPath);
                var version = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "0.2.3";
                var expectedScript = string.Format("<script data-cinepersona-web=\"true\" src=\"/web/ConfigurationPage?name=CinePersonaWeb&v={0}\"></script>", version);

                if (contents.IndexOf(expectedScript, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return;
                }

                // 如果已有旧版本的标签（包括无版本号或旧版本号），直接替换
                var regex = new System.Text.RegularExpressions.Regex(@"<script\s+[^>]*data-cinepersona-web=""true""[^>]*></script>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (regex.IsMatch(contents))
                {
                    contents = regex.Replace(contents, expectedScript);
                }
                else
                {
                    var bodyIndex = contents.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
                    contents = bodyIndex >= 0
                        ? contents.Insert(bodyIndex, expectedScript)
                        : contents + expectedScript;
                }
                File.WriteAllText(indexPath, contents);
            }
            catch
            {
                // A missing or read-only web directory must not prevent the
                // server plugin from loading. The script will be retried on
                // the next server restart.
            }
        }
    }
}
