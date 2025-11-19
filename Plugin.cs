#nullable disable

using System;
using System.Collections.Generic;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Jellyfin.Plugin.AdultMetadata.Configuration;

namespace Jellyfin.Plugin.AdultMetadata
{
    /// <summary>
    /// Plugin class for AdultMetadata library.
    /// </summary>
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="Plugin"/> class.
        /// </summary>
        /// <param name="applicationPaths">application paths.</param>
        /// <param name="xmlSerializer">xml serializer.</param>
        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
        }

        /// <summary>
        /// Gets the instance of AdultMetadata plugin.
        /// </summary>
        public static Plugin Instance { get; private set; }

        /// <inheritdoc/>
        public override Guid Id => new Guid("f5c8d4e2-1a3b-4c5d-8e9f-0a1b2c3d4e5f");

        /// <inheritdoc/>
        public override string Name => "Adult Metadata Provider";

        /// <inheritdoc/>
        public override string Description => "Get metadata for adult movies from GEVI and AEBN.";

        /// <inheritdoc/>
        public override string ConfigurationFileName => "Jellyfin.Plugin.AdultMetadata.xml";

        /// <summary>
        /// Return the plugin configuration page.
        /// </summary>
        /// <returns>PluginPageInfo.</returns>
        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = "AdultMetadata",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html"
                }
            };
        }
    }
}