using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.MediaRemover.Voting;

public sealed record VotingAccount(Guid Id, string Name, bool IsAdministrator);
public sealed record VotingItemIdentity(Guid Id, string Name, int? ProductionYear, string Type, string? Context);
public sealed record VotingLibraryItem(Guid Id, string Name, int? ProductionYear, string Type, string? Context,
    int ItemCount, int PlayedCount, int InProgressCount, string Status, string ProgressUnit);
public sealed record VotingItemsLibraryPage(IReadOnlyList<VotingLibraryItem> Items, int TotalCount, int StartIndex);
public sealed record VotingItem(Guid Id, string Name, int? ProductionYear, string Type, string? Context,
    int ItemCount, int PlayedCount, int InProgressCount, string Status, string ProgressUnit,
    bool MyVote, int VoteCount, DateTimeOffset? VotedAt);
public sealed record VotingItemsPage(IReadOnlyList<VotingItem> Items, int TotalCount, int StartIndex);
public sealed record ItemVoteResult(Guid ItemId, bool MyVote, int VoteCount, DateTimeOffset? VotedAt);
public sealed record ItemVoteSummary(Guid ItemId, string Name, int? ProductionYear, string Type, string? Context,
    int VoteCount, IReadOnlyList<VotingVoter> Voters);
public sealed record VotingItemsSummary(IReadOnlyList<ItemVoteSummary> Items, int TotalVotes, int TotalItems);

// Legacy series endpoints keep their original response shape for cached clients.
public sealed record VotingSeriesIdentity(Guid Id, string Name, int? ProductionYear);
public sealed record VotingLibrarySeries(Guid Id, string Name, int? ProductionYear, int EpisodeCount,
    int WatchedCount, int InProgressCount, string Status);
public sealed record VotingLibraryPage(IReadOnlyList<VotingLibrarySeries> Items, int TotalCount, int StartIndex);
public sealed record VotingSeries(Guid Id, string Name, int? ProductionYear, int EpisodeCount,
    int WatchedCount, int InProgressCount, string Status, bool MyVote, int VoteCount, DateTimeOffset? VotedAt);
public sealed record VotingPage(IReadOnlyList<VotingSeries> Items, int TotalCount, int StartIndex);
public sealed record VotingPreferences([property: JsonRequired] bool HomeDismissed);
public sealed record VoteUpdate([property: JsonRequired] bool Approved);
public sealed record VoteResult(Guid SeriesId, bool MyVote, int VoteCount, DateTimeOffset? VotedAt);
public sealed record VotingVoter(Guid Id, string Name, DateTimeOffset VotedAt);
public sealed record SeriesVoteSummary(Guid SeriesId, string Name, int? ProductionYear, int VoteCount, IReadOnlyList<VotingVoter> Voters);
public sealed record VotingSummary(IReadOnlyList<SeriesVoteSummary> Series, int TotalVotes, int TotalSeries);

// Retain the v1 on-disk key so upgrading preserves votes/preferences and older builds can still read it.
// Item type always comes from Jellyfin, never from this historical property name.
public sealed record MediaVote([property: JsonPropertyName("seriesId")] Guid ItemId, Guid UserId, DateTimeOffset VotedAt);
public sealed record HomePreference(Guid UserId, bool HomeDismissed);
public sealed record VotingState(int Version, IReadOnlyList<MediaVote> Votes, IReadOnlyList<HomePreference> Preferences)
{
    public static VotingState Empty { get; } = new(1, [], []);
}

public interface IVotingLibrary
{
    // Absent and disabled accounts both return null, including for previously issued sessions.
    VotingAccount? GetAccount(Guid userId);
    IReadOnlyList<VotingAccount> GetAccounts();
    VotingItemsLibraryPage GetItems(Guid userId, string? search, int startIndex, int limit);
    IReadOnlyList<VotingItemIdentity> GetVisibleItems(Guid userId, IReadOnlyCollection<Guid> itemIds);
    IReadOnlyList<VotingLibraryItem> GetItems(Guid userId, IReadOnlyCollection<Guid> itemIds);
    // Episodes and seasons resolve to their series, checking visibility of both the selected item and target.
    VotingItemIdentity? ResolveItem(Guid userId, Guid itemId);
    VotingLibraryPage GetSeries(Guid userId, string? search, int startIndex, int limit);
    // ID-filtered lookups still enforce the viewer's library and parental restrictions.
    IReadOnlyList<VotingSeriesIdentity> GetVisibleSeries(Guid userId, IReadOnlyCollection<Guid> seriesIds);
    IReadOnlyList<VotingLibrarySeries> GetSeries(Guid userId, IReadOnlyCollection<Guid> seriesIds);
    bool CanViewSeries(Guid userId, Guid seriesId);
    IReadOnlyList<VotingSeriesIdentity> GetExistingSeries(IReadOnlyCollection<Guid> seriesIds);
}

public interface IVoteStore
{
    VotingState Load();
    void Save(VotingState state);
}
