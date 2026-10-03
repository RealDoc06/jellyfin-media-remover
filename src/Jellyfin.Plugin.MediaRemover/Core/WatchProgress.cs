namespace Jellyfin.Plugin.MediaRemover.Core;

public sealed record EpisodeState(Guid Id, int? Season, int? Number, int? EndNumber, bool Played, long PositionTicks, DateTime? LastPlayed);
public sealed record WatchSummary(int EpisodeCount, int WatchedCount, int InProgressCount, DateTime? LastPlayed, string Status);

public static class WatchProgress
{
    public static WatchSummary ForItem(bool played, long positionTicks, DateTime? lastPlayed) =>
        new(1, played ? 1 : 0, !played && positionTicks > 0 ? 1 : 0, lastPlayed,
            played ? "watched" : positionTicks > 0 ? "inProgress" : "unwatched");

    // Count logical episodes once across duplicate files; a multi-episode file contributes each episode.
    public static WatchSummary Calculate(IEnumerable<EpisodeState> episodes)
    {
        var logical = new Dictionary<string, (bool Played, bool Started, DateTime? Last)>();
        foreach (var episode in episodes.Where(e => e.Season != 0))
        {
            var count = episode.Number is int start && episode.EndNumber is int end && end >= start
                ? (int)Math.Min((long)end - start + 1, 1000) : 1;
            for (var offset = 0; offset < count; offset++)
            {
                var key = episode.Season.HasValue && episode.Number.HasValue
                    ? $"{episode.Season}:{episode.Number + offset}" : episode.Id.ToString();
                logical.TryGetValue(key, out var old);
                var last = old.Last is null || episode.LastPlayed > old.Last ? episode.LastPlayed : old.Last;
                logical[key] = (old.Played || episode.Played, old.Started || episode.PositionTicks > 0, last);
            }
        }

        var watched = logical.Values.Count(e => e.Played);
        var progress = logical.Values.Count(e => !e.Played && e.Started);
        var status = logical.Count == 0 ? "empty" : watched == logical.Count ? "watched"
            : watched > 0 || progress > 0 ? "inProgress" : "unwatched";
        return new(logical.Count, watched, progress, logical.Values.Select(e => e.Last).DefaultIfEmpty().Max(), status);
    }
}
