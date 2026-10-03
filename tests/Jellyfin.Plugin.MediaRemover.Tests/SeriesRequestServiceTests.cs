using Jellyfin.Plugin.MediaRemover.Core;
using Jellyfin.Plugin.MediaRemover.Providers;
using Xunit;

namespace Jellyfin.Plugin.MediaRemover.Tests;

public sealed class SeriesRequestServiceTests
{
    [Fact]
    public async Task GroupsRequestersAndMatchesViewingProgressByIdIncludingDifferentDisplayNames()
    {
        var library = new Library();
        var providers = new Providers
        {
            Media = new(7, 456, 123,
            [
                new(1, new(10, "Seerr alias", library.WatchedId)),
                new(2, new(10, "Seerr alias", library.WatchedId)),
                new(3, new(20, "Viewer", library.ViewerId)),
                new(4, null)
            ])
        };
        var result = await Service(library, providers).GetAsync(library.Series.Id, default);

        Assert.Equal("available", result.State);
        Assert.Equal(4, result.RequestCount);
        Assert.Equal(1, result.UnknownRequesterCount);
        var watched = Assert.Single(result.Requesters, r => r.Id == 10);
        Assert.Equal(("Seerr alias", 2, "watched", 2, 2),
            (watched.Name, watched.RequestCount, watched.WatchStatus, watched.WatchedCount, watched.EpisodeCount));
        var viewer = Assert.Single(result.Requesters, r => r.Id == 20);
        Assert.Equal(("inProgress", 1), (viewer.WatchStatus, viewer.WatchedCount));
        Assert.Equal(1, providers.Calls);
    }

    [Fact]
    public async Task MatchingNamesDoNotLinkUnrelatedAccounts()
    {
        var library = new Library();
        var providers = new Providers
        {
            Media = new(7, 456, 123, [new(1, new(10, "Watched", Guid.NewGuid())), new(2, new(20, "Viewer", null))])
        };
        var result = await Service(library, providers).GetAsync(library.Series.Id, default);

        Assert.All(result.Requesters, requester =>
        {
            Assert.Equal("unlinked", requester.WatchStatus);
            Assert.Null(requester.WatchedCount);
            Assert.Null(requester.EpisodeCount);
        });
    }

