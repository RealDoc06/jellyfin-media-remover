using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.MediaRemover.Core;
using Jellyfin.Plugin.MediaRemover.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaRemover.Tests;

public sealed class WatchProgressTests
{
    [Fact]
    public void EmptyLibraryAndSpecialsOnlyAreNeverWatched()
    {
        Assert.Equal(new WatchSummary(0, 0, 0, null, "empty"), WatchProgress.Calculate([]));
        Assert.Equal(new WatchSummary(0, 0, 0, null, "empty"), WatchProgress.Calculate([Episode(0, 1, played: true)]));
    }

    [Fact]
    public void MixedProgressUsesRegularEpisodesAndLatestPlayback()
    {
        var last = new DateTime(2026, 1, 2, 12, 0, 0, DateTimeKind.Utc);
        var summary = WatchProgress.Calculate([
            Episode(1, 1, played: true) with { LastPlayed = last.AddDays(-1), PositionTicks = 50 },
            Episode(1, 2) with { PositionTicks = 20, LastPlayed = last },
            Episode(1, 3),
            Episode(0, 1, played: true) with { LastPlayed = last.AddDays(1) },
        ]);

        Assert.Equal(new WatchSummary(3, 1, 1, last, "inProgress"), summary);
    }

    [Fact]
    public void MultiEpisodeFilesAndDuplicatesCountLogicalEpisodesOnce()
    {
        var summary = WatchProgress.Calculate([
            Episode(1, 1, played: true) with { EndNumber = 2 },
            Episode(1, 1) with { PositionTicks = 20 },
            Episode(1, 2),
            Episode(2, 1, played: true),
        ]);

        Assert.Equal(new WatchSummary(3, 3, 0, null, "watched"), summary);
    }

    [Fact]
    public void EpisodesWithoutNumbersUseFileIdentityAndRemainUnwatched()
    {
        var first = Episode(null, null);
        var second = Episode(null, null);

        Assert.Equal(new WatchSummary(2, 0, 0, null, "unwatched"), WatchProgress.Calculate([first, first, second]));
    }

    [Fact]
    public void OversizedEpisodeRangeIsCappedWithoutIntegerOverflow()
    {
        var summary = WatchProgress.Calculate([Episode(1, 0, played: true) with { EndNumber = int.MaxValue }]);

        Assert.Equal(new WatchSummary(1000, 1000, 0, null, "watched"), summary);
    }

