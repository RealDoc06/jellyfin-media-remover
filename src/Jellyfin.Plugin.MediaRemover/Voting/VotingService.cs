using Jellyfin.Plugin.MediaRemover.Core;

namespace Jellyfin.Plugin.MediaRemover.Voting;

public sealed class VotingService(IVotingLibrary library, IVoteStore store, TimeProvider clock)
{
    private readonly object _gate = new();
    private VotingState _state = store.Load();

    public VotingItemsPage GetItems(Guid userId, string? search, int startIndex, int limit)
    {
        RequireAccount(userId);
        ValidatePagination(search, startIndex, limit);
        var page = library.GetItems(userId, search, startIndex, limit);
        var votes = GetEnabledVotes();
        return new(page.Items.Select(item => ToVotingItem(item, userId, votes[item.Id])).ToArray(),
            page.TotalCount, page.StartIndex);
    }

    public VotingItem GetItem(Guid userId, Guid itemId)
    {
        RequireAccount(userId);
        var target = RequireItem(userId, itemId);
        var item = library.GetItems(userId, [target.Id]).FirstOrDefault()
            ?? throw new RemovalException("Media not found.", 404);
        return ToVotingItem(item, userId, GetEnabledVotes()[item.Id]);
    }

    public ItemVoteResult SetItemVote(Guid userId, Guid itemId, bool approved)
    {
        RequireAccount(userId);
        var target = RequireItem(userId, itemId);
        return SaveVote(userId, target.Id, approved);
    }

    public VotingPage GetSeries(Guid userId, string? search, int startIndex, int limit)
    {
        RequireAccount(userId);
        ValidatePagination(search, startIndex, limit);
        var page = library.GetSeries(userId, search, startIndex, limit);
        var votes = GetEnabledVotes();
        return new(page.Items.Select(series => ToVotingSeries(series, userId, votes[series.Id])).ToArray(),
            page.TotalCount, page.StartIndex);
    }

