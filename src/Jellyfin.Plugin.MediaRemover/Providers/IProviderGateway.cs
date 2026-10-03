namespace Jellyfin.Plugin.MediaRemover.Providers;

public interface IProviderGateway
{
    Task<SonarrSeries?> FindSonarrAsync(ProviderOptions options, int tvdbId, CancellationToken cancellationToken);

    Task<RadarrMovie?> FindRadarrAsync(ProviderOptions options, int tmdbId, CancellationToken cancellationToken);

    Task<SeerrMedia?> FindSeerrAsync(ProviderOptions options, int tmdbId, CancellationToken cancellationToken);

    Task<SeerrMedia?> FindSeerrMovieAsync(ProviderOptions options, int tmdbId, CancellationToken cancellationToken);

    Task<SeerrRequestInfo?> GetSeerrRequestsAsync(ProviderOptions options, int tmdbId, CancellationToken cancellationToken);

    Task<SeerrRequestInfo?> GetSeerrMovieRequestsAsync(ProviderOptions options, int tmdbId, CancellationToken cancellationToken);

    Task DeleteSonarrAsync(ProviderOptions options, SonarrSeries expected, bool deleteFiles, bool addImportListExclusion, CancellationToken cancellationToken);

    Task DeleteRadarrAsync(ProviderOptions options, RadarrMovie expected, bool deleteFiles, bool addImportListExclusion, CancellationToken cancellationToken);

    Task DeleteSeerrAsync(ProviderOptions options, SeerrMedia expected, CancellationToken cancellationToken);

    Task TestSonarrAsync(ProviderOptions options, CancellationToken cancellationToken);

    Task TestRadarrAsync(ProviderOptions options, CancellationToken cancellationToken);

    Task TestSeerrAsync(ProviderOptions options, CancellationToken cancellationToken);
}
