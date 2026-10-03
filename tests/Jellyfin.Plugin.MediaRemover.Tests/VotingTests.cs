using System.Security.Claims;
using System.Text.Json;
using Jellyfin.Plugin.MediaRemover.Api;
using Jellyfin.Plugin.MediaRemover.Core;
using Jellyfin.Plugin.MediaRemover.Voting;
using Xunit;

namespace Jellyfin.Plugin.MediaRemover.Tests;

public sealed class VotingTests
{
    [Theory]
    [InlineData("Movie")]
    [InlineData("Video")]
    [InlineData("MusicVideo")]
    [InlineData("Audio")]
    [InlineData("MusicAlbum")]
    [InlineData("AudioBook")]
    [InlineData("Book")]
    [InlineData("Photo")]
    [InlineData("PhotoAlbum")]
    [InlineData("BoxSet")]
    [InlineData("Playlist")]
    public void MediaVotesAreIndependentPersistentAndExcludedFromLegacySeriesEndpoints(string type)
    {
        var library = new Library();
        var item = library.AddItem(type);
        var store = new MemoryStore();
        var service = new VotingService(library, store, new Clock());
        service.SetVote(library.Other.Id, library.Series.Id, true);
        var first = service.SetItemVote(library.Other.Id, item.Id, true);
        Assert.Equal(first, service.SetItemVote(library.Other.Id, item.Id, true));
        Assert.Equal(2, store.SaveCount);

        var restarted = new VotingService(library, store, new Clock());
        var personal = restarted.GetItem(library.Viewer.Id, item.Id);
        Assert.Equal((type, 1, false), (personal.Type, personal.VoteCount, personal.MyVote));
        Assert.Equal(personal, restarted.GetItemsHome(library.Viewer.Id, 3).Items.Single(row => row.Id == item.Id));
        Assert.Equal(personal, restarted.GetItems(library.Viewer.Id, null, 0, 25).Items.Single(row => row.Id == item.Id));
        var summary = restarted.GetItemsSummary(library.Admin.Id);
        Assert.Equal(2, summary.TotalItems);
        Assert.Equal(type, summary.Items.Single(row => row.ItemId == item.Id).Type);
        Assert.Equal(library.Series.Id, Assert.Single(restarted.GetSummary(library.Admin.Id).Series).SeriesId);
        Assert.Equal(library.Series.Id, Assert.Single(restarted.GetHome(library.Viewer.Id, 3).Items).Id);
        Assert.Equal(404, Assert.Throws<RemovalException>(() => restarted.SetVote(library.Viewer.Id, item.Id, true)).StatusCode);
        Assert.Equal(404, Assert.Throws<RemovalException>(() => restarted.GetSeries(library.Viewer.Id, item.Id)).StatusCode);
        restarted.SetItemVote(library.Other.Id, item.Id, false);
        Assert.False(restarted.GetItem(library.Other.Id, item.Id).MyVote);
        Assert.True(restarted.GetSeries(library.Other.Id, library.Series.Id).MyVote);
    }

    [Fact]
    public void EpisodesAndSeasonsShareTheirSeriesVoteAndRequireBothIdsToBeVisible()
    {
        var library = new Library();
        var episode = Guid.NewGuid();
        var season = Guid.NewGuid();
        library.SeriesChildren.Add(episode, library.Series.Id);
        library.SeriesChildren.Add(season, library.Series.Id);
        var store = new MemoryStore();
        var service = new VotingService(library, store, new Clock());
        var vote = service.SetItemVote(library.Viewer.Id, episode, true);
        Assert.Equal(library.Series.Id, vote.ItemId);
        Assert.Equal(vote, service.SetItemVote(library.Viewer.Id, season, true));
        Assert.Equal(library.Series.Id, Assert.Single(store.State.Votes).ItemId);
        Assert.Equal(library.Series.Id, service.GetItem(library.Viewer.Id, episode).Id);
        service.SetItemVote(library.Viewer.Id, season, false);
        Assert.Empty(store.State.Votes);

        foreach (var hiddenId in new[] { episode, library.Series.Id })
        {
            library.Hidden.Add(hiddenId);
            Assert.Equal(404, Assert.Throws<RemovalException>(() => service.GetItem(library.Viewer.Id, episode)).StatusCode);
            Assert.Equal(404, Assert.Throws<RemovalException>(() => service.SetItemVote(library.Viewer.Id, episode, true)).StatusCode);
            Assert.Equal(404, Assert.Throws<RemovalException>(() => service.SetItemVote(library.Viewer.Id, episode, false)).StatusCode);
            library.Hidden.Remove(hiddenId);
        }
        Assert.Equal(2, store.SaveCount);
    }

