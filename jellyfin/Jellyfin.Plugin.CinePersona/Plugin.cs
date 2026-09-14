using System.Globalization;
using System.IO;
using Jellyfin.Plugin.CinePersona.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CinePersona;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        PluginPaths = applicationPaths;
        InjectWebScript(applicationPaths);
        _ = RetryWebScriptInjectionAsync(applicationPaths);
    }

    public static Plugin? Instance { get; private set; }

    internal static IApplicationPaths? PluginPaths { get; private set; }

    private static async Task RetryWebScriptInjectionAsync(IApplicationPaths applicationPaths)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            InjectWebScript(applicationPaths);
            await Task.Delay(TimeSpan.FromSeconds(45)).ConfigureAwait(false);
            InjectWebScript(applicationPaths);
        }
        catch
        {
            // A server shutdown during the retry window is harmless.
        }
    }

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

    internal static void InjectWebScript(IApplicationPaths applicationPaths, ILogger? logger = null)
    {
        try
        {
            var webPaths = new[]
            {
                Environment.GetEnvironmentVariable("JELLYFIN_WEB_DIR"),
                applicationPaths?.WebPath,
                Path.Combine(applicationPaths?.ProgramSystemPath ?? string.Empty, "jellyfin-web"),
                Path.Combine(applicationPaths?.ProgramSystemPath ?? string.Empty, "dashboard-ui")
            };

            var indexPath = webPaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => Path.Combine(path!, "index.html"))
                .FirstOrDefault(File.Exists);
            if (indexPath is null)
            {
                logger?.LogDebug("CinePersona could not find Jellyfin Web index for injection");
                return;
            }

            var contents = File.ReadAllText(indexPath);
            var version = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "0.2.4";
            var expectedScript = string.Format("<script data-cinepersona-web=\"true\" src=\"/web/ConfigurationPage?name=CinePersonaWeb&v={0}\"></script>", version);

            if (contents.IndexOf(expectedScript, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return;
            }

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
            logger?.LogInformation("CinePersona Web review script (v{Version}) injected into {IndexPath}", version, indexPath);
        }
        catch (Exception exception)
        {
            logger?.LogWarning(exception, "CinePersona Web review script injection failed");
        }
    }
}
