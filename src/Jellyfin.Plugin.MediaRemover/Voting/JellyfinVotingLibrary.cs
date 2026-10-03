using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.MediaRemover.Core;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;

namespace Jellyfin.Plugin.MediaRemover.Voting;

public sealed class JellyfinVotingLibrary(ILibraryManager library, IUserManager users, IUserDataManager userData) : IVotingLibrary
{
    private static readonly BaseItemKind[] SupportedTypes =
    [
        BaseItemKind.Series, BaseItemKind.Movie, BaseItemKind.Video, BaseItemKind.MusicVideo,
        BaseItemKind.Audio, BaseItemKind.MusicAlbum, BaseItemKind.AudioBook, BaseItemKind.Book,
        BaseItemKind.Photo, BaseItemKind.PhotoAlbum, BaseItemKind.BoxSet, BaseItemKind.Playlist
    ];

    public VotingAccount? GetAccount(Guid userId) => FindEnabled(userId) is { } user ? Account(user) : null;

    public IReadOnlyList<VotingAccount> GetAccounts() => users.GetUsers()
        .Where(u => !u.HasPermission(PermissionKind.IsDisabled)).Select(Account).ToArray();

    public VotingItemsLibraryPage GetItems(Guid userId, string? search, int startIndex, int limit)
    {
        var user = RequireEnabled(userId);
        var selected = new List<BaseItem>(limit);
        var count = 0;
        // Jellyfin's SQL result does not apply playlist shares or all standalone visibility checks.
        // Filter before pagination/counting, using bounded batches and loading progress only for this page.
        const int batchSize = 250;
        for (var offset = 0; ; offset += batchSize)
        {
            var result = library.GetItemsResult(new InternalItemsQuery(user)
            {
                IncludeItemTypes = SupportedTypes, Recursive = true, IsVirtualItem = false,
                SearchTerm = string.IsNullOrWhiteSpace(search) ? null : search.Trim(),
                StartIndex = offset, Limit = batchSize,
                OrderBy = [(ItemSortBy.SortName, SortOrder.Ascending), (ItemSortBy.DateCreated, SortOrder.Ascending)]
            });
            foreach (var item in result.Items.Where(item => IsSupported(item) && item.IsVisibleStandalone(user)))
            {
                if (count >= startIndex && selected.Count < limit) selected.Add(item);
                count++;
            }
            if (result.Items.Count < batchSize || offset + batchSize >= result.TotalRecordCount) break;
        }
        return new(selected.Select(item => WithItemProgress(user, item)).ToArray(), count, startIndex);
    }

    public IReadOnlyList<VotingItemIdentity> GetVisibleItems(Guid userId, IReadOnlyCollection<Guid> itemIds)
    {
        var user = RequireEnabled(userId);
        return FindVisibleItems(user, itemIds).Select(item => Identity(user, item)).ToArray();
    }

    public IReadOnlyList<VotingLibraryItem> GetItems(Guid userId, IReadOnlyCollection<Guid> itemIds)
    {
        var user = RequireEnabled(userId);
        return FindVisibleItems(user, itemIds).Select(item => WithItemProgress(user, item)).ToArray();
    }

    public VotingItemIdentity? ResolveItem(Guid userId, Guid itemId)
    {
        var user = RequireEnabled(userId);
        var item = itemId == Guid.Empty ? null : library.GetItemById<BaseItem>(itemId, user);
        if (item is null || item.IsVirtualItem) return null;
        // The selected episode/season must be accessible before resolving the series. A guessed child ID
        // must not reveal its parent's identity or allow voting through an inaccessible child.
        var seriesId = item switch { Episode episode => episode.SeriesId, Season season => season.SeriesId, _ => Guid.Empty };
        if (item is Episode or Season)
            item = seriesId == Guid.Empty ? null : library.GetItemById<Series>(seriesId, user);
        return item is not null && IsSupported(item) ? Identity(user, item) : null;
    }

