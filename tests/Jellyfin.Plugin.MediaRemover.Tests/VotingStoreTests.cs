using System.Text.Json;
using Jellyfin.Plugin.MediaRemover.Voting;
using Xunit;

namespace Jellyfin.Plugin.MediaRemover.Tests;

public sealed class VotingStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "jmr-voting-test-" + Guid.NewGuid().ToString("N"));
    private string StorePath => Path.Combine(_directory, "media-remover", "votes.json");

    [Fact]
    public void VersionOneVotesAndPreferencesRemainReadableWithTheirOriginalIdsAndWireFormat()
    {
        var userId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        var movieId = Guid.NewGuid();
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        File.WriteAllText(StorePath, $$"""
            {"version":1,"votes":[{"seriesId":"{{seriesId}}","userId":"{{userId}}","votedAt":"2026-10-03T12:00:00+00:00"}],
            "preferences":[{"userId":"{{userId}}","homeDismissed":true}]}
            """);
        var store = new VoteStore(StorePath);
        var state = store.Load();
        var original = Assert.Single(state.Votes);
        Assert.Equal(seriesId, original.ItemId);
        store.Save(state with { Votes = [.. state.Votes, new(movieId, userId, original.VotedAt)] });
        var reloaded = new VoteStore(StorePath).Load();
        Assert.Equal([seriesId, movieId], reloaded.Votes.Select(vote => vote.ItemId));
        Assert.Equal(original, reloaded.Votes[0]);
        Assert.True(Assert.Single(reloaded.Preferences).HomeDismissed);
        using var document = JsonDocument.Parse(File.ReadAllText(StorePath));
        Assert.Equal(1, document.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(movieId, document.RootElement.GetProperty("votes")[1].GetProperty("seriesId").GetGuid());
    }

    [Fact]
    public void AtomicallyPersistsAndReloadsVotesAndPreferences()
    {
        var store = new VoteStore(StorePath);
        Assert.Empty(store.Load().Votes);
        var user = Guid.NewGuid();
        var vote = new MediaVote(Guid.NewGuid(), user, DateTimeOffset.UtcNow);
        var state = new VotingState(1, [vote], [new(user, true)]);
        store.Save(state);
        var reloaded = new VoteStore(StorePath).Load();
        Assert.Equal(vote, Assert.Single(reloaded.Votes));
        Assert.Equal(new HomePreference(user, true), Assert.Single(reloaded.Preferences));
        store.Save(state with { Votes = [] });
        Assert.Empty(new VoteStore(StorePath).Load().Votes);
        Assert.Single(Directory.EnumerateFiles(Path.GetDirectoryName(StorePath)!));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{broken")]
    [InlineData("{\"version\":2,\"votes\":[],\"preferences\":[]}")]
    [InlineData("{\"version\":1,\"votes\":null,\"preferences\":[]}")]
    [InlineData("{\"version\":1,\"votes\":[null],\"preferences\":[]}")]
    [InlineData("{\"version\":1,\"votes\":[],\"preferences\":[null]}")]
    public void CorruptOrUnsupportedDataFailsClosedWithoutOverwritingFile(string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        File.WriteAllText(StorePath, contents);
        Assert.Throws<InvalidDataException>(() => new VoteStore(StorePath).Load());
        Assert.Equal(contents, File.ReadAllText(StorePath));
    }

    [Fact]
    public void DuplicateVotesAndInvalidIdentitiesAreRejectedOnLoad()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        var vote = new MediaVote(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow);
        var preference = new HomePreference(vote.UserId, true);
        var states = new VotingState[]
        {
            new(1, [vote, vote], []),
            new(1, [vote with { UserId = Guid.Empty }], []),
            new(1, [vote with { ItemId = Guid.Empty }], []),
            new(1, [vote with { VotedAt = default }], []),
            new(1, [], [preference, preference]),
            new(1, [], [preference with { UserId = Guid.Empty }])
        };
        foreach (var state in states)
        {
            File.WriteAllText(StorePath, JsonSerializer.Serialize(state, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            Assert.Throws<InvalidDataException>(() => new VoteStore(StorePath).Load());
        }
    }

    [Fact]
    public void FailedAtomicReplacementLeavesNoTemporaryFile()
    {
        Directory.CreateDirectory(StorePath);
        Assert.ThrowsAny<IOException>(() => new VoteStore(StorePath).Save(VotingState.Empty));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(StorePath)!));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
