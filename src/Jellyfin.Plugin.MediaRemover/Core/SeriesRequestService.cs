using Jellyfin.Plugin.MediaRemover.Providers;

namespace Jellyfin.Plugin.MediaRemover.Core;

/// <summary>Reads request owners independently so a provider outage never hides Jellyfin viewing progress.</summary>
public sealed class SeriesRequestService(ISeriesLibrary library, IProviderGateway providers, Func<ProviderOptions> settings)
{
    private readonly SemaphoreSlim _requests = new(4, 4);

    public Task<SeriesRequests> GetAsync(Guid seriesId, CancellationToken ct) =>
        GetRequestsAsync(library.GetSeries(seriesId), ct);

    public Task<SeriesRequests> GetItemAsync(Guid itemId, CancellationToken ct) =>
        GetRequestsAsync(library.GetItem(itemId), ct);

    private async Task<SeriesRequests> GetRequestsAsync(SeriesDetail series, CancellationToken ct)
    {
        var options = settings();
        if (string.IsNullOrWhiteSpace(options.SeerrUrl) || string.IsNullOrWhiteSpace(options.SeerrApiKey))
            return new("notConfigured", "Connect Jellyseerr / Seerr to see who requested this item.", 0, 0, []);
        if (series.TmdbId is null)
            return new("missingMetadata", "Add the item TMDB ID in Jellyfin to match its requests.", 0, 0, []);

        await _requests.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var movie = series.Type == "Movie";
            var media = movie
                ? await providers.GetSeerrMovieRequestsAsync(options, series.TmdbId.Value, ct).ConfigureAwait(false)
                : await providers.GetSeerrRequestsAsync(options, series.TmdbId.Value, ct).ConfigureAwait(false);
            if (media is null) return new("notRequested", "No request records in Jellyseerr / Seerr.", 0, 0, []);
            if (media.TmdbId != series.TmdbId || (!movie && media.TvdbId is > 0 && series.TvdbId is > 0 && media.TvdbId != series.TvdbId))
                throw new ProviderException("Seerr's media identity conflicts with Jellyfin metadata. Request owners could not be matched.");
            if (media.Requests.Count == 0) return new("notRequested", "No request records in Jellyseerr / Seerr.", 0, 0, []);

            var requesters = media.Requests.Where(r => r.RequestedBy is not null).GroupBy(r => r.RequestedBy!.Id)
                .Select(group =>
                {
                    var requester = group.First().RequestedBy!;
                    if (group.Any(r => r.RequestedBy!.JellyfinUserId != requester.JellyfinUserId))
                        throw new ProviderException("Seerr returned conflicting requester accounts. Viewing progress could not be matched.");
                    // Match accounts only by Jellyfin ID, never by display name or email.
                    var progress = series.Users.FirstOrDefault(u => u.Id == requester.JellyfinUserId);
                    return new SeriesRequester(requester.Id, requester.Name, requester.JellyfinUserId, group.Count(),
                        progress?.Status ?? "unlinked", progress?.WatchedCount, progress is null ? null : series.EpisodeCount);
                }).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            return new("available", null, media.Requests.Count, media.Requests.Count(r => r.RequestedBy is null), requesters);
        }
        catch (ProviderException ex)
        {
            return new("unavailable", ex.Message, 0, 0, []);
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            return new("unavailable", "Jellyseerr / Seerr could not be reached. Request owners are unavailable.", 0, 0, []);
        }
        finally { _requests.Release(); }
    }
}
