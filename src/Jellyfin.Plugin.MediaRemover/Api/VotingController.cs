using System.Security.Claims;
using System.Text.Json;
using Jellyfin.Plugin.MediaRemover.Core;
using Jellyfin.Plugin.MediaRemover.Voting;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaRemover.Api;

[ApiController]
[Authorize]
[Route("MediaRemover/Voting")]
[TypeFilter(typeof(VotingExceptionFilter))]
public sealed class VotingController(VotingService voting) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [HttpGet("Items")]
    public IActionResult GetItems([FromQuery] string? search, [FromQuery] int startIndex = 0, [FromQuery] int limit = 25) =>
        Json(voting.GetItems(VotingIdentity.RequireUserId(User), search, startIndex, limit));

    [HttpGet("Items/{id:guid}")]
    public IActionResult GetItem([FromRoute] Guid id) =>
        Json(voting.GetItem(VotingIdentity.RequireUserId(User), id));

    [HttpGet("Items/Home")]
    public IActionResult GetItemsHome([FromQuery] int limit = 3) =>
        Json(voting.GetItemsHome(VotingIdentity.RequireUserId(User), limit));

    [HttpPut("Items/{id:guid}/Vote")]
    public IActionResult SetItemVote([FromRoute] Guid id, [FromBody] VoteUpdate update) =>
        Json(voting.SetItemVote(VotingIdentity.RequireUserId(User), id, update.Approved));

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpGet("Items/Summary")]
    public IActionResult GetItemsSummary() => Json(voting.GetItemsSummary(VotingIdentity.RequireUserId(User)));

    [HttpGet("Series")]
    public IActionResult GetSeries([FromQuery] string? search, [FromQuery] int startIndex = 0, [FromQuery] int limit = 25) =>
        Json(voting.GetSeries(VotingIdentity.RequireUserId(User), search, startIndex, limit));

    [HttpGet("Home")]
    public IActionResult GetHome([FromQuery] int limit = 3) =>
        Json(voting.GetHome(VotingIdentity.RequireUserId(User), limit));

    [HttpGet("Series/{id:guid}")]
    public IActionResult GetSeries([FromRoute] Guid id) =>
        Json(voting.GetSeries(VotingIdentity.RequireUserId(User), id));

    [HttpPut("Series/{id:guid}/Vote")]
    public IActionResult SetVote([FromRoute] Guid id, [FromBody] VoteUpdate update) =>
        Json(voting.SetVote(VotingIdentity.RequireUserId(User), id, update.Approved));

    [HttpGet("Preferences")]
    public IActionResult GetPreferences() => Json(voting.GetPreferences(VotingIdentity.RequireUserId(User)));

    [HttpPut("Preferences")]
    public IActionResult SetPreferences([FromBody] VotingPreferences update) =>
        Json(voting.SetPreferences(VotingIdentity.RequireUserId(User), update.HomeDismissed));

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpGet("Summary")]
    public IActionResult GetSummary() => Json(voting.GetSummary(VotingIdentity.RequireUserId(User)));

    private static JsonResult Json(object value) => new(value, JsonOptions);
}

public static class VotingIdentity
{
    public static Guid RequireUserId(ClaimsPrincipal principal)
    {
        var claim = principal.FindFirst("Jellyfin-UserId")?.Value;
        return principal.Identity?.IsAuthenticated == true && Guid.TryParse(claim, out var id) && id != Guid.Empty ? id
            : throw new RemovalException("Sign in with a Jellyfin user account to vote.", 403);
    }
}

public sealed class VotingExceptionFilter(ILogger<VotingExceptionFilter> logger) : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        var (status, message) = context.Exception is RemovalException exception
            ? (exception.StatusCode, exception.Message)
            : (500, "Voting is unavailable. Your change was not confirmed. Try again or contact your administrator.");
        logger.LogWarning("Media Remover voting request failed with status {Status}; exception type {ExceptionType}", status, context.Exception.GetType().Name);
        context.Result = new JsonResult(new { message }, new JsonSerializerOptions(JsonSerializerDefaults.Web)) { StatusCode = status };
        context.ExceptionHandled = true;
    }
}
