using Jellyfin.Plugin.MediaRemover.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.MediaRemover;

public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths paths, IXmlSerializer serializer) : base(paths, serializer)
    {
        Instance = this;
        JournalPath = Path.Combine(paths.DataPath, "media-remover", "operations.json");
        VotesPath = Path.Combine(paths.DataPath, "media-remover", "votes.json");
    }

    public static Plugin Instance { get; private set; } = null!;
    public string JournalPath { get; }
    public string VotesPath { get; }
    public override string Name => "Media Remover";
    public override Guid Id => Guid.Parse("8b60f2b0-8e08-4b9c-a6e2-5e4f4d519980");
    public override string Description => "Advisory votes for library media, with movie and series progress and Radarr/Sonarr/Seerr removal.";

    public IEnumerable<PluginPageInfo> GetPages() =>
    [
        new() { Name = "mediaremover", EmbeddedResourcePath = GetType().Namespace + ".Web.configuration.html", EnableInMainMenu = true },
        new() { Name = "mediaremoverjs", EmbeddedResourcePath = GetType().Namespace + ".Web.configuration.js" }
    ];
}
