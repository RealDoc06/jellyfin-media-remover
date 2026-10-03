using System.Text.Json;
using Jellyfin.Plugin.MediaRemover.Core;
using Jellyfin.Plugin.MediaRemover.Providers;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaRemover.Api;

[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("MediaRemover")]
[TypeFilter(typeof(MediaRemoverExceptionFilter))]
public sealed class MediaRemoverController(SettingsService settings, ISeriesLibrary library,
    IProviderGateway providers, RemovalService removals, SeriesRequestService requests) : ControllerBase
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [HttpGet("Settings")]
    public IActionResult GetSettings() => Json(settings.GetView());

    [HttpPut("Settings")]
    public IActionResult SaveSettings([FromBody] SettingsUpdate update) => Json(settings.Save(update));

    [HttpPost("Connections/Test")]
    public async Task<IActionResult> TestConnections(CancellationToken ct)
    {
        var options = settings.GetOptions();
        var sonarr = await Test(options.SonarrUrl, options.SonarrApiKey, () => providers.TestSonarrAsync(options, ct)).ConfigureAwait(false);
        var seerr = await Test(options.SeerrUrl, options.SeerrApiKey, () => providers.TestSeerrAsync(options, ct)).ConfigureAwait(false);
        var radarr = await Test(options.RadarrUrl, options.RadarrApiKey, () => providers.TestRadarrAsync(options, ct)).ConfigureAwait(false);
        return Json(new { sonarr, seerr, radarr });
    }

    [HttpGet("Users")]
    public IActionResult GetUsers() => Json(library.GetUsers());

    [HttpGet("Series")]
    public IActionResult GetSeries([FromQuery] Guid userId, [FromQuery] string? search, [FromQuery] int startIndex = 0, [FromQuery] int limit = 25) =>
        Json(library.GetSeries(userId, search, startIndex, limit));

    [HttpGet("Series/{id:guid}")]
    public IActionResult GetSeries([FromRoute] Guid id) => Json(library.GetSeries(id));

    [HttpGet("Series/{id:guid}/Requests")]
    public async Task<IActionResult> GetRequests([FromRoute] Guid id, CancellationToken ct) =>
        Json(await requests.GetAsync(id, ct).ConfigureAwait(false));

    [HttpGet("Items")]
    public IActionResult GetItems([FromQuery] Guid userId, [FromQuery] string? search, [FromQuery] int startIndex = 0, [FromQuery] int limit = 25) =>
        Json(library.GetItems(userId, search, startIndex, limit));

    [HttpGet("Items/{id:guid}")]
    public IActionResult GetItem([FromRoute] Guid id) => Json(library.GetItem(id));

    [HttpGet("Items/{id:guid}/Requests")]
    public async Task<IActionResult> GetItemRequests([FromRoute] Guid id, CancellationToken ct) =>
        Json(await requests.GetItemAsync(id, ct).ConfigureAwait(false));

    [HttpPost("Removals/Preview")]
    public async Task<IActionResult> Preview([FromBody] RemovalOptions options, CancellationToken ct) =>
        Json(await removals.PreviewAsync(OwnerId(), options, ct).ConfigureAwait(false));

    [HttpPost("Removals/{id:guid}/Execute")]
    public async Task<IActionResult> Execute([FromRoute] Guid id, [FromBody] Confirmation confirmation) =>
        Json(PublicOperation(await removals.ExecuteAsync(OwnerId(), id, confirmation.ConfirmationTitle).ConfigureAwait(false)));

    [HttpPost("Removals/{id:guid}/Retry")]
    public async Task<IActionResult> Retry([FromRoute] Guid id, [FromBody] Confirmation confirmation) =>
        Json(PublicOperation(await removals.RetryAsync(OwnerId(), id, confirmation.ConfirmationTitle).ConfigureAwait(false)));

    [HttpGet("Removals")]
    public async Task<IActionResult> History() => Json((await removals.GetHistoryAsync().ConfigureAwait(false)).Select(PublicOperation));

    private Guid OwnerId()
    {
        // Require a real administrator session for mutations; server API keys do not identify an accountable user.
        var claim = User.FindFirst("Jellyfin-UserId")?.Value;
        return Guid.TryParse(claim, out var id) && id != Guid.Empty ? id
            : throw new RemovalException("Sign in with a Jellyfin administrator account to remove media.", 403);
    }

    private static object PublicOperation(RemovalOperation o) => new
    {
        o.Id, o.Title, o.Options, o.Sonarr, o.Seerr, o.Radarr, o.Status, o.SonarrDone, o.SeerrDone, o.RadarrDone,
        o.Error, o.CreatedAt, o.UpdatedAt
    };

    private static JsonResult Json(object value) => new(value, JsonOptions);

    private static async Task<ConnectionResult> Test(string url, string key, Func<Task> test)
    {
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(key)) return new(false, false, "Set a URL and API key.");
        try { await test().ConfigureAwait(false); return new(true, true, "Connected"); }
        catch (Exception ex) when (ex is ProviderException or HttpRequestException or OperationCanceledException)
        { return new(true, false, ex is ProviderException ? ex.Message : "Connection failed or timed out."); }
    }
}

public sealed record Confirmation(string ConfirmationTitle);
public sealed record ConnectionResult(bool Configured, bool Ok, string Message);

public sealed class MediaRemoverExceptionFilter(ILogger<MediaRemoverExceptionFilter> logger) : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        var (status, message) = context.Exception switch
        {
            RemovalException e => (e.StatusCode, e.Message),
            ProviderException e => (502, e.Message),
            OperationCanceledException => (504, "Provider request timed out or was cancelled."),
            HttpRequestException => (502, "Provider connection failed."),
            _ => (500, "The operation could not be completed. Check the server logs and removal history before retrying.")
        };
        // Do not log provider exception bodies, request URLs, or credentials.
        logger.LogWarning("Media Remover request failed with status {Status}; exception type {ExceptionType}", status, context.Exception.GetType().Name);
        context.Result = new JsonResult(new { message }, MediaRemoverController.JsonOptions) { StatusCode = status };
        context.ExceptionHandled = true;
    }
}
