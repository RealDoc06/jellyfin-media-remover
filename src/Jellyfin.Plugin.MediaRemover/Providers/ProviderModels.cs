namespace Jellyfin.Plugin.MediaRemover.Providers;

public sealed record ProviderOptions(string SonarrUrl, string SonarrApiKey, string SeerrUrl, string SeerrApiKey,
    string RadarrUrl = "", string RadarrApiKey = "");

public sealed record SonarrSeries(int Id, int TvdbId, string Title, string Path);

public sealed record RadarrMovie(int Id, int TmdbId, string Title, string Path);

public sealed record SeerrMedia(int Id, int TmdbId, int? TvdbId, string MediaType, int RequestCount);

public sealed record SeerrRequestInfo(int Id, int TmdbId, int? TvdbId, IReadOnlyList<SeerrRequest> Requests);

public sealed record SeerrRequest(int Id, SeerrRequester? RequestedBy);

public sealed record SeerrRequester(int Id, string Name, Guid? JellyfinUserId);

/// <summary>A provider failure whose message is safe to display without exposing credentials or response bodies.</summary>
public sealed class ProviderException(string message) : Exception(message)
{
}
