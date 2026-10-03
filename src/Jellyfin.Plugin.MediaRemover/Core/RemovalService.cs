using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.MediaRemover.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaRemover.Core;

/// <summary>Serializes removals and persists each checkpoint so partial provider failures can be retried.</summary>
public sealed class RemovalService
{
    private readonly ISeriesLibrary _library;
    private readonly IProviderGateway _providers;
    private readonly Func<ProviderOptions> _settings;
    private readonly OperationStore _store;
    private readonly ILogger<RemovalService> _logger;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, (RemovalPreview Preview, Guid Owner, string Fingerprint)> _previews = [];
    private readonly Dictionary<Guid, RemovalOperation> _operations;

    public RemovalService(ISeriesLibrary library, IProviderGateway providers, Func<ProviderOptions> settings,
        OperationStore store, ILogger<RemovalService> logger, TimeProvider clock)
    {
        _library = library;
        _providers = providers;
        _settings = settings;
        _store = store;
        _logger = logger;
        _clock = clock;
        _operations = store.Load().ToDictionary(o => o.Id, o => o.Status == "running"
            ? o with { Status = "partialFailure", Error = "Server stopped during removal. Retry to reconcile the remaining provider steps." } : o);
    }

    public async Task<RemovalPreview> PreviewAsync(Guid owner, RemovalOptions options, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = _clock.GetUtcNow();
            foreach (var id in _previews.Where(p => p.Value.Preview.ExpiresAt <= now).Select(p => p.Key).ToArray()) _previews.Remove(id);
            if (_previews.Count >= 100) throw new RemovalException("Too many pending reviews. Wait for an existing review to expire.", 429);
            ValidateOptions(options);
            var item = _library.GetItem(options.SeriesId);
            if (item.Type != options.MediaType)
                throw new RemovalException("Media type does not match this Jellyfin item. Create a new review.", 409);
            var movie = item.Type == "Movie";
            var manager = movie ? "Radarr" : "Sonarr";
            var settings = _settings();
            if (options.RemoveSonarr && item.TvdbId is null) throw new RemovalException("This series has no TVDB ID. Fix its Jellyfin metadata first.");
            if ((options.RemoveSeerr || options.RemoveRadarr) && item.TmdbId is null)
                throw new RemovalException("This item has no TMDB ID. Fix its Jellyfin metadata first.");
            var sonarr = options.RemoveSonarr ? await _providers.FindSonarrAsync(settings, item.TvdbId!.Value, ct).ConfigureAwait(false) : null;
            var radarr = options.RemoveRadarr ? await _providers.FindRadarrAsync(settings, item.TmdbId!.Value, ct).ConfigureAwait(false) : null;
            var seerr = !options.RemoveSeerr ? null : movie
                ? await _providers.FindSeerrMovieAsync(settings, item.TmdbId!.Value, ct).ConfigureAwait(false)
                : await _providers.FindSeerrAsync(settings, item.TmdbId!.Value, ct).ConfigureAwait(false);
            if (!MatchesIdentity(item, sonarr, radarr, seerr))
                throw new RemovalException("Provider identity conflicts with this Jellyfin item. Fix the provider mapping before removal.", 409);
            if (sonarr is null && radarr is null && seerr is null)
                throw new RemovalException("No matching records exist in the selected providers.", 409);
            var warnings = new List<string>();
            if (item.Users.Any(u => u.Status != "watched")) warnings.Add(movie
                ? "Some users have not watched this movie." : "Some users have not watched all available regular episodes.");
            if (options.DeleteFiles) warnings.Add(movie
                ? "Radarr will delete the movie and its files."
                : "Sonarr will delete the entire series and its files, including specials and episodes outside this Jellyfin library.");
            else if (options.RemoveSonarr || options.RemoveRadarr)
                warnings.Add($"Files will remain on disk and in Jellyfin. Only the {manager} record will be removed.");
            if (options.RemoveSeerr) warnings.Add("The Seerr media record and all its current requests will be cleared. Remaining files may be rediscovered on the next scan.");
            if (options.RemoveSonarr && sonarr is null) warnings.Add("No matching Sonarr record exists; this step will be skipped.");
            if (options.RemoveRadarr && radarr is null) warnings.Add("No matching Radarr record exists; this step will be skipped.");
            if (options.RemoveSeerr && seerr is null) warnings.Add("No matching Seerr record exists; this step will be skipped.");
            if (options.DeleteFiles && sonarr is null && radarr is null)
                throw new RemovalException($"Cannot delete files because the item is missing from {manager}.", 409);
            var preview = new RemovalPreview(Guid.NewGuid(), item.Id, item.Name, now.AddMinutes(10), options, sonarr, seerr, warnings, radarr);
            _previews.Add(preview.Id, (preview, owner, Fingerprint(settings)));
            return preview;
        }
        finally { _gate.Release(); }
    }

    public async Task<RemovalOperation> ExecuteAsync(Guid owner, Guid id, string title)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // A repeated submit returns the existing result; only an explicit retry repeats incomplete steps.
            if (_operations.TryGetValue(id, out var existing))
            {
                CheckOwnerAndTitle(owner, title, existing.OwnerId, existing.Title);
                return existing;
            }
            if (!_previews.TryGetValue(id, out var entry) || entry.Preview.ExpiresAt <= _clock.GetUtcNow())
                throw new RemovalException("This review expired or the server restarted. Create a new removal preview.", 409);
            CheckOwnerAndTitle(owner, title, entry.Owner, entry.Preview.Title);
            var settings = _settings();
            if (entry.Fingerprint != Fingerprint(settings)) throw new RemovalException("Provider settings changed. Create a new preview.", 409);
            var current = _library.GetItem(entry.Preview.SeriesId);
            if (current.Name != entry.Preview.Title || current.Type != entry.Preview.Options.MediaType
                || !MatchesIdentity(current, entry.Preview.Sonarr, entry.Preview.Radarr, entry.Preview.Seerr))
                throw new RemovalException("Media metadata changed. Create a new preview.", 409);
            if (_operations.Values.Any(o => o.Options.SeriesId == current.Id && o.Status != "completed"))
                throw new RemovalException("This item has an unfinished removal. Retry it from removal history.", 409);
            var now = _clock.GetUtcNow();
            var operation = new RemovalOperation(id, owner, current.Name, TargetsFingerprint(settings, entry.Preview.Options.MediaType), entry.Preview.Options,
                entry.Preview.Sonarr, entry.Preview.Seerr, "running", entry.Preview.Sonarr is null,
                entry.Preview.Seerr is null, null, now, now, entry.Preview.Radarr, entry.Preview.Radarr is null);
            Save(operation);
            _previews.Remove(id);
            return await RunAsync(operation, settings).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MarkInterruptedWrites();
            throw new RemovalException("Cannot save removal history. Further provider steps stopped. Restore write access, then retry the review or unfinished history entry.", 500);
        }
        finally { _gate.Release(); }
    }

    public async Task<RemovalOperation> RetryAsync(Guid owner, Guid id, string title)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_operations.TryGetValue(id, out var operation)) throw new RemovalException("Removal operation not found.", 404);
            CheckOwnerAndTitle(owner, title, operation.OwnerId, operation.Title);
            if (operation.Status == "completed") return operation;
            var settings = _settings();
            if (operation.SettingsFingerprint != TargetsFingerprint(settings, operation.Options.MediaType))
                throw new RemovalException("Restore the provider settings used for this removal before retrying.", 409);
            return await RunAsync(operation, settings).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MarkInterruptedWrites();
            throw new RemovalException("Cannot save removal history. Further provider steps stopped. Restore write access, then retry the review or unfinished history entry.", 500);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<RemovalOperation>> GetHistoryAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return _operations.Values.Where(o => o.Status != "completed")
                .Concat(_operations.Values.Where(o => o.Status == "completed").OrderByDescending(o => o.CreatedAt).Take(200))
                .OrderByDescending(o => o.CreatedAt).ToArray();
        }
        finally { _gate.Release(); }
    }

    private async Task<RemovalOperation> RunAsync(RemovalOperation operation, ProviderOptions settings)
    {
        operation = operation with { Status = "running", Error = null };
        Save(operation);
        try
        {
            // Do not tie mutations to an HTTP client's lifetime: every successful step must be journaled.
            if (!operation.SonarrDone && operation.Sonarr is { } sonarr)
            {
                await _providers.DeleteSonarrAsync(settings, sonarr, operation.Options.DeleteFiles,
                    operation.Options.AddImportListExclusion, CancellationToken.None).ConfigureAwait(false);
                operation = operation with { SonarrDone = true };
                Save(operation);
            }
            if (!operation.RadarrDone && operation.Radarr is { } radarr)
            {
                await _providers.DeleteRadarrAsync(settings, radarr, operation.Options.DeleteFiles,
                    operation.Options.AddImportListExclusion, CancellationToken.None).ConfigureAwait(false);
                operation = operation with { RadarrDone = true };
                Save(operation);
            }
            if (!operation.SeerrDone && operation.Seerr is { } seerr)
            {
                await _providers.DeleteSeerrAsync(settings, seerr, CancellationToken.None).ConfigureAwait(false);
                operation = operation with { SeerrDone = true };
                Save(operation);
            }
            operation = operation with { Status = "completed", Error = null };
        }
        catch (Exception ex) when (ex is ProviderException or HttpRequestException or OperationCanceledException)
        {
            operation = operation with { Status = "partialFailure", Error = ex is ProviderException ? ex.Message
                : "Provider communication failed. Check connections, then retry the remaining steps." };
        }
        operation = operation with { UpdatedAt = _clock.GetUtcNow() };
        Save(operation);
        _logger.LogInformation("Media Remover operation {OperationId} by {AdminId}: {Status}, Sonarr complete: {SonarrDone}, Radarr complete: {RadarrDone}, Seerr complete: {SeerrDone}",
            operation.Id, operation.OwnerId, operation.Status, operation.SonarrDone, operation.RadarrDone, operation.SeerrDone);
        return operation;
    }

    private void Save(RemovalOperation operation)
    {
        operation = operation with { UpdatedAt = _clock.GetUtcNow() };
        // Preserve old memory state if the disk write fails. Never mutate another provider after a failed checkpoint.
        var next = _operations.Values.Where(o => o.Id != operation.Id).Append(operation).ToArray();
        _store.Save(next);
        _operations[operation.Id] = operation;
    }

    private void MarkInterruptedWrites()
    {
        // The durable checkpoint remains conservative. Expose a retry in this process too;
        // after restart, the same running checkpoint is reconciled by the constructor.
        foreach (var operation in _operations.Values.Where(o => o.Status == "running").ToArray())
            _operations[operation.Id] = operation with { Status = "partialFailure",
                Error = "History could not be saved. Restore write access, then retry to reconcile provider state.", UpdatedAt = _clock.GetUtcNow() };
    }

    private static void CheckOwnerAndTitle(Guid owner, string title, Guid expectedOwner, string expectedTitle)
    {
        if (owner != expectedOwner) throw new RemovalException("Only the administrator who reviewed this removal can execute or retry it.", 403);
        if (!string.Equals(title, expectedTitle, StringComparison.Ordinal)) throw new RemovalException("Type the exact title to confirm removal.");
    }

    private static string Fingerprint(ProviderOptions options) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(options))));

    private static void ValidateOptions(RemovalOptions options)
    {
        if (options.MediaType is not ("Series" or "Movie"))
            throw new RemovalException("Provider removal supports movies and series.");
        if (options.MediaType == "Movie" && options.RemoveSonarr || options.MediaType == "Series" && options.RemoveRadarr)
            throw new RemovalException("Use Sonarr for series and Radarr for movies.");
        if (!options.RemoveSonarr && !options.RemoveRadarr && !options.RemoveSeerr)
            throw new RemovalException("Select at least one provider.");
        if ((options.DeleteFiles || options.AddImportListExclusion) && !options.RemoveSonarr && !options.RemoveRadarr)
            throw new RemovalException("File deletion and import exclusion require removal from the download manager.");
    }

    private static bool MatchesIdentity(SeriesDetail item, SonarrSeries? sonarr, RadarrMovie? radarr, SeerrMedia? seerr) =>
        (sonarr is null || item.Type == "Series" && sonarr.TvdbId == item.TvdbId)
        && (radarr is null || item.Type == "Movie" && radarr.TmdbId == item.TmdbId)
        && (seerr is null || seerr.MediaType == (item.Type == "Movie" ? "movie" : "tv") && seerr.TmdbId == item.TmdbId
            && (item.Type == "Movie" || seerr.TvdbId is not > 0 || item.TvdbId is not > 0 || seerr.TvdbId == item.TvdbId));

    // Preserve the exact v1 series fingerprint so unfinished journals survive adding Radarr.
    // Each operation binds only to the manager appropriate for its media type.
    private static string TargetsFingerprint(ProviderOptions options, string mediaType) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(mediaType == "Movie"
            ? JsonSerializer.Serialize(new { options.RadarrUrl, options.SeerrUrl })
            : JsonSerializer.Serialize(new { options.SonarrUrl, options.SeerrUrl }))));
}