    [Theory]
    [InlineData(false, 0, 0, 0, "unwatched")]
    [InlineData(false, 50, 0, 1, "inProgress")]
    [InlineData(true, 50, 1, 0, "watched")]
    public void MovieProgressUsesPlayedFlagBeforePlaybackPosition(bool played, long position, int watched, int started, string status)
    {
        var last = new DateTime(2026, 1, 2, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(new WatchSummary(1, watched, started, last, status), WatchProgress.ForItem(played, position, last));
    }

    private static EpisodeState Episode(int? season, int? number, bool played = false) =>
        new(Guid.NewGuid(), season, number, null, played, 0, null);
}

public sealed class RemovalServiceTests
{
    [Fact]
    public async Task PreviewOnlyReadsProvidersAndDescribesDestructiveScope()
    {
        using var fixture = new Fixture();
        fixture.Library.Series = fixture.Library.Series! with
        {
            Users = [new UserProgress(Guid.NewGuid(), "Viewer", 1, 0, null, "inProgress")],
        };

        var preview = await fixture.PreviewAsync();

        Assert.Equal(["sonarr", "seerr"], fixture.Providers.Lookups);
        Assert.Empty(fixture.Providers.Deletions);
        Assert.Empty(await fixture.Service.GetHistoryAsync());
        Assert.False(File.Exists(fixture.Path));
        Assert.Equal(fixture.Options, preview.Options);
        Assert.Equal(fixture.Clock.GetUtcNow().AddMinutes(10), preview.ExpiresAt);
        Assert.Contains(preview.Warnings, warning => warning.Contains("not watched", StringComparison.Ordinal));
        Assert.Contains(preview.Warnings, warning => warning.Contains("including specials", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(false, true, true, false)]
    [InlineData(false, true, false, true)]
    public async Task InvalidOptionsCannotReachProviders(bool sonarr, bool seerr, bool deleteFiles, bool exclude)
    {
        using var fixture = new Fixture();
        var options = new RemovalOptions(fixture.Options.SeriesId, sonarr, seerr, deleteFiles, exclude);

        await Assert.ThrowsAsync<RemovalException>(() => fixture.Service.PreviewAsync(fixture.Owner, options, CancellationToken.None));

        Assert.Empty(fixture.Providers.Lookups);
        Assert.Empty(fixture.Providers.Deletions);
    }

    [Fact]
    public async Task ConflictingProviderMappingFailsBeforeCreatingReview()
    {
        using var fixture = new Fixture();
        fixture.Providers.Seerr = fixture.Providers.Seerr! with { TvdbId = 999 };

        var error = await Assert.ThrowsAsync<RemovalException>(fixture.PreviewAsync);

        Assert.Equal(409, error.StatusCode);
        Assert.Empty(fixture.Providers.Deletions);
        Assert.Empty(await fixture.Service.GetHistoryAsync());
    }

    [Fact]
    public async Task MissingSonarrCannotSilentlySkipRequestedFileDeletion()
    {
        using var fixture = new Fixture();
        fixture.Providers.Sonarr = null;

        var error = await Assert.ThrowsAsync<RemovalException>(fixture.PreviewAsync);

        Assert.Equal(409, error.StatusCode);
        Assert.Empty(fixture.Providers.Deletions);
    }

    [Fact]
    public async Task ExecutionRequiresOriginalAdministratorAndExactTitle()
    {
        using var fixture = new Fixture();
        var preview = await fixture.PreviewAsync();

        var wrongOwner = await Assert.ThrowsAsync<RemovalException>(() => fixture.Service.ExecuteAsync(Guid.NewGuid(), preview.Id, preview.Title));
        var wrongTitle = await Assert.ThrowsAsync<RemovalException>(() => fixture.Service.ExecuteAsync(fixture.Owner, preview.Id, preview.Title.ToLowerInvariant()));

        Assert.Equal(403, wrongOwner.StatusCode);
        Assert.Equal(400, wrongTitle.StatusCode);
        Assert.Empty(fixture.Providers.Deletions);
        Assert.False(File.Exists(fixture.Path));

        var result = await fixture.ExecuteAsync(preview);
        Assert.Equal("completed", result.Status);
    }

    [Fact]
    public async Task ExpiredReviewRequiresAnotherPreview()
    {
        using var fixture = new Fixture();
        var preview = await fixture.PreviewAsync();
        fixture.Clock.Now = preview.ExpiresAt;

        var error = await Assert.ThrowsAsync<RemovalException>(() => fixture.ExecuteAsync(preview));

        Assert.Equal(409, error.StatusCode);
        Assert.Empty(fixture.Providers.Deletions);
    }

    [Fact]
    public async Task ProviderSettingsChangedSincePreviewFailClosed()
    {
        using var fixture = new Fixture();
        var preview = await fixture.PreviewAsync();
        fixture.Settings = fixture.Settings with { SonarrUrl = "https://other-sonarr.test" };

        var error = await Assert.ThrowsAsync<RemovalException>(() => fixture.ExecuteAsync(preview));

        Assert.Equal(409, error.StatusCode);
        Assert.Empty(fixture.Providers.Deletions);
        Assert.False(File.Exists(fixture.Path));
    }

    [Theory]
    [InlineData("title")]
    [InlineData("tvdb")]
    [InlineData("tmdb")]
    public async Task LibraryIdentityChangedSincePreviewFailsClosed(string changed)
    {
        using var fixture = new Fixture();
        var preview = await fixture.PreviewAsync();
        var series = fixture.Library.Series!;
        fixture.Library.Series = changed switch
        {
            "title" => series with { Name = "Different Series" },
            "tvdb" => series with { TvdbId = 999 },
            _ => series with { TmdbId = 999 },
        };

        var error = await Assert.ThrowsAsync<RemovalException>(() => fixture.ExecuteAsync(preview));

        Assert.Equal(409, error.StatusCode);
        Assert.Empty(fixture.Providers.Deletions);
    }

    [Fact]
    public async Task SeerrOnlyRemovalRejectsChangedTvdbMappingEvenWhenTmdbIsUnchanged()
    {
        using var fixture = new Fixture();
        var options = fixture.Options with { RemoveSonarr = false, DeleteFiles = false, AddImportListExclusion = false };
        var preview = await fixture.Service.PreviewAsync(fixture.Owner, options, CancellationToken.None);
        fixture.Library.Series = fixture.Library.Series! with { TvdbId = 999 };

        var error = await Assert.ThrowsAsync<RemovalException>(() => fixture.ExecuteAsync(preview));

        Assert.Equal(409, error.StatusCode);
        Assert.Equal(["seerr"], fixture.Providers.Lookups);
        Assert.Empty(fixture.Providers.Deletions);
        Assert.False(File.Exists(fixture.Path));
    }

    [Fact]
    public async Task ConcurrentDoubleSubmitExecutesEachProviderOnceAndPersistsCheckpoints()
    {
        using var fixture = new Fixture();
        var preview = await fixture.PreviewAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Providers.OnSonarr = async () =>
        {
            var saved = Assert.Single(fixture.Store.Load());
            Assert.Equal("running", saved.Status);
            Assert.False(saved.SonarrDone);
            entered.SetResult();
            await release.Task;
        };
        fixture.Providers.OnSeerr = () =>
        {
            var saved = Assert.Single(fixture.Store.Load());
            Assert.True(saved.SonarrDone);
            Assert.False(saved.SeerrDone);
            return Task.CompletedTask;
        };

        var first = fixture.ExecuteAsync(preview);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = fixture.ExecuteAsync(preview);
        release.SetResult();
        var results = await Task.WhenAll(first, second);

        Assert.All(results, result => Assert.Equal("completed", result.Status));
        Assert.Equal(results[0], results[1]);
        Assert.Equal(["sonarr", "seerr"], fixture.Providers.Deletions);
        Assert.Equal((true, true), fixture.Providers.SonarrFlags);
        Assert.Equal(preview.Sonarr, fixture.Providers.DeletedSonarr);
        Assert.Equal(preview.Seerr, fixture.Providers.DeletedSeerr);
        var persisted = Assert.Single(fixture.Store.Load());
        Assert.True(persisted.SonarrDone && persisted.SeerrDone);
        Assert.Equal("completed", persisted.Status);
        var json = File.ReadAllText(fixture.Path);
        Assert.DoesNotContain(fixture.Settings.SonarrApiKey, json, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Settings.SeerrApiKey, json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PartialFailureRetriesOnlyRemainingProviderAfterRestartAndLibraryRemoval()
    {
        using var fixture = new Fixture();
        var preview = await fixture.PreviewAsync();
        fixture.Providers.OnSeerr = () => throw new ProviderException("Seerr unavailable.");

        var failed = await fixture.ExecuteAsync(preview);
        Assert.Equal("partialFailure", failed.Status);
        Assert.True(failed.SonarrDone);
        Assert.False(failed.SeerrDone);
        Assert.Equal("Seerr unavailable.", failed.Error);
        Assert.Equal(failed, await fixture.ExecuteAsync(preview));
        Assert.Equal(["sonarr", "seerr"], fixture.Providers.Deletions);

        fixture.Library.Series = null;
        fixture.Providers.OnSeerr = null;
        var restarted = fixture.NewService();
        var wrongOwner = await Assert.ThrowsAsync<RemovalException>(() => restarted.RetryAsync(Guid.NewGuid(), preview.Id, preview.Title));
        var wrongTitle = await Assert.ThrowsAsync<RemovalException>(() => restarted.RetryAsync(fixture.Owner, preview.Id, "wrong"));
        Assert.Equal(403, wrongOwner.StatusCode);
        Assert.Equal(400, wrongTitle.StatusCode);

        var completed = await restarted.RetryAsync(fixture.Owner, preview.Id, preview.Title);

        Assert.Equal("completed", completed.Status);
        Assert.Null(completed.Error);
        Assert.Equal(["sonarr", "seerr", "seerr"], fixture.Providers.Deletions);
        Assert.Equal(completed, await restarted.RetryAsync(fixture.Owner, preview.Id, preview.Title));
        Assert.Equal(3, fixture.Providers.Deletions.Count);
        Assert.Equal(completed, Assert.Single(fixture.Store.Load()));
    }

    [Fact]
    public async Task RetryRefusesDifferentProviderSettingsAndFreshExecutionCannotBypassPartialRemoval()
    {
        using var fixture = new Fixture();
        var preview = await fixture.PreviewAsync();
        fixture.Providers.OnSonarr = () => throw new ProviderException("Unavailable.");
        var operation = await fixture.ExecuteAsync(preview);
        Assert.Equal("partialFailure", operation.Status);
        var freshPreview = await fixture.PreviewAsync();

        var blockedNew = await Assert.ThrowsAsync<RemovalException>(() => fixture.ExecuteAsync(freshPreview));
        fixture.Settings = fixture.Settings with { SeerrUrl = "https://different-seerr.test" };
        var blockedRetry = await Assert.ThrowsAsync<RemovalException>(() => fixture.Service.RetryAsync(fixture.Owner, preview.Id, preview.Title));

        Assert.Equal(409, blockedNew.StatusCode);
        Assert.Equal(409, blockedRetry.StatusCode);
        Assert.Equal(["sonarr"], fixture.Providers.Deletions);
    }

    [Fact]
    public async Task RotatedApiKeyCanRecoverPartialRemovalWithoutChangingProviderTarget()
    {
        using var fixture = new Fixture();
        var preview = await fixture.PreviewAsync();
        fixture.Providers.OnSeerr = () => throw new ProviderException("API key revoked.");
        Assert.Equal("partialFailure", (await fixture.ExecuteAsync(preview)).Status);
        fixture.Settings = fixture.Settings with { SeerrApiKey = "replacement-key" };
        fixture.Providers.OnSeerr = null;

        var result = await fixture.NewService().RetryAsync(fixture.Owner, preview.Id, preview.Title);

        Assert.Equal("completed", result.Status);
        Assert.Equal(["sonarr", "seerr", "seerr"], fixture.Providers.Deletions);
    }

    [Fact]
    public async Task HistoryKeepsOldUnfinishedRemovalsDiscoverableAndRetryableBeyondCompletedLimit()
    {
        using var fixture = new Fixture();
        var preview = await fixture.PreviewAsync();
        fixture.Providers.OnSeerr = () => throw new ProviderException("Unavailable.");
        var failed = await fixture.ExecuteAsync(preview);
        var interrupted = failed with
        {
            Id = Guid.NewGuid(),
            Options = failed.Options with { SeriesId = Guid.NewGuid() },
            Status = "running",
            CreatedAt = failed.CreatedAt.AddDays(-1),
        };
        var completed = Enumerable.Range(1, 205).Select(index => failed with
        {
            Id = Guid.NewGuid(),
            Options = failed.Options with { SeriesId = Guid.NewGuid() },
            Status = "completed",
            SeerrDone = true,
            Error = null,
            CreatedAt = failed.CreatedAt.AddMinutes(index),
        }).ToArray();
        fixture.Store.Save([failed, interrupted, .. completed]);

        var restarted = fixture.NewService();
        var history = await restarted.GetHistoryAsync();

        Assert.Equal(202, history.Count);
        Assert.Equal(200, history.Count(operation => operation.Status == "completed"));
        Assert.Equal(failed.Id, history[^2].Id);
        Assert.Equal(interrupted.Id, history[^1].Id);
        Assert.Equal("partialFailure", history[^1].Status);
        Assert.DoesNotContain(history, operation => completed.Take(5).Any(old => old.Id == operation.Id));
        Assert.Equal(completed[^1].Id, history[0].Id);

        fixture.Library.Series = null;
        fixture.Providers.OnSeerr = null;
        var recovered = await restarted.RetryAsync(fixture.Owner, failed.Id, failed.Title);
        Assert.Equal("completed", recovered.Status);
        Assert.Equal(["sonarr", "seerr", "seerr"], fixture.Providers.Deletions);
    }

    [Fact]
    public async Task JournalWriteFailureStopsBeforeFirstProviderMutation()
    {
        using var fixture = new Fixture();
        var preview = await fixture.PreviewAsync();
        Directory.CreateDirectory(fixture.Path + ".tmp");

        var error = await Assert.ThrowsAsync<RemovalException>(() => fixture.ExecuteAsync(preview));

        Assert.Equal(500, error.StatusCode);
        Assert.Empty(fixture.Providers.Deletions);
        Assert.Empty(await fixture.Service.GetHistoryAsync());
        Assert.Empty(fixture.Store.Load());
        Directory.Delete(fixture.Path + ".tmp");
        Assert.Equal("completed", (await fixture.ExecuteAsync(preview)).Status);
    }

    [Fact]
    public async Task FailedCheckpointStopsNextProviderAndAllowsRecoveryWithOrWithoutRestart()
    {
        using var fixture = new Fixture();
        var preview = await fixture.PreviewAsync();
        fixture.Providers.OnSonarr = () =>
        {
            Directory.CreateDirectory(fixture.Path + ".tmp");
            return Task.CompletedTask;
        };

        var error = await Assert.ThrowsAsync<RemovalException>(() => fixture.ExecuteAsync(preview));

        Assert.Equal(500, error.StatusCode);
        Assert.Equal(["sonarr"], fixture.Providers.Deletions);
        Assert.Equal("running", Assert.Single(fixture.Store.Load()).Status);
        var current = Assert.Single(await fixture.Service.GetHistoryAsync());
        Assert.Equal("partialFailure", current.Status);
        Assert.False(current.SonarrDone);
        var restarted = fixture.NewService();
        var interrupted = Assert.Single(await restarted.GetHistoryAsync());
        Assert.Equal("partialFailure", interrupted.Status);
        Assert.False(interrupted.SonarrDone);
        Assert.Contains("Server stopped", interrupted.Error, StringComparison.Ordinal);

        var stillBlocked = await Assert.ThrowsAsync<RemovalException>(() => fixture.Service.RetryAsync(fixture.Owner, preview.Id, preview.Title));
        Assert.Equal(500, stillBlocked.StatusCode);
        Assert.Equal(["sonarr"], fixture.Providers.Deletions);
        Assert.Equal("partialFailure", Assert.Single(await fixture.Service.GetHistoryAsync()).Status);

        Directory.Delete(fixture.Path + ".tmp");
        fixture.Providers.OnSonarr = null;
        fixture.Providers.OnSeerr = () =>
        {
            Assert.True(Assert.Single(fixture.Store.Load()).SonarrDone);
            return Task.CompletedTask;
        };
        var recovered = await fixture.Service.RetryAsync(fixture.Owner, preview.Id, preview.Title);
        Assert.Equal("completed", recovered.Status);
        Assert.Equal(["sonarr", "sonarr", "seerr"], fixture.Providers.Deletions);
    }

    [Fact]
    public void CorruptJournalPreventsServiceFromStartingRemovals()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Path, "[{\"id\":");

        Assert.Throws<InvalidDataException>(fixture.NewService);
        Assert.Empty(fixture.Providers.Deletions);
    }

    [Fact]
    public async Task MoviePreviewUsesRadarrAndMovieNamespaceWithoutMutatingProgress()
    {
        using var fixture = new Fixture();
        var options = fixture.MovieOptions();
        var original = fixture.Library.Series!;
        var preview = await fixture.Service.PreviewAsync(fixture.Owner, options, default);

        Assert.Equal(["radarr", "seerrMovie"], fixture.Providers.Lookups);
        Assert.Null(preview.Sonarr);
        Assert.NotNull(preview.Radarr);
        Assert.Equal("movie", preview.Seerr!.MediaType);
        Assert.Contains(preview.Warnings, w => w.Contains("Radarr will delete", StringComparison.Ordinal));
        Assert.Same(original, fixture.Library.Series);
        Assert.Empty(fixture.Providers.Deletions);
        Assert.Empty(await fixture.Service.GetHistoryAsync());
    }

    [Theory]
    [InlineData("Movie", true, false)]
    [InlineData("Series", false, true)]
    [InlineData("Audio", false, false)]
    public async Task CrossManagerOptionsAreRejectedBeforeProviderRequests(string type, bool sonarr, bool radarr)
    {
        using var fixture = new Fixture();
        var options = fixture.Options with { MediaType = type, RemoveSonarr = sonarr, RemoveRadarr = radarr };
        await Assert.ThrowsAsync<RemovalException>(() => fixture.Service.PreviewAsync(fixture.Owner, options, default));
        Assert.Empty(fixture.Providers.Lookups);
        Assert.Empty(fixture.Providers.Deletions);
    }

    [Fact]
    public async Task LegacySeriesPreviewCannotDeleteMovieAndWrongSeerrNamespaceCannotMatch()
    {
        using var fixture = new Fixture();
        var options = fixture.MovieOptions();
        await Assert.ThrowsAsync<RemovalException>(fixture.PreviewAsync);
        Assert.Empty(fixture.Providers.Lookups);

        fixture.Providers.Seerr = fixture.Providers.Seerr! with { MediaType = "tv" };
        var error = await Assert.ThrowsAsync<RemovalException>(() => fixture.Service.PreviewAsync(fixture.Owner, options, default));
        Assert.Equal(409, error.StatusCode);
        Assert.Empty(fixture.Providers.Deletions);
    }

    [Theory]
    [InlineData("type")]
    [InlineData("tmdb")]
    public async Task ChangedMovieIdentityCannotExecuteAReviewedRemoval(string changed)
    {
        using var fixture = new Fixture();
        var preview = await fixture.Service.PreviewAsync(fixture.Owner, fixture.MovieOptions(), default);
        fixture.Library.Series = changed == "type"
            ? fixture.Library.Series! with { Type = "Series" }
            : fixture.Library.Series! with { TmdbId = 999 };

        var error = await Assert.ThrowsAsync<RemovalException>(() => fixture.ExecuteAsync(preview));
        Assert.Equal(409, error.StatusCode);
        Assert.Empty(fixture.Providers.Deletions);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MovieFileDeletionRequiresMatchedRadarrRecord(bool absent)
    {
        using var fixture = new Fixture();
        var options = fixture.MovieOptions();
        fixture.Providers.Radarr = absent ? null : fixture.Providers.Radarr! with { TmdbId = 999 };
        var error = await Assert.ThrowsAsync<RemovalException>(() => fixture.Service.PreviewAsync(fixture.Owner, options, default));
        Assert.Equal(409, error.StatusCode);
        Assert.Empty(fixture.Providers.Deletions);
    }

    [Fact]
    public async Task MoviePartialRemovalCheckpointsAndRetriesOnlySeerrAfterRestart()
    {
        using var fixture = new Fixture();
        var preview = await fixture.Service.PreviewAsync(fixture.Owner, fixture.MovieOptions(), default);
        fixture.Providers.OnRadarr = () =>
        {
            var saved = Assert.Single(fixture.Store.Load());
            Assert.False(saved.RadarrDone);
            Assert.True(saved.SonarrDone);
            return Task.CompletedTask;
        };
        fixture.Providers.OnSeerr = () =>
        {
            Assert.True(Assert.Single(fixture.Store.Load()).RadarrDone);
            throw new ProviderException("Seerr unavailable.");
        };
        var failed = await fixture.ExecuteAsync(preview);
        Assert.Equal("partialFailure", failed.Status);
        Assert.True(failed.RadarrDone);
        Assert.False(failed.SeerrDone);
        Assert.Equal((true, true), fixture.Providers.RadarrFlags);
        Assert.Equal(["radarr", "seerr"], fixture.Providers.Deletions);

        fixture.Library.Series = null;
        fixture.Providers.OnSeerr = null;
        fixture.Settings = fixture.Settings with { RadarrUrl = "https://wrong-radarr.test" };
        await Assert.ThrowsAsync<RemovalException>(() => fixture.NewService().RetryAsync(fixture.Owner, preview.Id, preview.Title));
        fixture.Settings = fixture.Settings with { RadarrUrl = "https://radarr.test", RadarrApiKey = "rotated-key" };
        var completed = await fixture.NewService().RetryAsync(fixture.Owner, preview.Id, preview.Title);
        Assert.Equal("completed", completed.Status);
        Assert.Equal(["radarr", "seerr", "seerr"], fixture.Providers.Deletions);
        Assert.Equal(completed, Assert.Single(fixture.Store.Load()));
    }

    [Fact]
    public async Task FailedMovieCheckpointStopsSeerrUntilExplicitRetry()
    {
        using var fixture = new Fixture();
        var preview = await fixture.Service.PreviewAsync(fixture.Owner, fixture.MovieOptions(), default);
        fixture.Providers.OnRadarr = () =>
        {
            Directory.CreateDirectory(fixture.Path + ".tmp");
            return Task.CompletedTask;
        };
        var error = await Assert.ThrowsAsync<RemovalException>(() => fixture.ExecuteAsync(preview));
        Assert.Equal(500, error.StatusCode);
        Assert.Equal(["radarr"], fixture.Providers.Deletions);
        Assert.False(Assert.Single(fixture.Store.Load()).RadarrDone);
        Directory.Delete(fixture.Path + ".tmp");
        fixture.Providers.OnRadarr = null;
        Assert.Equal("completed", (await fixture.NewService().RetryAsync(fixture.Owner, preview.Id, preview.Title)).Status);
        Assert.Equal(["radarr", "radarr", "seerr"], fixture.Providers.Deletions);
    }

    [Fact]
    public async Task LegacyUnfinishedJournalRemainsRetryableAfterAddingRadarr()
    {
        using var fixture = new Fixture();
        var preview = await fixture.PreviewAsync();
        fixture.Providers.OnSeerr = () => throw new ProviderException("Unavailable.");
        await fixture.ExecuteAsync(preview);
        // Write the exact old shape with no movie fields, preserving the original two-target fingerprint.
        var entries = JsonNode.Parse(File.ReadAllText(fixture.Path))!.AsArray();
        var operation = entries[0]!.AsObject();
        operation.Remove("radarr");
        operation.Remove("radarrDone");
        operation["status"] = "running";
        operation["options"]!.AsObject().Remove("removeRadarr");
        operation["options"]!.AsObject().Remove("mediaType");
        operation["settingsFingerprint"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { fixture.Settings.SonarrUrl, fixture.Settings.SeerrUrl }))));
        File.WriteAllText(fixture.Path, entries.ToJsonString());
        fixture.Settings = fixture.Settings with { RadarrUrl = "https://new-radarr.test", RadarrApiKey = "new-secret" };
        fixture.Library.Series = null;
        fixture.Providers.OnSeerr = null;

        var restarted = fixture.NewService();
        var loaded = Assert.Single(await restarted.GetHistoryAsync());
        Assert.Equal("Series", loaded.Options.MediaType);
        Assert.False(loaded.Options.RemoveRadarr);
        Assert.True(loaded.RadarrDone);
        Assert.Null(loaded.Radarr);
        Assert.Equal("completed", (await restarted.RetryAsync(fixture.Owner, preview.Id, preview.Title)).Status);
        Assert.Equal(["sonarr", "seerr", "seerr"], fixture.Providers.Deletions);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "jmr-core-tests-" + Guid.NewGuid().ToString("N"));

