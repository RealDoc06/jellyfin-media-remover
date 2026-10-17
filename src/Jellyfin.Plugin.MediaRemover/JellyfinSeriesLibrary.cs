using System.Globalization;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.MediaRemover.Core;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Jellyfin.Database.Implementations.Enums;

namespace Jellyfin.Plugin.MediaRemover;

public sealed class JellyfinSeriesLibrary(ILibraryManager library, IUserManager users, IUserDataManager userData) : ISeriesLibrary
{
    public IReadOnlyList<LibraryUser> GetUsers() => users.GetUsers().OrderBy(u => u.Username)
        .Select(u => new LibraryUser(u.Id, u.Username)).ToArray();

    public SeriesPage GetSeries(Guid userId, string? search, int startIndex, int limit) =>
        GetPage(userId, search, startIndex, limit, [BaseItemKind.Series]);

    public SeriesPage GetItems(Guid userId, string? search, int startIndex, int limit, ItemSort sort, bool descending,
        IReadOnlyDictionary<Guid, int> voteCounts) =>
        GetPage(userId, search, startIndex, limit, [BaseItemKind.Series, BaseItemKind.Movie], sort, descending, voteCounts);

    private SeriesPage GetPage(Guid userId, string? search, int startIndex, int limit, BaseItemKind[] types,
        ItemSort sort = ItemSort.Name, bool descending = false, IReadOnlyDictionary<Guid, int>? voteCounts = null)
    {
        var accounts = users.GetUsers().OrderBy(u => u.Username).ToArray();
        if (userId != Guid.Empty && accounts.All(u => u.Id != userId)) throw new RemovalException("User not found.", 404);
        if (startIndex < 0 || limit is < 1 or > 100) throw new RemovalException("Invalid pagination. Limit must be between 1 and 100.");
        if (!Enum.IsDefined(sort)) throw new RemovalException("Unknown sort column.");
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = types, Recursive = true, IsVirtualItem = false,
            SearchTerm = string.IsNullOrWhiteSpace(search) ? null : search.Trim(),
            OrderBy = [(ItemSortBy.SortName, sort == ItemSort.Name && descending ? SortOrder.Descending : SortOrder.Ascending)]
        };
        SeriesDetail[] details;
        int total;
        if (sort == ItemSort.Name)
        {
            query.StartIndex = startIndex;
            query.Limit = limit;
            var result = library.GetItemsResult(query);
            details = result.Items.Select(item => Detail(item, accounts)).ToArray();
            total = result.TotalRecordCount;
        }
        else
        {
            // Ranking happens before paging, so stable ordering keeps name order within ties.
            // Vote ranking only needs IDs; progress ranking loads progress for every matching item.
            var items = library.GetItemList(query);
            total = items.Count;
            var status = sort == ItemSort.Watched ? "watched" : "inProgress";
            details = sort == ItemSort.Votes
                ? Rank(items, item => voteCounts?.GetValueOrDefault(item.Id) ?? 0, descending)
                    .Skip(startIndex).Take(limit).Select(item => Detail(item, accounts)).ToArray()
                : Rank(items.Select(item => Detail(item, accounts)), detail => detail.Users.Count(u => u.Status == status), descending)
                    .Skip(startIndex).Take(limit).ToArray();
        }
        var rows = details.Select(detail =>
        {
            var selected = detail.Users.FirstOrDefault(p => p.Id == userId);
            return new SeriesRow(detail.Id, detail.Name, detail.ProductionYear, detail.TvdbId,
                detail.TmdbId, detail.EpisodeCount, selected?.WatchedCount, selected?.InProgressCount,
                selected?.LastPlayed, selected?.Status, detail.Users, detail.Type);
        }).ToArray();
        return new(rows, total, startIndex);
    }

    private static IEnumerable<T> Rank<T>(IEnumerable<T> source, Func<T, int> key, bool descending) =>
        descending ? source.OrderByDescending(key) : source.OrderBy(key);

    public SeriesDetail GetSeries(Guid id)
    {
        if (library.GetItemById(id) is not Series series) throw new RemovalException("Series not found in Jellyfin.", 404);
        return Detail(series, users.GetUsers().OrderBy(u => u.Username).ToArray());
    }

    public SeriesDetail GetItem(Guid id)
    {
        var item = library.GetItemById(id);
        if (item is not Series and not Movie) throw new RemovalException("Movie or series not found in Jellyfin.", 404);
        return Detail(item, users.GetUsers().OrderBy(u => u.Username).ToArray());
    }

    private SeriesDetail Detail(BaseItem item, User[] accounts)
    {
        var episodes = item is Series ? Episodes(item.Id) : [];
        var total = item is Movie ? 1 : WatchProgress.Calculate(episodes.Select(e => State(e, null))).EpisodeCount;
        var progress = accounts.Select(user =>
        {
            var data = item is Movie ? userData.GetUserData(user, item) : null;
            var p = item is Movie
                ? WatchProgress.ForItem(data?.Played ?? false, data?.PlaybackPositionTicks ?? 0, data?.LastPlayedDate)
                : Progress(user, episodes);
            return new UserProgress(user.Id, user.Username, p.WatchedCount, p.InProgressCount, p.LastPlayed, p.Status);
        }).ToArray();
        return new(item.Id, item.Name, item.ProductionYear, ProviderId(item, "Tvdb"), ProviderId(item, "Tmdb"),
            total, progress, item is Movie ? "Movie" : "Series");
    }

    private Episode[] Episodes(Guid seriesId) => library.GetItemList(new InternalItemsQuery
    {
        ParentId = seriesId, Recursive = true, IncludeItemTypes = [BaseItemKind.Episode], IsVirtualItem = false
    }).OfType<Episode>().Where(e => e.ParentIndexNumber != 0).ToArray();

    private WatchSummary Progress(User user, Episode[] episodes) =>
        WatchProgress.Calculate(episodes.Select(e => State(e, userData.GetUserData(user, e))));

    private static EpisodeState State(Episode e, UserItemData? data) => new(e.Id, e.ParentIndexNumber, e.IndexNumber,
        e.IndexNumberEnd, data?.Played ?? false, data?.PlaybackPositionTicks ?? 0, data?.LastPlayedDate);

    private static int? ProviderId(BaseItem item, string provider) =>
        item.ProviderIds.TryGetValue(provider, out var value) && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;
}