    public VotingLibraryPage GetSeries(Guid userId, string? search, int startIndex, int limit)
    {
        var user = RequireEnabled(userId);
        // An unparented user query applies both library access and parental/tag restrictions.
        var result = library.GetItemsResult(new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.Series], Recursive = true, IsVirtualItem = false,
            SearchTerm = string.IsNullOrWhiteSpace(search) ? null : search.Trim(),
            StartIndex = startIndex, Limit = limit, OrderBy = [(ItemSortBy.SortName, SortOrder.Ascending)]
        });
        var series = result.Items.OfType<Series>().Select(item => WithProgress(user, item)).ToArray();
        return new(series, result.TotalRecordCount, startIndex);
    }

    public IReadOnlyList<VotingSeriesIdentity> GetVisibleSeries(Guid userId, IReadOnlyCollection<Guid> seriesIds) =>
        FindVisibleSeries(RequireEnabled(userId), seriesIds).Select(item => new VotingSeriesIdentity(item.Id, item.Name, item.ProductionYear)).ToArray();

    public IReadOnlyList<VotingLibrarySeries> GetSeries(Guid userId, IReadOnlyCollection<Guid> seriesIds)
    {
        var user = RequireEnabled(userId);
        return FindVisibleSeries(user, seriesIds).Select(item => WithProgress(user, item)).ToArray();
    }

    public bool CanViewSeries(Guid userId, Guid seriesId)
    {
        var user = RequireEnabled(userId);
        // ItemIds queries skip Jellyfin's automatic library scoping. This overload explicitly checks
        // standalone visibility, including accessible collection folders and ancestor restrictions.
        return library.GetItemById<Series>(seriesId, user) is { IsVirtualItem: false };
    }

    public IReadOnlyList<VotingSeriesIdentity> GetExistingSeries(IReadOnlyCollection<Guid> seriesIds)
    {
        if (seriesIds.Count == 0) return [];
        // This unscoped lookup is used only after VotingService has checked administrator access.
        return library.GetItemList(new InternalItemsQuery
        {
            ItemIds = seriesIds.ToArray(), IncludeItemTypes = [BaseItemKind.Series], IsVirtualItem = false
        }).OfType<Series>().Select(s => new VotingSeriesIdentity(s.Id, s.Name, s.ProductionYear)).ToArray();
    }

    private IEnumerable<Series> FindVisibleSeries(User user, IReadOnlyCollection<Guid> seriesIds)
    {
        // ItemIds bypass automatic library scoping, so apply standalone visibility explicitly.
        // Chunk IDs to bound query parameters; an empty set must never become an unfiltered query.
        foreach (var ids in seriesIds.Where(id => id != Guid.Empty).Distinct().Chunk(100))
        {
            var items = library.GetItemList(new InternalItemsQuery(user)
            {
                ItemIds = ids, IncludeItemTypes = [BaseItemKind.Series], IsVirtualItem = false
            }).OfType<Series>();
            foreach (var item in items)
                if (item.IsVisibleStandalone(user)) yield return item;
        }
    }

    private IEnumerable<BaseItem> FindVisibleItems(User user, IReadOnlyCollection<Guid> itemIds)
    {
        foreach (var ids in itemIds.Where(id => id != Guid.Empty).Distinct().Chunk(100))
        {
            var items = library.GetItemList(new InternalItemsQuery(user)
            {
                ItemIds = ids, IncludeItemTypes = SupportedTypes, IsVirtualItem = false
            });
            foreach (var item in items)
                if (IsSupported(item) && item.IsVisibleStandalone(user)) yield return item;
        }
    }

    private static bool IsSupported(BaseItem item) => !item.IsVirtualItem
        && Enum.TryParse<BaseItemKind>(item.GetClientTypeName(), out var kind) && SupportedTypes.Contains(kind);

    private static VotingItemIdentity Identity(User user, BaseItem item)
    {
        var parent = item.GetParent();
        var context = parent is MusicAlbum or PhotoAlbum
            && parent.IsVisibleStandalone(user) ? parent.Name : null;
        return new(item.Id, item.Name, item.ProductionYear, item.GetBaseItemKind().ToString(), context);
    }

    private VotingLibraryItem WithItemProgress(User user, BaseItem item)
    {
        var identity = Identity(user, item);
        if (item is Series series)
        {
            var progress = WithProgress(user, series);
            return new(item.Id, item.Name, item.ProductionYear, identity.Type, identity.Context,
                progress.EpisodeCount, progress.WatchedCount, progress.InProgressCount, progress.Status, "episode");
        }
        // Folder APIs include linked children (collections/playlists), unlike a raw ParentId query.
        var children = item is Folder folder
            ? folder.GetRecursiveChildren(user, new InternalItemsQuery(user) { Recursive = true, IsVirtualItem = false }, out _)
                .Where(child => !child.IsFolder && !child.IsVirtualItem
                    && (child is Episode || IsSupported(child)) && child.IsVisibleStandalone(user)).DistinctBy(child => child.Id).ToArray()
            : [item];
        var played = 0;
        var inProgress = 0;
        foreach (var child in children)
        {
            var data = userData.GetUserData(user, child);
            if (data?.Played == true) played++;
            else if (data?.PlaybackPositionTicks > 0) inProgress++;
        }
        var status = children.Length == 0 ? "empty" : played == children.Length ? "watched"
            : played > 0 || inProgress > 0 ? "inProgress" : "unwatched";
        var unit = item.GetBaseItemKind() is BaseItemKind.Audio or BaseItemKind.MusicAlbum ? "track" : "item";
        return new(item.Id, item.Name, item.ProductionYear, identity.Type, identity.Context,
            children.Length, played, inProgress, status, unit);
    }

    private VotingLibrarySeries WithProgress(User user, Series item)
    {
        var episodes = library.GetItemList(new InternalItemsQuery(user)
        {
            ParentId = item.Id, Recursive = true, IncludeItemTypes = [BaseItemKind.Episode], IsVirtualItem = false
        }).OfType<Episode>().Where(e => e.ParentIndexNumber != 0 && e.IsVisibleStandalone(user));
        var progress = WatchProgress.Calculate(episodes.Select(e =>
        {
            var data = userData.GetUserData(user, e);
            return new EpisodeState(e.Id, e.ParentIndexNumber, e.IndexNumber, e.IndexNumberEnd,
                data?.Played ?? false, data?.PlaybackPositionTicks ?? 0, data?.LastPlayedDate);
        }));
        return new(item.Id, item.Name, item.ProductionYear, progress.EpisodeCount,
            progress.WatchedCount, progress.InProgressCount, progress.Status);
    }

    private User? FindEnabled(Guid id)
    {
        if (id == Guid.Empty) return null;
        var user = users.GetUserById(id);
        return user is not null && !user.HasPermission(PermissionKind.IsDisabled) ? user : null;
    }

    private User RequireEnabled(Guid id) => FindEnabled(id)
        ?? throw new RemovalException("Sign in with an enabled Jellyfin user account to vote.", 403);

    private static VotingAccount Account(User user) => new(user.Id, user.Username, user.HasPermission(PermissionKind.IsAdministrator));
}