    [Fact]
    public void GenericHomeRanksOnlyVisibleNominationsFromEnabledOtherUsersBeforeLimiting()
    {
        var library = new Library();
        var hidden = library.AddItem("Playlist", "A private playlist");
        var self = library.AddItem("Photo", "Self only");
        var inactive = library.AddItem("Book", "Disabled vote");
        var movie = library.AddItem("Movie", "Alpha");
        var album = library.AddItem("MusicAlbum", "Beta");
        var service = new VotingService(library, new MemoryStore(), new Clock());
        foreach (var item in new[] { hidden, movie, album }) service.SetItemVote(library.Other.Id, item.Id, true);
        service.SetItemVote(library.Viewer.Id, hidden.Id, true);
        service.SetItemVote(library.Viewer.Id, self.Id, true);
        service.SetItemVote(library.Admin.Id, inactive.Id, true);
        service.SetItemVote(library.Viewer.Id, album.Id, true);
        library.Disabled.Add(library.Admin.Id);
        library.Hidden.Add(hidden.Id);

        var home = service.GetItemsHome(library.Viewer.Id, 1);
        Assert.Equal(album.Id, Assert.Single(home.Items).Id);
        Assert.Equal(2, home.TotalCount);
        Assert.Equal([album.Id], library.ProgressQueriedIds);
        var json = JsonSerializer.Serialize(home);
        Assert.DoesNotContain(library.Other.Name, json);
        Assert.DoesNotContain(library.Other.Id.ToString(), json);
        Assert.DoesNotContain("Voters", json);
        Assert.DoesNotContain(hidden.Name, json);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RestrictedDeletedAndUnsupportedMediaAreUniformlyUnavailable(bool approved)
    {
        var library = new Library();
        var movie = library.AddItem("Movie");
        var service = new VotingService(library, new MemoryStore(), new Clock());
        service.SetItemVote(library.Other.Id, movie.Id, true);
        library.Hidden.Add(movie.Id);
        foreach (var id in new[] { movie.Id, Guid.NewGuid(), Guid.Empty })
        {
            var read = Assert.Throws<RemovalException>(() => service.GetItem(library.Viewer.Id, id));
            var write = Assert.Throws<RemovalException>(() => service.SetItemVote(library.Viewer.Id, id, approved));
            Assert.Equal((404, "Media not found."), (read.StatusCode, read.Message));
            Assert.Equal((read.StatusCode, read.Message), (write.StatusCode, write.Message));
        }
        Assert.Empty(service.GetItemsSummary(library.Admin.Id).Items);
        Assert.Empty(service.GetItemsHome(library.Viewer.Id, 3).Items);
        library.Hidden.Clear();
        library.Media.Clear();
        Assert.Empty(service.GetItemsSummary(library.Admin.Id).Items);
    }

    [Fact]
    public void RepeatedVotesPreserveTimestampAndWithdrawOnlyTheActorsOwnVote()
    {
        var library = new Library();
        var store = new MemoryStore();
        var clock = new Clock();
        var service = new VotingService(library, store, clock);
        var first = service.SetVote(library.Viewer.Id, library.Series.Id, true);
        clock.Now = clock.Now.AddHours(1);
        Assert.Equal(first, service.SetVote(library.Viewer.Id, library.Series.Id, true));
        Assert.Equal(1, store.SaveCount);
        var second = service.SetVote(library.Other.Id, library.Series.Id, true);
        Assert.Equal(2, second.VoteCount);
        Assert.True(second.VotedAt > first.VotedAt);
        var removed = service.SetVote(library.Viewer.Id, library.Series.Id, false);
        Assert.False(removed.MyVote);
        Assert.Null(removed.VotedAt);
        Assert.Equal(1, removed.VoteCount);
        Assert.Equal(removed, service.SetVote(library.Viewer.Id, library.Series.Id, false));
        Assert.Equal(3, store.SaveCount);
        Assert.Equal(library.Other.Id, Assert.Single(store.State.Votes).UserId);
    }

    [Fact]
    public async Task ConcurrentVotesAreUniqueAndPersistWithoutLosingOtherUsers()
    {
        var library = new Library();
        var store = new MemoryStore();
        var service = new VotingService(library, store, new Clock());
        await Task.WhenAll(Enumerable.Range(0, 100).Select(i => Task.Run(() =>
            service.SetVote(i % 2 == 0 ? library.Viewer.Id : library.Other.Id, library.Series.Id, true))));
        Assert.Equal(2, store.State.Votes.Count);
        Assert.Equal(2, store.SaveCount);
        Assert.Equal(2, service.GetSummary(library.Admin.Id).TotalVotes);
    }

    [Fact]
    public void PersistenceFailureDoesNotPublishVoteOrPreferenceAndRetryWorks()
    {
        var library = new Library();
        var store = new MemoryStore { FailWrites = true };
        var service = new VotingService(library, store, new Clock());
        Assert.Throws<IOException>(() => service.SetVote(library.Viewer.Id, library.Series.Id, true));
        Assert.Throws<IOException>(() => service.SetPreferences(library.Viewer.Id, true));
        Assert.False(Assert.Single(service.GetSeries(library.Viewer.Id, null, 0, 25).Items).MyVote);
        Assert.False(service.GetPreferences(library.Viewer.Id).HomeDismissed);
        Assert.Empty(store.State.Votes);
        store.FailWrites = false;
        service.SetVote(library.Viewer.Id, library.Series.Id, true);
        service.SetPreferences(library.Viewer.Id, true);
        store.FailWrites = true;
        Assert.Throws<IOException>(() => service.SetVote(library.Viewer.Id, library.Series.Id, false));
        Assert.Throws<IOException>(() => service.SetPreferences(library.Viewer.Id, false));
        Assert.True(Assert.Single(service.GetSeries(library.Viewer.Id, null, 0, 25).Items).MyVote);
        Assert.True(service.GetPreferences(library.Viewer.Id).HomeDismissed);
        Assert.Single(store.State.Votes);
    }

    [Fact]
    public void HomeDismissalIsPersonalPersistentAndRestorable()
    {
        var library = new Library();
        var store = new MemoryStore();
        var service = new VotingService(library, store, new Clock());
        Assert.False(service.GetPreferences(library.Viewer.Id).HomeDismissed);
        service.SetPreferences(library.Viewer.Id, false);
        Assert.Equal(0, store.SaveCount);
        service.SetPreferences(library.Viewer.Id, true);
        service.SetPreferences(library.Viewer.Id, true);
        Assert.Equal(1, store.SaveCount);
        Assert.False(service.GetPreferences(library.Other.Id).HomeDismissed);
        var restarted = new VotingService(library, store, new Clock());
        Assert.True(restarted.GetPreferences(library.Viewer.Id).HomeDismissed);
        Assert.False(restarted.SetPreferences(library.Viewer.Id, false).HomeDismissed);
        Assert.Empty(store.State.Preferences);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HiddenOrMissingSeriesReturnsTheSame404WithoutPersistence(bool approved)
    {
        var library = new Library { Visible = false };
        var store = new MemoryStore();
        var service = new VotingService(library, store, new Clock());
        var hidden = Assert.Throws<RemovalException>(() => service.SetVote(library.Viewer.Id, library.Series.Id, approved));
        var missing = Assert.Throws<RemovalException>(() => service.SetVote(library.Viewer.Id, Guid.NewGuid(), approved));
        var empty = Assert.Throws<RemovalException>(() => service.SetVote(library.Viewer.Id, Guid.Empty, approved));
        Assert.Equal(404, hidden.StatusCode);
        Assert.Equal((hidden.StatusCode, hidden.Message), (missing.StatusCode, missing.Message));
        Assert.Equal((hidden.StatusCode, hidden.Message), (empty.StatusCode, empty.Message));
        Assert.Empty(service.GetSeries(library.Viewer.Id, null, 0, 25).Items);
        Assert.Equal(0, store.SaveCount);
    }

    [Fact]
    public void DeletedOrDisabledAccountsCannotUseAnyPersonalOrAdministrativeEndpoint()
    {
        var library = new Library();
        var store = new MemoryStore();
        var service = new VotingService(library, store, new Clock());
        service.SetVote(library.Viewer.Id, library.Series.Id, true);
        library.Disabled.Add(library.Viewer.Id);
        foreach (var id in new[] { library.Viewer.Id, Guid.NewGuid(), Guid.Empty })
        {
            Assert.Equal(403, Assert.Throws<RemovalException>(() => service.SetVote(id, library.Series.Id, true)).StatusCode);
            Assert.Equal(403, Assert.Throws<RemovalException>(() => service.GetSeries(id, null, 0, 25)).StatusCode);
            Assert.Equal(403, Assert.Throws<RemovalException>(() => service.GetSeries(id, library.Series.Id)).StatusCode);
            Assert.Equal(403, Assert.Throws<RemovalException>(() => service.GetHome(id, 3)).StatusCode);
            Assert.Equal(403, Assert.Throws<RemovalException>(() => service.GetPreferences(id)).StatusCode);
            Assert.Equal(403, Assert.Throws<RemovalException>(() => service.SetPreferences(id, true)).StatusCode);
            Assert.Equal(403, Assert.Throws<RemovalException>(() => service.GetSummary(id)).StatusCode);
            Assert.Equal(403, Assert.Throws<RemovalException>(() => service.GetItems(id, null, 0, 25)).StatusCode);
            Assert.Equal(403, Assert.Throws<RemovalException>(() => service.GetItem(id, library.Series.Id)).StatusCode);
            Assert.Equal(403, Assert.Throws<RemovalException>(() => service.SetItemVote(id, library.Series.Id, true)).StatusCode);
            Assert.Equal(403, Assert.Throws<RemovalException>(() => service.GetItemsHome(id, 3)).StatusCode);
            Assert.Equal(403, Assert.Throws<RemovalException>(() => service.GetItemsSummary(id)).StatusCode);
        }
        Assert.Equal(1, store.SaveCount);
        Assert.Equal(0, service.GetSummary(library.Admin.Id).TotalVotes);
        Assert.Equal(0, Assert.Single(service.GetSeries(library.Other.Id, null, 0, 25).Items).VoteCount);
    }

    [Fact]
    public void AdministratorSummaryUsesCurrentNamesAndExcludesMissingSeriesAndInactiveAccounts()
    {
        var library = new Library();
        var store = new MemoryStore();
        var clock = new Clock();
        var service = new VotingService(library, store, clock);
        service.SetVote(library.Viewer.Id, library.Series.Id, true);
        service.SetVote(library.Other.Id, library.Series.Id, true);
        library.Viewer = library.Viewer with { Name = "Renamed viewer" };
        library.Disabled.Add(library.Other.Id);
        var summary = service.GetSummary(library.Admin.Id);
        Assert.Equal((1, 1), (summary.TotalVotes, summary.TotalSeries));
        var series = Assert.Single(summary.Series);
        Assert.Equal((library.Series.Id, library.Series.Name, library.Series.ProductionYear), (series.SeriesId, series.Name, series.ProductionYear));
        var voter = Assert.Single(series.Voters);
        Assert.Equal((library.Viewer.Id, "Renamed viewer", clock.Now), (voter.Id, voter.Name, voter.VotedAt));
        library.SeriesExists = false;
        Assert.Empty(service.GetSummary(library.Admin.Id).Series);
        // Historical votes remain persisted; summary filtering never edits library or voting state.
        Assert.Equal(2, store.State.Votes.Count);
    }

    [Fact]
    public void NonAdministratorCannotReadVoterIdentitiesEvenWhenCallingServiceDirectly()
    {
        var library = new Library();
        var service = new VotingService(library, new MemoryStore(), new Clock());
        Assert.Equal(403, Assert.Throws<RemovalException>(() => service.GetSummary(library.Viewer.Id)).StatusCode);
        Assert.Equal(403, Assert.Throws<RemovalException>(() => service.GetItemsSummary(library.Viewer.Id)).StatusCode);
    }

    [Fact]
    public void PersonalListingContainsOnlyOwnProgressAndAggregatedVotes()
    {
        var library = new Library();
        var service = new VotingService(library, new MemoryStore(), new Clock());
        service.SetVote(library.Other.Id, library.Series.Id, true);
        var page = service.GetSeries(library.Viewer.Id, "Example", 0, 25);
        Assert.Equal(library.Viewer.Id, library.LastQueriedUser);
        var item = Assert.Single(page.Items);
        Assert.False(item.MyVote);
        Assert.Null(item.VotedAt);
        Assert.Equal(1, item.VoteCount);
        Assert.Equal((2, 1, "inProgress"), (item.EpisodeCount, item.WatchedCount, item.Status));
        var json = JsonSerializer.Serialize(page);
        Assert.DoesNotContain(library.Other.Id.ToString(), json);
        Assert.DoesNotContain(library.Other.Name, json);
        Assert.DoesNotContain("Voters", json);
        Assert.DoesNotContain("Users", json);
    }

    [Fact]
    public void HomeRequiresAnEnabledOtherUsersVoteAndNeverLoadsUnnominatedProgress()
    {
        var library = new Library();
        var selfOnly = library.AddSeries("Self only");
        var disabledOnly = library.AddSeries("Disabled only");
        var nominated = library.AddSeries("Nominated");
        var service = new VotingService(library, new MemoryStore(), new Clock());
        service.SetVote(library.Viewer.Id, selfOnly.Id, true);
        service.SetVote(library.Admin.Id, disabledOnly.Id, true);
        service.SetVote(library.Other.Id, nominated.Id, true);
        service.SetVote(library.Viewer.Id, nominated.Id, true);
        service.SetVote(library.Admin.Id, nominated.Id, true);
        library.Disabled.Add(library.Admin.Id);

        var page = service.GetHome(library.Viewer.Id, 3);
        var item = Assert.Single(page.Items);
        Assert.Equal(nominated.Id, item.Id);
        Assert.True(item.MyVote);
        Assert.Equal(2, item.VoteCount);
        Assert.Equal((1, 0), (page.TotalCount, page.StartIndex));
        Assert.Equal([nominated.Id], library.IdentityQueriedIds);
        Assert.Equal([nominated.Id], library.ProgressQueriedIds);
    }

    [Fact]
    public void HomeFiltersVisibilityAndRanksBeforeLimitingProgressQueries()
    {
        var library = new Library();
        var hidden = library.AddSeries("A hidden series");
        var popular = library.AddSeries("Z popular series");
        var firstTie = library.AddSeries("Alpha", Guid.Parse("00000000-0000-0000-0000-000000000001"));
        var secondTie = library.AddSeries("alpha", Guid.Parse("00000000-0000-0000-0000-000000000002"));
        var last = library.AddSeries("Bravo");
        var service = new VotingService(library, new MemoryStore(), new Clock());
        foreach (var series in new[] { hidden, last, secondTie, popular, firstTie })
            service.SetVote(library.Other.Id, series.Id, true);
        service.SetVote(library.Viewer.Id, popular.Id, true);
        service.SetVote(library.Viewer.Id, hidden.Id, true);
        service.SetVote(library.Admin.Id, hidden.Id, true);
        library.Hidden.Add(hidden.Id);

        var page = service.GetHome(library.Viewer.Id, 3);
        Guid[] expected = [popular.Id, firstTie.Id, secondTie.Id];
        Assert.Equal(expected, page.Items.Select(item => item.Id));
        Assert.Equal(4, page.TotalCount);
        Assert.Equal(expected, library.ProgressQueriedIds);
        Assert.Equal(library.Viewer.Id, library.LastQueriedUser);
        Assert.DoesNotContain(hidden.Name, JsonSerializer.Serialize(page));
    }

    [Fact]
    public void HomeAndSingleSeriesExposeOnlyPersonalProgressAndAggregateVotes()
    {
        var library = new Library();
        var service = new VotingService(library, new MemoryStore(), new Clock());
        service.SetVote(library.Other.Id, library.Series.Id, true);
        var home = service.GetHome(library.Viewer.Id, 3);
        var item = service.GetSeries(library.Viewer.Id, library.Series.Id);
        Assert.Equal(Assert.Single(home.Items), item);
        Assert.Equal((2, 1, "inProgress"), (item.EpisodeCount, item.WatchedCount, item.Status));
        Assert.False(item.MyVote);
        Assert.Null(item.VotedAt);
        Assert.Equal(1, item.VoteCount);
        var json = JsonSerializer.Serialize(home);
        Assert.DoesNotContain(library.Other.Id.ToString(), json);
        Assert.DoesNotContain(library.Other.Name, json);
        Assert.DoesNotContain("Voters", json);
        Assert.DoesNotContain("Users", json);
    }

    [Fact]
    public void SingleSeriesReturnsUniform404ForHiddenMissingAndEmptyIds()
    {
        var library = new Library { Visible = false };
        var service = new VotingService(library, new MemoryStore(), new Clock());
        var hidden = Assert.Throws<RemovalException>(() => service.GetSeries(library.Viewer.Id, library.Series.Id));
        var missing = Assert.Throws<RemovalException>(() => service.GetSeries(library.Viewer.Id, Guid.NewGuid()));
        var empty = Assert.Throws<RemovalException>(() => service.GetSeries(library.Viewer.Id, Guid.Empty));
        Assert.Equal(404, hidden.StatusCode);
        Assert.Equal((hidden.StatusCode, hidden.Message), (missing.StatusCode, missing.Message));
        Assert.Equal((hidden.StatusCode, hidden.Message), (empty.StatusCode, empty.Message));
    }

    [Fact]
    public void HomeWithNoNominationsDoesNotQueryTheLibrary()
    {
        var library = new Library();
        var service = new VotingService(library, new MemoryStore(), new Clock());
        service.SetVote(library.Viewer.Id, library.Series.Id, true);
        var home = service.GetHome(library.Viewer.Id, 3);
        Assert.Empty(home.Items);
        Assert.Equal(0, home.TotalCount);
        Assert.Null(library.LastQueriedUser);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public void RejectsInvalidHomeLimitBeforeCallingLibrary(int limit)
    {
        var library = new Library();
        var service = new VotingService(library, new MemoryStore(), new Clock());
        Assert.Equal(400, Assert.Throws<RemovalException>(() => service.GetHome(library.Viewer.Id, limit)).StatusCode);
        Assert.Equal(400, Assert.Throws<RemovalException>(() => service.GetItemsHome(library.Viewer.Id, limit)).StatusCode);
        Assert.Null(library.LastQueriedUser);
    }

    [Theory]
    [InlineData(-1, 25)]
    [InlineData(0, 0)]
    [InlineData(0, 101)]
    public void RejectsInvalidPaginationBeforeCallingLibrary(int start, int limit)
    {
        var library = new Library();
        var service = new VotingService(library, new MemoryStore(), new Clock());
        Assert.Equal(400, Assert.Throws<RemovalException>(() => service.GetSeries(library.Viewer.Id, null, start, limit)).StatusCode);
        Assert.Equal(400, Assert.Throws<RemovalException>(() => service.GetItems(library.Viewer.Id, null, start, limit)).StatusCode);
        Assert.Null(library.LastQueriedUser);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("not-a-guid", true)]
    [InlineData("00000000-0000-0000-0000-000000000000", true)]
    [InlineData("826933cd-692b-4ba4-a17f-e998a50fc7b0", false)]
    public void RejectsServerKeysInvalidClaimsAndUnauthenticatedActors(string? claim, bool authenticated)
    {
        var identity = new ClaimsIdentity(claim is null ? [] : [new Claim("Jellyfin-UserId", claim)], authenticated ? "Jellyfin" : null);
        Assert.Equal(403, Assert.Throws<RemovalException>(() => VotingIdentity.RequireUserId(new(identity))).StatusCode);
    }

    [Fact]
    public void SessionActorIsTakenOnlyFromTheAuthenticatedClaim()
    {
        var id = Guid.NewGuid();
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("Jellyfin-UserId", id.ToString("N"))], "Jellyfin"));
        Assert.Equal(id, VotingIdentity.RequireUserId(principal));
    }

