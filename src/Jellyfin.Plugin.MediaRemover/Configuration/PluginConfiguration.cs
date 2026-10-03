using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.MediaRemover.Configuration;

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public string SonarrUrl { get; set; } = "";
    public string SonarrApiKey { get; set; } = "";
    public string SeerrUrl { get; set; } = "";
    public string SeerrApiKey { get; set; } = "";
    public string RadarrUrl { get; set; } = "";
    public string RadarrApiKey { get; set; } = "";
}
