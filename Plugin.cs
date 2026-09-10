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
                }
            };
        }
    }
}
