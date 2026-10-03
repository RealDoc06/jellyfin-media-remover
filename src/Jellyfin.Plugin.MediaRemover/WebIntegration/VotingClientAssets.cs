namespace Jellyfin.Plugin.MediaRemover.WebIntegration;

public static class VotingClientAssets
{
    public static string Version { get; } = typeof(VotingClientAssets).Assembly.GetName().Version?.ToString() ?? "0";
}