        public Fixture()
        {
            Directory.CreateDirectory(_directory);
            Path = System.IO.Path.Combine(_directory, "operations.json");
            Store = new OperationStore(Path);
            Options = new RemovalOptions(Library.Series!.Id, true, true, true, true);
            Service = NewService();
        }

        public Guid Owner { get; } = Guid.NewGuid();
        public string Path { get; }
        public OperationStore Store { get; }
        public FakeLibrary Library { get; } = new();
        public FakeProviders Providers { get; } = new();
        public FakeClock Clock { get; } = new();
        public ProviderOptions Settings { get; set; } = new("https://sonarr.test", "sonarr-secret", "https://seerr.test", "seerr-secret");
        public RemovalOptions Options { get; }
        public RemovalService Service { get; }

        public RemovalOptions MovieOptions()
        {
            Library.Series = Library.Series! with { Type = "Movie", Name = "Example Movie", TvdbId = null, EpisodeCount = 1,
                Users = [new(Guid.NewGuid(), "Viewer", 1, 0, null, "watched")] };
            Providers.Seerr = Providers.Seerr! with { MediaType = "movie", TvdbId = null };
            Settings = Settings with { RadarrUrl = "https://radarr.test", RadarrApiKey = "radarr-secret" };
            return Options with { MediaType = "Movie", RemoveSonarr = false, RemoveRadarr = true };
        }

