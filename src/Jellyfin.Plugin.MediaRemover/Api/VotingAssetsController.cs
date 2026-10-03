using Jellyfin.Plugin.MediaRemover.WebIntegration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.MediaRemover.Api;

/// <summary>Public code and styles only; voting data is served by authenticated endpoints.</summary>
[ApiController]
[AllowAnonymous]
[Route("MediaRemover/Voting")]
public sealed class VotingAssetsController : ControllerBase
{
    [HttpGet("client.js")]
    [HttpHead("client.js")]
    public IActionResult Script([FromQuery] string? v) => Asset("voting.js", "application/javascript; charset=utf-8", v);

    [HttpGet("client.css")]
    [HttpHead("client.css")]
    public IActionResult Styles([FromQuery] string? v) => Asset("voting.css", "text/css; charset=utf-8", v);

    private IActionResult Asset(string name, string contentType, string? version)
    {
        var stream = typeof(VotingAssetsController).Assembly.GetManifestResourceStream($"Jellyfin.Plugin.MediaRemover.Web.{name}");
        if (stream is null) return NotFound();
        Response.Headers.CacheControl = version == VotingClientAssets.Version
            ? "public, max-age=31536000, immutable" : "no-cache";
        Response.Headers.XContentTypeOptions = "nosniff";
        return File(stream, contentType);
    }
}