    public VotingItemsPage GetItemsHome(Guid userId, int limit)
    {
        RequireAccount(userId);
        if (limit is < 1 or > 10) throw new RemovalException("Home limit must be between 1 and 10.");
        var votes = GetEnabledVotes();
        var candidates = votes.Where(group => group.Any(vote => vote.UserId != userId)).Select(group => group.Key).ToArray();
        if (candidates.Length == 0) return new([], 0, 0);

        // Select from visible, nominated titles before loading progress for the small Home list.
        var visible = library.GetVisibleItems(userId, candidates);
        var selected = visible.OrderByDescending(item => votes[item.Id].Count())
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.Id)
            .Take(limit).Select(item => item.Id).ToArray();
        var items = library.GetItems(userId, selected).ToDictionary(item => item.Id);
        var rows = selected.Where(items.ContainsKey)
            .Select(id => ToVotingItem(items[id], userId, votes[id])).ToArray();
        return new(rows, visible.Count, 0);
    }

    public VotingPage GetHome(Guid userId, int limit)
    {
        RequireAccount(userId);
        if (limit is < 1 or > 10) throw new RemovalException("Home limit must be between 1 and 10.");
        var votes = GetEnabledVotes();
        var candidates = votes.Where(group => group.Any(vote => vote.UserId != userId)).Select(group => group.Key).ToArray();
        if (candidates.Length == 0) return new([], 0, 0);
        var visible = library.GetVisibleSeries(userId, candidates);
        var selected = visible.OrderByDescending(series => votes[series.Id].Count())
            .ThenBy(series => series.Name, StringComparer.OrdinalIgnoreCase).ThenBy(series => series.Id)
            .Take(limit).Select(series => series.Id).ToArray();
        var series = library.GetSeries(userId, selected).ToDictionary(item => item.Id);
        return new(selected.Where(series.ContainsKey).Select(id => ToVotingSeries(series[id], userId, votes[id])).ToArray(),
            visible.Count, 0);
    }

    public VotingSeries GetSeries(Guid userId, Guid seriesId)
    {
        RequireAccount(userId);
        var series = seriesId == Guid.Empty ? null : library.GetSeries(userId, [seriesId]).FirstOrDefault();
        if (series is null) throw new RemovalException("Series not found.", 404);
        return ToVotingSeries(series, userId, GetEnabledVotes()[seriesId]);
    }

    public VoteResult SetVote(Guid userId, Guid seriesId, bool approved)
    {
        RequireAccount(userId);
        // Both adding and withdrawing require the same visibility check; guessed IDs disclose nothing.
        if (seriesId == Guid.Empty || !library.CanViewSeries(userId, seriesId))
            throw new RemovalException("Series not found.", 404);
        var result = SaveVote(userId, seriesId, approved);
        return new(result.ItemId, result.MyVote, result.VoteCount, result.VotedAt);
    }

    private ItemVoteResult SaveVote(Guid userId, Guid itemId, bool approved)
    {
        var enabled = library.GetAccounts().Select(u => u.Id).ToHashSet();
        lock (_gate)
        {
            var own = _state.Votes.FirstOrDefault(v => v.ItemId == itemId && v.UserId == userId);
            if (approved && own is null)
            {
                own = new(itemId, userId, clock.GetUtcNow());
                Commit(_state with { Votes = [.. _state.Votes, own] });
            }
            else if (!approved && own is not null)
            {
                Commit(_state with { Votes = _state.Votes.Where(v => v != own).ToArray() });
                own = null;
            }
            return new(itemId, own is not null, _state.Votes.Count(v => v.ItemId == itemId && enabled.Contains(v.UserId)), own?.VotedAt);
        }
    }

    public VotingPreferences GetPreferences(Guid userId)
    {
        RequireAccount(userId);
        lock (_gate) return new(_state.Preferences.FirstOrDefault(p => p.UserId == userId)?.HomeDismissed ?? false);
    }

    public VotingPreferences SetPreferences(Guid userId, bool homeDismissed)
    {
        RequireAccount(userId);
        lock (_gate)
        {
            var previous = _state.Preferences.FirstOrDefault(p => p.UserId == userId);
            if ((previous?.HomeDismissed ?? false) != homeDismissed)
            {
                var preferences = _state.Preferences.Where(p => p.UserId != userId).ToList();
                // False is the default, so restoring the Home section removes its override.
                if (homeDismissed) preferences.Add(new(userId, true));
                Commit(_state with { Preferences = preferences.ToArray() });
            }
            return new(homeDismissed);
        }
    }

    public VotingSummary GetSummary(Guid administratorId)
    {
        if (!RequireAccount(administratorId).IsAdministrator)
            throw new RemovalException("A Jellyfin administrator account is required.", 403);
        var accounts = library.GetAccounts().ToDictionary(u => u.Id);
        MediaVote[] votes;
        lock (_gate) votes = _state.Votes.Where(v => accounts.ContainsKey(v.UserId)).ToArray();
        var existing = library.GetExistingSeries(votes.Select(v => v.ItemId).Distinct().ToArray()).ToDictionary(s => s.Id);
        var series = votes.Where(v => existing.ContainsKey(v.ItemId)).GroupBy(v => v.ItemId)
            .Select(group => new SeriesVoteSummary(group.Key, existing[group.Key].Name, existing[group.Key].ProductionYear,
                group.Count(), group.OrderBy(v => v.VotedAt)
                .Select(v => new VotingVoter(v.UserId, accounts[v.UserId].Name, v.VotedAt)).ToArray()))
            .OrderByDescending(s => s.VoteCount).ThenBy(s => s.SeriesId).ToArray();
        return new(series, series.Sum(s => s.VoteCount), series.Length);
    }

    public VotingItemsSummary GetItemsSummary(Guid administratorId)
    {
        if (!RequireAccount(administratorId).IsAdministrator)
            throw new RemovalException("A Jellyfin administrator account is required.", 403);
        var accounts = library.GetAccounts().ToDictionary(user => user.Id);
        MediaVote[] votes;
        lock (_gate) votes = _state.Votes.Where(vote => accounts.ContainsKey(vote.UserId)).ToArray();
        // Even administrators must have access to private playlists and restricted libraries.
        var visible = library.GetVisibleItems(administratorId, votes.Select(vote => vote.ItemId).Distinct().ToArray())
            .ToDictionary(item => item.Id);
        var items = votes.Where(vote => visible.ContainsKey(vote.ItemId)).GroupBy(vote => vote.ItemId)
            .Select(group => new ItemVoteSummary(group.Key, visible[group.Key].Name, visible[group.Key].ProductionYear,
                visible[group.Key].Type, visible[group.Key].Context, group.Count(), group.OrderBy(vote => vote.VotedAt)
                    .Select(vote => new VotingVoter(vote.UserId, accounts[vote.UserId].Name, vote.VotedAt)).ToArray()))
            .OrderByDescending(item => item.VoteCount).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ItemId).ToArray();
        return new(items, items.Sum(item => item.VoteCount), items.Length);
    }

    private VotingItemIdentity RequireItem(Guid userId, Guid itemId) =>
        (itemId == Guid.Empty ? null : library.ResolveItem(userId, itemId))
        ?? throw new RemovalException("Media not found.", 404);

    private static void ValidatePagination(string? search, int startIndex, int limit)
    {
        if (startIndex < 0 || limit is < 1 or > 100)
            throw new RemovalException("Invalid pagination. Limit must be between 1 and 100.");
        if (search?.Length > 200) throw new RemovalException("Search must be at most 200 characters.");
    }

    private VotingAccount RequireAccount(Guid id) => id != Guid.Empty && library.GetAccount(id) is { } user
        ? user : throw new RemovalException("Sign in with an enabled Jellyfin user account to vote.", 403);

    private ILookup<Guid, MediaVote> GetEnabledVotes()
    {
        var accounts = library.GetAccounts().Select(user => user.Id).ToHashSet();
        lock (_gate) return _state.Votes.Where(vote => accounts.Contains(vote.UserId)).ToLookup(vote => vote.ItemId);
    }

    private static VotingSeries ToVotingSeries(VotingLibrarySeries series, Guid userId, IEnumerable<MediaVote> votes)
    {
        var own = votes.FirstOrDefault(vote => vote.UserId == userId);
        return new(series.Id, series.Name, series.ProductionYear, series.EpisodeCount, series.WatchedCount,
            series.InProgressCount, series.Status, own is not null, votes.Count(), own?.VotedAt);
    }

    private static VotingItem ToVotingItem(VotingLibraryItem item, Guid userId, IEnumerable<MediaVote> votes)
    {
        var own = votes.FirstOrDefault(vote => vote.UserId == userId);
        return new(item.Id, item.Name, item.ProductionYear, item.Type, item.Context, item.ItemCount, item.PlayedCount,
            item.InProgressCount, item.Status, item.ProgressUnit, own is not null, votes.Count(), own?.VotedAt);
    }

    private void Commit(VotingState next)
    {
        // A failed write leaves the previous in-memory state intact, making retry safe.
        store.Save(next);
        _state = next;
    }
}
