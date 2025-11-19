#nullable disable

using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.AdultMetadata.Configuration
{
    /// <summary>
    /// Plugin configuration for AdultMetadata.
    /// </summary>
    public class PluginConfiguration : BasePluginConfiguration
    {
        /// <summary>
        /// Gets or sets a value indicating whether to enable GEVI provider.
        /// </summary>
        public bool EnableGevi { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether to enable AEBN provider.
        /// </summary>
        public bool EnableAebn { get; set; } = true;

        /// <summary>
        /// Gets or sets the API key for AEBN if available.
        /// </summary>
        public string AebnApiKey { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the language for metadata.
        /// </summary>
        public string Language { get; set; } = "en";
    }
}