using MediaBrowser.Model.Plugins;

namespace Emby.Plugin.CinePersona.Configuration
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        public string ApiKey { get; set; } = string.Empty;

        public string ServerUrl { get; set; } = "https://cinepersona.com";
    }
}
