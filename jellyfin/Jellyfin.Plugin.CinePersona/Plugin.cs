using System.Globalization;
using System.IO;
using Jellyfin.Plugin.CinePersona.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.CinePersona;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        InjectWebScript(applicationPaths);
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "CinePersona";

    public override string Description => "自动同步 Jellyfin 观影记录到 CinePersona";

    public override Guid Id => Guid.Parse("f62e8471-469b-43d8-b57f-f4a4d7d10002");

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}.Configuration.configPage.html",
                    GetType().Namespace)
            },
            new PluginPageInfo
            {
                Name = "CinePersonaWeb",
                EmbeddedResourcePath = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}.Configuration.CinePersonaWeb.js",
                    GetType().Namespace)
            }
        };
    }

    private static void InjectWebScript(IApplicationPaths applicationPaths)
    {
        try
        {
            var webPath = applicationPaths?.WebPath;
            if (string.IsNullOrWhiteSpace(webPath))
            {
                return;
            }

            var indexPath = Path.Combine(webPath, "index.html");
            if (!File.Exists(indexPath))
            {
                return;
            }

            var contents = File.ReadAllText(indexPath);
            const string marker = "data-cinepersona-web=\"true\"";
            if (contents.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return;
            }

            const string script = "<script data-cinepersona-web=\"true\" src=\"/web/ConfigurationPage?name=CinePersonaWeb\"></script>";
            var bodyIndex = contents.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            contents = bodyIndex >= 0
                ? contents.Insert(bodyIndex, script)
                : contents + script;
            File.WriteAllText(indexPath, contents);
        }
        catch
        {
            // A missing or read-only web directory must not prevent the
            // server plugin from loading. Retry on the next server restart.
        }
    }
}
