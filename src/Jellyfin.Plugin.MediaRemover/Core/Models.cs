using Jellyfin.Plugin.MediaRemover.Providers;

namespace Jellyfin.Plugin.MediaRemover.Core;

public sealed record LibraryUser(Guid Id, string Name);
public sealed record UserProgress(Guid Id, string Name, int WatchedCount, int InProgressCount, DateTime? LastPlayed, string Status);
public sealed record SeriesDetail(Guid Id, string Name, int? ProductionYear, int? TvdbId, int? TmdbId, int EpisodeCount, IReadOnlyList<UserProgress> Users, string Type = "Series");
public sealed record SeriesRow(Guid Id, string Name, int? ProductionYear, int? TvdbId, int? TmdbId, int EpisodeCount,
    int? WatchedCount, int? InProgressCount, DateTime? LastPlayed, string? Status, IReadOnlyList<UserProgress> Users, string Type = "Series");
public sealed record SeriesPage(IReadOnlyList<SeriesRow> Items, int TotalCount, int StartIndex);
public sealed record SeriesRequester(int Id, string Name, Guid? JellyfinUserId, int RequestCount,
    string WatchStatus, int? WatchedCount, int? EpisodeCount);
public sealed record SeriesRequests(string State, string? Message, int RequestCount, int UnknownRequesterCount,
    IReadOnlyList<SeriesRequester> Requesters);
// SeriesId retains its wire name so cached clients and existing journals remain readable.
public sealed record RemovalOptions(Guid SeriesId, bool RemoveSonarr, bool RemoveSeerr, bool DeleteFiles, bool AddImportListExclusion,
    bool RemoveRadarr = false, string MediaType = "Series");
public sealed record RemovalPreview(Guid Id, Guid SeriesId, string Title, DateTimeOffset ExpiresAt, RemovalOptions Options, SonarrSeries? Sonarr, SeerrMedia? Seerr, IReadOnlyList<string> Warnings,
    RadarrMovie? Radarr = null);

// Journal entries contain identities and outcomes, never provider credentials.
public sealed record RemovalOperation(Guid Id, Guid OwnerId, string Title, string SettingsFingerprint,
    RemovalOptions Options, SonarrSeries? Sonarr, SeerrMedia? Seerr, string Status,
    bool SonarrDone, bool SeerrDone, string? Error, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    RadarrMovie? Radarr = null, bool RadarrDone = true);

public sealed class RemovalException(string message, int statusCode = 400) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public interface ISeriesLibrary
{
    IReadOnlyList<LibraryUser> GetUsers();
    SeriesPage GetSeries(Guid userId, string? search, int startIndex, int limit);
    SeriesDetail GetSeries(Guid id);
    SeriesPage GetItems(Guid userId, string? search, int startIndex, int limit);
    SeriesDetail GetItem(Guid id);
}
