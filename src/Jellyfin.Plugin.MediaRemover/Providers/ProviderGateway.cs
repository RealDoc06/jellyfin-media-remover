using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Jellyfin.Plugin.MediaRemover.Providers;

/// <summary>
/// Uses exact provider identifiers and checks them again before deleting. The MediaRemover
/// HTTP client must have redirects disabled so API keys cannot be forwarded to another host.
/// </summary>
public sealed class ProviderGateway(IHttpClientFactory httpClientFactory) : IProviderGateway
{
    private const string Sonarr = "Sonarr";
    private const string Radarr = "Radarr";
    private const string Seerr = "Jellyseerr / Seerr";

    public async Task<SonarrSeries?> FindSonarrAsync(ProviderOptions options, int tvdbId, CancellationToken cancellationToken)
    {
        RequirePositiveId(tvdbId, Sonarr);
        using var json = await GetJsonAsync(Sonarr, options.SonarrUrl, options.SonarrApiKey,
            $"api/v3/series?tvdbId={Id(tvdbId)}", false, cancellationToken).ConfigureAwait(false);
        var root = json!.RootElement;
        if (root.ValueKind != JsonValueKind.Array)
        {
            throw InvalidResponse(Sonarr);
        }

        // The filtered endpoint returns an empty array for a missing series, never a 404.
        if (root.GetArrayLength() == 0)
        {
            return null;
        }

        if (root.GetArrayLength() != 1)
        {
            throw new ProviderException("Sonarr returned ambiguous series matches. No deletion was performed.");
        }

        var series = ReadSonarr(root[0]);
        if (series.TvdbId != tvdbId)
        {
            throw IdentityChanged(Sonarr);
        }

        return series;
    }

    public async Task<RadarrMovie?> FindRadarrAsync(ProviderOptions options, int tmdbId, CancellationToken cancellationToken)
    {
        RequirePositiveId(tmdbId, Radarr);
        using var json = await GetJsonAsync(Radarr, options.RadarrUrl, options.RadarrApiKey,
            $"api/v3/movie?tmdbId={Id(tmdbId)}", false, cancellationToken).ConfigureAwait(false);
        var root = json!.RootElement;
        if (root.ValueKind != JsonValueKind.Array)
        {
            throw InvalidResponse(Radarr);
        }

        if (root.GetArrayLength() == 0)
        {
            return null;
        }

        if (root.GetArrayLength() != 1)
        {
            throw new ProviderException("Radarr returned ambiguous movie matches. No deletion was performed.");
        }

        var movie = ReadRadarr(root[0]);
        if (movie.TmdbId != tmdbId)
        {
            throw IdentityChanged(Radarr);
        }

        return movie;
    }

    public Task<SeerrMedia?> FindSeerrAsync(ProviderOptions options, int tmdbId, CancellationToken cancellationToken) =>
        FindSeerrMediaAsync(options, tmdbId, "tv", cancellationToken);

    public Task<SeerrMedia?> FindSeerrMovieAsync(ProviderOptions options, int tmdbId, CancellationToken cancellationToken) =>
        FindSeerrMediaAsync(options, tmdbId, "movie", cancellationToken);

