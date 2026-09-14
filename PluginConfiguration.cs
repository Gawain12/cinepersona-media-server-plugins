using System;
using MediaBrowser.Model.Plugins;

namespace Emby.Plugin.CinePersona.Configuration
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        public string ApiKey { get; set; } = string.Empty;

        public string ServerUrl { get; set; } = "https://cinepersona.com";

        public bool ReverseSyncEnabled { get; set; } = true;

        public string SyncUserId { get; set; } = string.Empty;

        public bool InitialSyncCompleted { get; set; }

        public DateTime? LastSyncAt { get; set; }

        public string LastSyncUserId { get; set; } = string.Empty;
    }
}