        public RemovalService NewService() => new(Library, Providers, () => Settings, Store, NullLogger<RemovalService>.Instance, Clock);
        public Task<RemovalPreview> PreviewAsync() => Service.PreviewAsync(Owner, Options, CancellationToken.None);
        public Task<RemovalOperation> ExecuteAsync(RemovalPreview preview) => Service.ExecuteAsync(Owner, preview.Id, preview.Title);
        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }

    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeLibrary : ISeriesLibrary
    {
        public SeriesDetail? Series { get; set; } = new(Guid.NewGuid(), "Example Series", 2024, 123, 456, 2,
            [new UserProgress(Guid.NewGuid(), "Viewer", 2, 0, null, "watched")]);

        public IReadOnlyList<LibraryUser> GetUsers() => throw new NotSupportedException();
        public SeriesPage GetSeries(Guid userId, string? search, int startIndex, int limit) => throw new NotSupportedException();
        public SeriesDetail GetSeries(Guid id) => Series is { Type: "Series" } series && series.Id == id
            ? series : throw new RemovalException("Series no longer exists.", 404);
        public SeriesPage GetItems(Guid userId, string? search, int startIndex, int limit) => throw new NotSupportedException();
        public SeriesDetail GetItem(Guid id) => Series is { } item && item.Id == id
            ? item : throw new RemovalException("Item no longer exists.", 404);
    }