    private async Task<SeerrMedia?> FindSeerrMediaAsync(ProviderOptions options, int tmdbId, string mediaType, CancellationToken cancellationToken)
    {
        RequirePositiveId(tmdbId, Seerr);
        using var json = await GetJsonAsync(Seerr, options.SeerrUrl, options.SeerrApiKey,
            $"api/v1/{mediaType}/{Id(tmdbId)}", false, cancellationToken).ConfigureAwait(false);
        var root = json!.RootElement;
        if (ReadId(root, "id", Seerr) != tmdbId)
        {
            throw IdentityChanged(Seerr);
        }

        _ = ReadText(root, mediaType == "tv" ? "name" : "title", Seerr);

        // Catalog details exist even when no local media record exists.
        // A failing metadata request (including 404) must not be treated as local absence.
        if (!root.TryGetProperty("mediaInfo", out var media) || media.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        var result = ReadSeerr(media, mediaType);
        if (result.TmdbId != tmdbId)
        {
            throw IdentityChanged(Seerr);
        }

        return result;
    }

    public Task<SeerrRequestInfo?> GetSeerrRequestsAsync(ProviderOptions options, int tmdbId, CancellationToken cancellationToken) =>
        GetSeerrMediaRequestsAsync(options, tmdbId, "tv", cancellationToken);

    public Task<SeerrRequestInfo?> GetSeerrMovieRequestsAsync(ProviderOptions options, int tmdbId, CancellationToken cancellationToken) =>
        GetSeerrMediaRequestsAsync(options, tmdbId, "movie", cancellationToken);

    private async Task<SeerrRequestInfo?> GetSeerrMediaRequestsAsync(ProviderOptions options, int tmdbId, string mediaType, CancellationToken cancellationToken)
    {
        RequirePositiveId(tmdbId, Seerr);
        using var json = await GetJsonAsync(Seerr, options.SeerrUrl, options.SeerrApiKey,
            $"api/v1/{mediaType}/{Id(tmdbId)}", false, cancellationToken).ConfigureAwait(false);
        var root = json!.RootElement;
        if (ReadId(root, "id", Seerr) != tmdbId)
        {
            throw IdentityChanged(Seerr);
        }

        _ = ReadText(root, mediaType == "tv" ? "name" : "title", Seerr);
        if (!root.TryGetProperty("mediaInfo", out var media) || media.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        // Blocklisting prevents deletion, but does not hide who requested an item.
        var identity = ReadSeerr(media, mediaType, allowBlocklisted: true);
        if (identity.TmdbId != tmdbId)
        {
            throw IdentityChanged(Seerr);
        }

        var requests = media.GetProperty("requests").EnumerateArray().Select(ReadSeerrRequest).ToArray();
        return new SeerrRequestInfo(identity.Id, identity.TmdbId, identity.TvdbId, requests);
    }

    public async Task DeleteSonarrAsync(ProviderOptions options, SonarrSeries expected, bool deleteFiles, bool addImportListExclusion, CancellationToken cancellationToken)
    {
        RequirePositiveId(expected.Id, Sonarr);
        RequirePositiveId(expected.TvdbId, Sonarr);
        if (string.IsNullOrWhiteSpace(expected.Path))
        {
            throw IdentityChanged(Sonarr);
        }

        using var json = await GetJsonAsync(Sonarr, options.SonarrUrl, options.SonarrApiKey,
            $"api/v3/series/{Id(expected.Id)}", true, cancellationToken).ConfigureAwait(false);
        if (json is null)
        {
            return;
        }

        var current = ReadSonarr(json.RootElement);
        if (current.Id != expected.Id || current.TvdbId != expected.TvdbId || current.Path != expected.Path)
        {
            throw IdentityChanged(Sonarr);
        }

        using var response = await SendAsync(Sonarr, options.SonarrUrl, options.SonarrApiKey, HttpMethod.Delete,
            $"api/v3/series/{Id(expected.Id)}?deleteFiles={Boolean(deleteFiles)}&addImportListExclusion={Boolean(addImportListExclusion)}",
            true, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteRadarrAsync(ProviderOptions options, RadarrMovie expected, bool deleteFiles, bool addImportListExclusion, CancellationToken cancellationToken)
    {
        RequirePositiveId(expected.Id, Radarr);
        RequirePositiveId(expected.TmdbId, Radarr);
        if (string.IsNullOrWhiteSpace(expected.Path))
        {
            throw IdentityChanged(Radarr);
        }

        using var json = await GetJsonAsync(Radarr, options.RadarrUrl, options.RadarrApiKey,
            $"api/v3/movie/{Id(expected.Id)}", true, cancellationToken).ConfigureAwait(false);
        if (json is null)
        {
            return;
        }

        var current = ReadRadarr(json.RootElement);
        if (current.Id != expected.Id || current.TmdbId != expected.TmdbId || current.Path != expected.Path)
        {
            throw IdentityChanged(Radarr);
        }

        // Radarr names its exclusion option differently from Sonarr.
        using var response = await SendAsync(Radarr, options.RadarrUrl, options.RadarrApiKey, HttpMethod.Delete,
            $"api/v3/movie/{Id(expected.Id)}?deleteFiles={Boolean(deleteFiles)}&addImportExclusion={Boolean(addImportListExclusion)}",
            true, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteSeerrAsync(ProviderOptions options, SeerrMedia expected, CancellationToken cancellationToken)
    {
        RequirePositiveId(expected.Id, Seerr);
        RequirePositiveId(expected.TmdbId, Seerr);
        if (expected.MediaType is not ("tv" or "movie") || expected.TvdbId is <= 0 || expected.RequestCount < 0)
        {
            throw IdentityChanged(Seerr);
        }

        // Seerr has no GET media/{id}. Re-resolve the TMDB mapping before using its local ID.
        var current = await FindSeerrMediaAsync(options, expected.TmdbId, expected.MediaType, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return;
        }

        if (current.Id != expected.Id || current.TmdbId != expected.TmdbId
            || current.TvdbId != expected.TvdbId || current.MediaType != expected.MediaType)
        {
            throw IdentityChanged(Seerr);
        }

        // This deletes the local record and its requests. /file would trigger another
        // Sonarr/Radarr deletion using Seerr's own server configuration and is deliberately unused.
        using var response = await SendAsync(Seerr, options.SeerrUrl, options.SeerrApiKey, HttpMethod.Delete,
            $"api/v1/media/{Id(expected.Id)}", false, cancellationToken).ConfigureAwait(false);
    }

    public async Task TestSonarrAsync(ProviderOptions options, CancellationToken cancellationToken)
    {
        using var json = await GetJsonAsync(Sonarr, options.SonarrUrl, options.SonarrApiKey,
            "api/v3/system/status", false, cancellationToken).ConfigureAwait(false);
        if (ReadText(json!.RootElement, "appName", Sonarr) != "Sonarr")
        {
            throw InvalidResponse(Sonarr);
        }

        _ = ReadText(json.RootElement, "version", Sonarr);
    }

    public async Task TestRadarrAsync(ProviderOptions options, CancellationToken cancellationToken)
    {
        using var json = await GetJsonAsync(Radarr, options.RadarrUrl, options.RadarrApiKey,
            "api/v3/system/status", false, cancellationToken).ConfigureAwait(false);
        if (ReadText(json!.RootElement, "appName", Radarr) != "Radarr")
        {
            throw InvalidResponse(Radarr);
        }

        _ = ReadText(json.RootElement, "version", Radarr);
    }

    public async Task TestSeerrAsync(ProviderOptions options, CancellationToken cancellationToken)
    {
        using var json = await GetJsonAsync(Seerr, options.SeerrUrl, options.SeerrApiKey,
            "api/v1/auth/me", false, cancellationToken).ConfigureAwait(false);
        _ = ReadId(json!.RootElement, "id", Seerr);
        var root = json.RootElement;
        if (!root.TryGetProperty("permissions", out var permissions) || permissions.ValueKind != JsonValueKind.Number
            || !permissions.TryGetInt64(out var value) || value < 0 || (value & (2 | 16)) == 0)
        {
            throw new ProviderException("Jellyseerr / Seerr requires administrator or Manage Requests permission.");
        }
    }

    private async Task<JsonDocument?> GetJsonAsync(string provider, string url, string key, string path, bool allowNotFound, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(provider, url, key, HttpMethod.Get, path, allowNotFound, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        try
        {
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            throw InvalidResponse(provider);
        }
        catch (IOException)
        {
            throw new ProviderException($"{provider} response could not be read. Check the connection and try again.");
        }
    }

    private async Task<HttpResponseMessage> SendAsync(string provider, string url, string key, HttpMethod method, string path, bool allowNotFound, CancellationToken cancellationToken)
    {
        var uri = BuildUri(provider, url, path);
        if (string.IsNullOrWhiteSpace(key) || key.Any(char.IsControl))
        {
            throw new ProviderException($"{provider} requires a valid API key.");
        }

        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Add("X-Api-Key", key.Trim());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var client = httpClientFactory.CreateClient("MediaRemover");
        client.MaxResponseContentBufferSize = 8 * 1024 * 1024;
        HttpResponseMessage response;
        try
        {
            // Buffer within HttpClient's timeout and size limit, including JSON response bodies.
            response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ProviderException($"{provider} request timed out. Check provider state before retrying a deletion.");
        }
        catch (HttpRequestException)
        {
            throw new ProviderException($"{provider} could not be reached. Check the URL, connection and certificate.");
        }

        if (response.IsSuccessStatusCode || (allowNotFound && response.StatusCode == HttpStatusCode.NotFound))
        {
            return response;
        }

        var status = (int)response.StatusCode;
        response.Dispose();
        throw status switch
        {
            >= 300 and < 400 => new ProviderException($"{provider} returned a redirect. Use its direct URL and correct base path."),
            401 or 403 => new ProviderException($"{provider} rejected authentication or permissions. Check the API key."),
            _ => new ProviderException($"{provider} request failed (HTTP {status}). Check provider state before retrying a deletion."),
        };
    }

    private static Uri BuildUri(string provider, string url, string path)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Any(char.IsControl)
            || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(uri.Host) || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
        {
            throw new ProviderException($"{provider} URL must use HTTP or HTTPS without credentials, query or fragment.");
        }

        return new Uri(new Uri(uri.AbsoluteUri.TrimEnd('/') + "/"), path);
    }

    private static SonarrSeries ReadSonarr(JsonElement value) => new(
        ReadId(value, "id", Sonarr), ReadId(value, "tvdbId", Sonarr),
        ReadText(value, "title", Sonarr), ReadText(value, "path", Sonarr));

    private static RadarrMovie ReadRadarr(JsonElement value) => new(
        ReadId(value, "id", Radarr), ReadId(value, "tmdbId", Radarr),
        ReadText(value, "title", Radarr), ReadText(value, "path", Radarr));

    private static SeerrMedia ReadSeerr(JsonElement value, string expectedMediaType, bool allowBlocklisted = false)
    {
        var id = ReadId(value, "id", Seerr);
        var tmdbId = ReadId(value, "tmdbId", Seerr);
        var type = ReadText(value, "mediaType", Seerr);
        if (type != expectedMediaType)
        {
            throw new ProviderException("Jellyseerr / Seerr returned a different media type. No deletion was performed.");
        }

        // Seerr deliberately preserves blocklisted records and their requests on DELETE.
        if (!allowBlocklisted && value.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.Number
            && status.TryGetInt32(out var statusCode) && statusCode == 6)
        {
            throw new ProviderException("Seerr preserves blocklisted media. Remove this item from its blocklist before deleting its media record.");
        }

        int? tvdbId = null;
        if (value.TryGetProperty("tvdbId", out var tvdb) && tvdb.ValueKind != JsonValueKind.Null)
        {
            tvdbId = ReadId(value, "tvdbId", Seerr);
        }

        if (!value.TryGetProperty("requests", out var requests) || requests.ValueKind != JsonValueKind.Array)
        {
            throw InvalidResponse(Seerr);
        }

        return new SeerrMedia(id, tmdbId, tvdbId, type, requests.GetArrayLength());
    }

    private static SeerrRequest ReadSeerrRequest(JsonElement value)
    {
        var id = ReadId(value, "id", Seerr);
        if (!value.TryGetProperty("requestedBy", out var user) || user.ValueKind == JsonValueKind.Null)
        {
            return new SeerrRequest(id, null);
        }

        var userId = ReadId(user, "id", Seerr);
        Guid? jellyfinUserId = null;
        if (user.TryGetProperty("jellyfinUserId", out var linkedId) && linkedId.ValueKind != JsonValueKind.Null)
        {
            if (linkedId.ValueKind != JsonValueKind.String
                || !(Guid.TryParseExact(linkedId.GetString(), "N", out var parsed)
                    || Guid.TryParseExact(linkedId.GetString(), "D", out parsed))
                || parsed == Guid.Empty)
            {
                throw InvalidResponse(Seerr);
            }

            jellyfinUserId = parsed;
        }

        // Seerr's displayName can fall back to an email address. Keep only public labels;
        // never forward full user objects or infer a Jellyfin account from its username.
        var name = ReadRequesterName(user, "displayName") ?? ReadRequesterName(user, "username")
            ?? ReadRequesterName(user, "jellyfinUsername") ?? $"Seerr user #{Id(userId)}";
        return new SeerrRequest(id, new SeerrRequester(userId, name, jellyfinUserId));
    }

    private static string? ReadRequesterName(JsonElement user, string property)
    {
        if (!user.TryGetProperty(property, out var name) || name.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (name.ValueKind != JsonValueKind.String)
        {
            throw InvalidResponse(Seerr);
        }

        var text = name.GetString()!.Trim();
        return text.Length == 0 || text.Contains('@') ? null : text;
    }

    private static int ReadId(JsonElement value, string property, string provider)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(property, out var item)
            || item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out var result) || result <= 0)
        {
            throw InvalidResponse(provider);
        }

        return result;
    }

    private static string ReadText(JsonElement value, string property, string provider)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(property, out var item)
            || item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
        {
            throw InvalidResponse(provider);
        }

        return item.GetString()!;
    }

    private static void RequirePositiveId(int value, string provider)
    {
        if (value <= 0)
        {
            throw new ProviderException($"{provider} requires a valid external and local media identity.");
        }
    }

    private static string Id(int id) => id.ToString(CultureInfo.InvariantCulture);

    private static string Boolean(bool value) => value ? "true" : "false";

    private static ProviderException InvalidResponse(string provider) => new($"{provider} returned an invalid or incomplete response. No deletion was performed.");

    private static ProviderException IdentityChanged(string provider) => new($"{provider} media identity or path changed. Review a new deletion preview.");
}