    [Theory]
    [InlineData(true, false, "notConfigured")]
    [InlineData(false, true, "missingMetadata")]
    public async Task ExplainsUnavailablePrerequisitesWithoutCallingProviders(bool unconfigured, bool missingMetadata, string state)
    {
        var library = new Library();
        if (missingMetadata) library.Series = library.Series with { TmdbId = null };
        var providers = new Providers();
        var service = Service(library, providers, unconfigured ? Options with { SeerrApiKey = "" } : Options);
        var result = await service.GetAsync(library.Series.Id, default);

        Assert.Equal(state, result.State);
        Assert.NotEmpty(result.Message!);
        Assert.Equal(0, providers.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ADefinitivelyAbsentMediaOrEmptyRequestsReportsNoRequests(bool missingMedia)
    {
        var library = new Library();
        var result = await Service(library, new Providers { Media = missingMedia ? null : new(7, 456, 123, []) })
            .GetAsync(library.Series.Id, default);

        Assert.Equal("notRequested", result.State);
        Assert.Empty(result.Requesters);
    }

    [Fact]
    public async Task ProviderFailureNeverClaimsNobodyRequestedTheSeries()
    {
        var library = new Library();
        var providers = new Providers { Read = _ => throw new ProviderException("Seerr returned HTTP 503.") };
        var result = await Service(library, providers).GetAsync(library.Series.Id, default);

        Assert.Equal("unavailable", result.State);
        Assert.Contains("503", result.Message);
        Assert.Empty(result.Requesters);
        Assert.Equal("watched", library.Series.Users[0].Status);
    }

    [Theory]
    [InlineData(999, 123)]
    [InlineData(456, 999)]
    public async Task CrossProviderIdentityConflictsHideUnreliableRequesterData(int tmdb, int tvdb)
    {
        var library = new Library();
        var providers = new Providers { Media = new(7, tmdb, tvdb, [new(1, new(10, "Wrong series owner", library.WatchedId))]) };
        var result = await Service(library, providers).GetAsync(library.Series.Id, default);

        Assert.Equal("unavailable", result.State);
        Assert.Empty(result.Requesters);
    }

    [Fact]
    public async Task ConflictingLinksForTheSameRequesterAreNotGuessed()
    {
        var library = new Library();
        var providers = new Providers { Media = new(7, 456, 123,
            [new(1, new(10, "Person", library.WatchedId)), new(2, new(10, "Person", library.ViewerId))]) };
        var result = await Service(library, providers).GetAsync(library.Series.Id, default);

        Assert.Equal("unavailable", result.State);
        Assert.Empty(result.Requesters);
    }

    [Fact]
    public async Task LimitsParallelRequestsAndReleasesCapacityAfterQueuedCancellation()
    {
        var library = new Library();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inFlight = 0;
        var providers = new Providers
        {
            Read = async ct =>
            {
                var current = Interlocked.Increment(ref inFlight);
                Assert.InRange(current, 1, 4);
                if (current == 4) ready.TrySetResult();
                await release.Task.WaitAsync(ct);
                Interlocked.Decrement(ref inFlight);
                return null;
            }
        };
        var service = Service(library, providers);
        var first = Enumerable.Range(0, 4).Select(_ => service.GetAsync(library.Series.Id, default)).ToArray();
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancelled = new CancellationTokenSource();
        var queued = service.GetAsync(library.Series.Id, cancelled.Token);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.Equal(4, providers.Calls);
        release.SetResult();
        await Task.WhenAll(first);
        Assert.Equal("notRequested", (await service.GetAsync(library.Series.Id, default)).State);
        Assert.Equal(5, providers.Calls);
        Assert.Equal(0, inFlight);
    }

    [Fact]
    public async Task MovieRequestersUseMovieNamespaceAndMoviePlaybackState()
    {
        var library = new Library();
        library.Series = library.Series with { Type = "Movie", TvdbId = null, EpisodeCount = 1,
            Users = [new(library.WatchedId, "Watched", 1, 0, null, "watched")] };
        var providers = new Providers
        {
            Media = new(8, 456, null, [new(1, new(10, "Seerr requester", library.WatchedId))])
        };
        var result = await Service(library, providers).GetItemAsync(library.Series.Id, default);
        Assert.Equal("available", result.State);
        var requester = Assert.Single(result.Requesters);
        Assert.Equal(("watched", 1, 1), (requester.WatchStatus, requester.WatchedCount, requester.EpisodeCount));
        Assert.Equal(1, providers.MovieCalls);
        Assert.Equal(0, providers.Calls);
    }

    [Fact]
    public async Task MovieRequestersRejectMismatchedTmdbIdentity()
    {
        var library = new Library();
        library.Series = library.Series with { Type = "Movie" };
        var providers = new Providers { Media = new(8, 999, null, [new(1, new(10, "Wrong owner", library.WatchedId))]) };
        var result = await Service(library, providers).GetItemAsync(library.Series.Id, default);
        Assert.Equal("unavailable", result.State);
        Assert.Empty(result.Requesters);
        Assert.Equal(1, providers.MovieCalls);
        Assert.Equal(0, providers.Calls);
    }

    [Fact]
    public async Task LegacySeriesRequestsDoNotResolveMovies()
    {
        var library = new Library();
        library.Series = library.Series with { Type = "Movie" };
        var providers = new Providers();
        await Assert.ThrowsAsync<RemovalException>(() => Service(library, providers).GetAsync(library.Series.Id, default));
        Assert.Equal(0, providers.Calls + providers.MovieCalls);
    }

    private static readonly ProviderOptions Options = new("", "", "https://seerr.test", "secret");
    private static SeriesRequestService Service(Library library, Providers providers, ProviderOptions? options = null) =>
        new(library, providers, () => options ?? Options);

    private sealed class Library : ISeriesLibrary
    {
        public Guid WatchedId { get; } = Guid.NewGuid();
        public Guid ViewerId { get; } = Guid.NewGuid();
        public SeriesDetail Series { get; set; }

        public Library() => Series = new(Guid.NewGuid(), "Example", 2026, 123, 456, 2,
            [new(WatchedId, "Watched", 2, 0, null, "watched"), new(ViewerId, "Viewer", 1, 0, null, "inProgress")]);

        public SeriesDetail GetSeries(Guid id) => id == Series.Id && Series.Type == "Series" ? Series : throw new RemovalException("Unknown series", 404);
        public SeriesDetail GetItem(Guid id) => id == Series.Id ? Series : throw new RemovalException("Unknown item", 404);
        public SeriesPage GetItems(Guid userId, string? search, int startIndex, int limit) => throw new NotSupportedException();
        public IReadOnlyList<LibraryUser> GetUsers() => throw new NotSupportedException();
        public SeriesPage GetSeries(Guid userId, string? search, int startIndex, int limit) => throw new NotSupportedException();
    }

    private sealed class Providers : IProviderGateway
    {
        private int _calls;
        public int Calls => _calls;
        private int _movieCalls;
        public int MovieCalls => _movieCalls;
        public SeerrRequestInfo? Media { get; init; }
        public Func<CancellationToken, Task<SeerrRequestInfo?>>? Read { get; init; }
        public Task<SeerrRequestInfo?> GetSeerrRequestsAsync(ProviderOptions options, int tmdbId, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Read is not null ? Read(ct) : Task.FromResult(Media);
        }
        public Task<SeerrRequestInfo?> GetSeerrMovieRequestsAsync(ProviderOptions options, int tmdbId, CancellationToken ct)
        {
            Interlocked.Increment(ref _movieCalls);
            return Read is not null ? Read(ct) : Task.FromResult(Media);
        }
        public Task<RadarrMovie?> FindRadarrAsync(ProviderOptions options, int tmdbId, CancellationToken ct) => throw new NotSupportedException();
        public Task<SeerrMedia?> FindSeerrMovieAsync(ProviderOptions options, int tmdbId, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteRadarrAsync(ProviderOptions options, RadarrMovie expected, bool deleteFiles, bool addImportListExclusion, CancellationToken ct) => throw new NotSupportedException();
        public Task TestRadarrAsync(ProviderOptions options, CancellationToken ct) => throw new NotSupportedException();
        public Task<SonarrSeries?> FindSonarrAsync(ProviderOptions options, int tvdbId, CancellationToken ct) => throw new NotSupportedException();
        public Task<SeerrMedia?> FindSeerrAsync(ProviderOptions options, int tmdbId, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteSonarrAsync(ProviderOptions options, SonarrSeries expected, bool deleteFiles, bool addImportListExclusion, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteSeerrAsync(ProviderOptions options, SeerrMedia expected, CancellationToken ct) => throw new NotSupportedException();
        public Task TestSonarrAsync(ProviderOptions options, CancellationToken ct) => throw new NotSupportedException();
        public Task TestSeerrAsync(ProviderOptions options, CancellationToken ct) => throw new NotSupportedException();
    }
}