    [Fact]
    public void MutationBodiesRequireAnExplicitBoolean()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<VoteUpdate>("{}", options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<VotingPreferences>("{}", options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<VoteUpdate>("{\"approved\":null}", options));
        Assert.False(JsonSerializer.Deserialize<VoteUpdate>("{\"approved\":false}", options)!.Approved);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class MemoryStore : IVoteStore
    {
        public VotingState State { get; private set; } = VotingState.Empty;
        public int SaveCount { get; private set; }
        public bool FailWrites { get; set; }
        public VotingState Load() => State;
        public void Save(VotingState state)
        {
            if (FailWrites) throw new IOException("Disk unavailable");
            State = state;
            SaveCount++;
        }
    }

    private sealed class Library : IVotingLibrary
    {
        public VotingAccount Admin { get; } = new(Guid.NewGuid(), "Admin", true);
        public VotingAccount Viewer { get; set; } = new(Guid.NewGuid(), "Viewer", false);
        public VotingAccount Other { get; } = new(Guid.NewGuid(), "Other user's private name", false);
        public VotingLibrarySeries Series { get; } = new(Guid.NewGuid(), "Example series", 2026, 2, 1, 0, "inProgress");
        public List<VotingLibrarySeries> AdditionalSeries { get; } = [];
        public List<VotingLibraryItem> Media { get; } = [];
        public Dictionary<Guid, Guid> SeriesChildren { get; } = [];
        public HashSet<Guid> Disabled { get; } = [];
        public HashSet<Guid> Hidden { get; } = [];
        public List<Guid> IdentityQueriedIds { get; } = [];
        public List<Guid> ProgressQueriedIds { get; } = [];
        public bool Visible { get; set; } = true;
        public bool SeriesExists { get; set; } = true;
        public Guid? LastQueriedUser { get; private set; }
        public VotingAccount? GetAccount(Guid userId) => GetAccounts().FirstOrDefault(u => u.Id == userId);
        public IReadOnlyList<VotingAccount> GetAccounts() => new[] { Admin, Viewer, Other }.Where(u => !Disabled.Contains(u.Id)).ToArray();
        private IEnumerable<VotingLibraryItem> AllItems => AllSeries.Where(_ => SeriesExists)
            .Select(series => new VotingLibraryItem(series.Id, series.Name, series.ProductionYear, "Series", null,
                series.EpisodeCount, series.WatchedCount, series.InProgressCount, series.Status, "episode")).Concat(Media);
        public VotingLibraryItem AddItem(string type, string name = "Example media")
        {
            var item = new VotingLibraryItem(Guid.NewGuid(), name, 2026, type, null, 1, 0, 0, "unwatched", "item");
            Media.Add(item);
            return item;
        }
        private bool CanViewItem(Guid userId, Guid itemId) => GetAccount(userId) is not null && Visible
            && !Hidden.Contains(itemId) && AllItems.Any(item => item.Id == itemId);
        public VotingItemsLibraryPage GetItems(Guid userId, string? search, int startIndex, int limit)
        {
            LastQueriedUser = userId;
            var visible = AllItems.Where(item => CanViewItem(userId, item.Id)
                && (string.IsNullOrEmpty(search) || item.Name.Contains(search, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            return new(visible.Skip(startIndex).Take(limit).ToArray(), visible.Length, startIndex);
        }
        public IReadOnlyList<VotingItemIdentity> GetVisibleItems(Guid userId, IReadOnlyCollection<Guid> itemIds)
        {
            LastQueriedUser = userId;
            IdentityQueriedIds.AddRange(itemIds);
            return AllItems.Where(item => itemIds.Contains(item.Id) && CanViewItem(userId, item.Id))
                .Select(item => new VotingItemIdentity(item.Id, item.Name, item.ProductionYear, item.Type, item.Context)).ToArray();
        }
        public IReadOnlyList<VotingLibraryItem> GetItems(Guid userId, IReadOnlyCollection<Guid> itemIds)
        {
            LastQueriedUser = userId;
            ProgressQueriedIds.AddRange(itemIds);
            return AllItems.Where(item => itemIds.Contains(item.Id) && CanViewItem(userId, item.Id)).ToArray();
        }
        public VotingItemIdentity? ResolveItem(Guid userId, Guid itemId)
        {
            if (Hidden.Contains(itemId)) return null;
            var target = SeriesChildren.GetValueOrDefault(itemId, itemId);
            var item = AllItems.FirstOrDefault(item => item.Id == target && CanViewItem(userId, target));
            return item is null ? null : new(item.Id, item.Name, item.ProductionYear, item.Type, item.Context);
        }
        public VotingLibrarySeries AddSeries(string name, Guid? id = null)
        {
            var series = Series with { Id = id ?? Guid.NewGuid(), Name = name };
            AdditionalSeries.Add(series);
            return series;
        }
        private IEnumerable<VotingLibrarySeries> AllSeries => new[] { Series }.Concat(AdditionalSeries);
        public bool CanViewSeries(Guid userId, Guid seriesId) => GetAccount(userId) is not null && Visible && SeriesExists
            && !Hidden.Contains(seriesId) && AllSeries.Any(series => series.Id == seriesId);
        public VotingLibraryPage GetSeries(Guid userId, string? search, int startIndex, int limit)
        {
            LastQueriedUser = userId;
            return CanViewSeries(userId, Series.Id) ? new([Series], 1, startIndex) : new([], 0, startIndex);
        }
        public IReadOnlyList<VotingSeriesIdentity> GetVisibleSeries(Guid userId, IReadOnlyCollection<Guid> seriesIds)
        {
            LastQueriedUser = userId;
            IdentityQueriedIds.AddRange(seriesIds);
            return AllSeries.Where(series => seriesIds.Contains(series.Id) && CanViewSeries(userId, series.Id))
                .Select(series => new VotingSeriesIdentity(series.Id, series.Name, series.ProductionYear)).ToArray();
        }
        public IReadOnlyList<VotingLibrarySeries> GetSeries(Guid userId, IReadOnlyCollection<Guid> seriesIds)
        {
            LastQueriedUser = userId;
            ProgressQueriedIds.AddRange(seriesIds);
            return AllSeries.Where(series => seriesIds.Contains(series.Id) && CanViewSeries(userId, series.Id)).ToArray();
        }
        public IReadOnlyList<VotingSeriesIdentity> GetExistingSeries(IReadOnlyCollection<Guid> seriesIds) => SeriesExists && seriesIds.Contains(Series.Id)
            ? [new(Series.Id, Series.Name, Series.ProductionYear)] : [];
    }
}