    private sealed class FakeProviders : IProviderGateway
    {
        public Task<SeerrRequestInfo?> GetSeerrRequestsAsync(ProviderOptions options, int tmdbId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<SeerrRequestInfo?> GetSeerrMovieRequestsAsync(ProviderOptions options, int tmdbId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public RadarrMovie? Radarr { get; set; } = new(56, 456, "Example Movie", "/movies/Example Movie");
        public Func<Task>? OnRadarr { get; set; }
        public (bool DeleteFiles, bool Exclude)? RadarrFlags { get; private set; }

        public SonarrSeries? Sonarr { get; set; } = new(12, 123, "Example Series", "/tv/Example Series");
        public SeerrMedia? Seerr { get; set; } = new(34, 456, 123, "tv", 1);
        public List<string> Lookups { get; } = [];
        public List<string> Deletions { get; } = [];
        public Func<Task>? OnSonarr { get; set; }
        public Func<Task>? OnSeerr { get; set; }
        public (bool DeleteFiles, bool Exclude)? SonarrFlags { get; private set; }
        public SonarrSeries? DeletedSonarr { get; private set; }
        public SeerrMedia? DeletedSeerr { get; private set; }

        public Task<SonarrSeries?> FindSonarrAsync(ProviderOptions options, int tvdbId, CancellationToken cancellationToken)
        {
            Assert.Equal(123, tvdbId);
            Lookups.Add("sonarr");
            return Task.FromResult(Sonarr);
        }

        public Task<SeerrMedia?> FindSeerrAsync(ProviderOptions options, int tmdbId, CancellationToken cancellationToken)
        {
            Assert.Equal(456, tmdbId);
            Lookups.Add("seerr");
            return Task.FromResult(Seerr);
        }

        public Task<RadarrMovie?> FindRadarrAsync(ProviderOptions options, int tmdbId, CancellationToken cancellationToken)
        {
            Assert.Equal(456, tmdbId);
            Lookups.Add("radarr");
            return Task.FromResult(Radarr);
        }

        public Task<SeerrMedia?> FindSeerrMovieAsync(ProviderOptions options, int tmdbId, CancellationToken cancellationToken)
        {
            Assert.Equal(456, tmdbId);
            Lookups.Add("seerrMovie");
            return Task.FromResult(Seerr);
        }

        public async Task DeleteRadarrAsync(ProviderOptions options, RadarrMovie expected, bool deleteFiles, bool addImportListExclusion, CancellationToken cancellationToken)
        {
            Assert.False(cancellationToken.CanBeCanceled);
            Deletions.Add("radarr");
            RadarrFlags = (deleteFiles, addImportListExclusion);
            if (OnRadarr is not null) await OnRadarr();
        }

        public async Task DeleteSonarrAsync(ProviderOptions options, SonarrSeries expected, bool deleteFiles, bool addImportListExclusion, CancellationToken cancellationToken)
        {
            Assert.False(cancellationToken.CanBeCanceled);
            Deletions.Add("sonarr");
            DeletedSonarr = expected;
            SonarrFlags = (deleteFiles, addImportListExclusion);
            if (OnSonarr is not null) await OnSonarr();
        }

        public async Task DeleteSeerrAsync(ProviderOptions options, SeerrMedia expected, CancellationToken cancellationToken)
        {
            Assert.False(cancellationToken.CanBeCanceled);
            Deletions.Add("seerr");
            DeletedSeerr = expected;
            if (OnSeerr is not null) await OnSeerr();
        }

        public Task TestRadarrAsync(ProviderOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task TestSonarrAsync(ProviderOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task TestSeerrAsync(ProviderOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}

public sealed class OperationStoreTests
{
    [Fact]
    public void InterruptedSavePreservesLastCompleteJournalAndCanBeRetried()
    {
        var directory = Path.Combine(Path.GetTempPath(), "jmr-journal-tests-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "operations.json");
        try
        {
            var store = new OperationStore(path);
            var original = Operation();
            store.Save([original]);
            var savedBytes = File.ReadAllBytes(path);

            Assert.Throws<IOException>(() => store.Save(InterruptedWrite(original)));

            Assert.Equal(savedBytes, File.ReadAllBytes(path));
            Assert.Equal(original, Assert.Single(new OperationStore(path).Load()));
            var completed = original with { Status = "completed", SonarrDone = true, SeerrDone = true };
            store.Save([completed]);
            Assert.Equal(completed, Assert.Single(new OperationStore(path).Load()));
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static IEnumerable<RemovalOperation> InterruptedWrite(RemovalOperation operation)
    {
        yield return operation with { Status = "completed" };
        throw new IOException("Simulated interrupted write.");
    }

    private static RemovalOperation Operation()
    {
        var now = DateTimeOffset.UtcNow;
        return new RemovalOperation(Guid.NewGuid(), Guid.NewGuid(), "Example", "settings-fingerprint",
            new RemovalOptions(Guid.NewGuid(), true, false, false, false),
            new SonarrSeries(12, 123, "Example", "/tv/Example"), null, "running", false, true, null, now, now);
    }
}
