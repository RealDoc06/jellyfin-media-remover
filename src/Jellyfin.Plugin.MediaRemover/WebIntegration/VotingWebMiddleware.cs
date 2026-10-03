using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Jellyfin.Plugin.MediaRemover.WebIntegration;

/// <summary>Injects a versioned external client without changing Jellyfin's web files on disk.</summary>
public sealed class VotingWebMiddleware(RequestDelegate next, string? baseUrl)
{
    public const int MaximumShellBytes = 2 * 1024 * 1024;
    private const string Marker = "data-media-remover-voting";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly string[] SuppressedHeaders =
        ["Accept-Encoding", "If-None-Match", "If-Modified-Since", "Range", "If-Range"];
    private readonly PathString _baseUrl = new((baseUrl ?? "").TrimEnd('/'));

    public async Task InvokeAsync(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method) || !IsShellRequest(context.Request, out var effectiveBaseUrl))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var headers = SuppressedHeaders.Select(name => (Name: name, Value: context.Request.Headers[name])).ToArray();
        foreach (var (name, _) in headers) context.Request.Headers.Remove(name);
        var originalBody = context.Response.Body;
        using var buffer = new BoundedShellStream(originalBody, MaximumShellBytes);
        context.Response.Body = buffer;
        try
        {
            // Keep compression and conditional static-file responses out of this small HTML transform.
            await next(context).ConfigureAwait(false);
            context.RequestAborted.ThrowIfCancellationRequested();
            if (buffer.IsPassthrough) return;

            var bytes = buffer.GetBytes();
            var transformed = TryInject(context.Response, bytes, effectiveBaseUrl);
            if (transformed is not null)
            {
                ClearShellValidators(context.Response);
                // Other HTML transformers can wrap this filter and replace headers afterward.
                context.Response.OnStarting(() =>
                {
                    ClearShellValidators(context.Response);
                    return Task.CompletedTask;
                });
                bytes = transformed;
            }
            context.Response.Body = originalBody;
            await originalBody.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
        }
        finally
        {
            context.Response.Body = originalBody;
            foreach (var (name, value) in headers)
            {
                if (StringValues.IsNullOrEmpty(value)) context.Request.Headers.Remove(name);
                else context.Request.Headers[name] = value;
            }
        }
    }

    private static void ClearShellValidators(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers.Remove("ETag");
        response.Headers.Remove("Last-Modified");
        response.Headers.Remove("Accept-Ranges");
        response.ContentLength = null;
    }

    private bool IsShellRequest(HttpRequest request, out PathString effectiveBaseUrl)
    {
        effectiveBaseUrl = request.PathBase;
        var path = request.Path;
        if (_baseUrl.HasValue)
        {
            // Normally Jellyfin has not mapped BaseUrl yet. Also support an upstream UsePathBase.
            if (path.StartsWithSegments(_baseUrl, out var remainder))
            {
                effectiveBaseUrl = effectiveBaseUrl.Add(_baseUrl);
                path = remainder;
            }
            else if (!request.PathBase.Equals(_baseUrl)) return false;
        }
        return path.Equals("/web") || path.Equals("/web/") || path.Equals("/web/index.html");
    }

    private static byte[]? TryInject(HttpResponse response, byte[] bytes, PathString basePath)
    {
        if (response.HasStarted || response.StatusCode != StatusCodes.Status200OK ||
            !IsUtf8Html(response.ContentType) || !StringValues.IsNullOrEmpty(response.Headers.ContentEncoding)) return null;
        string html;
        try { html = StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { return null; }
        if (html.Contains(Marker, StringComparison.Ordinal)) return null;
        var insertion = html.LastIndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        if (insertion < 0) insertion = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        if (insertion < 0) return null;

        var source = basePath.ToUriComponent() + "/MediaRemover/Voting/client.js?v=" + VotingClientAssets.Version;
        var script = $"<script defer {Marker} src=\"{HtmlEncoder.Default.Encode(source)}\"></script>";
        return Encoding.UTF8.GetBytes(html.Insert(insertion, script));
    }

    private static bool IsUtf8Html(string? contentType)
    {
        if (!Microsoft.Net.Http.Headers.MediaTypeHeaderValue.TryParse(contentType, out var parsed) ||
            !parsed.MediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase)) return false;
        return !parsed.Charset.HasValue || parsed.Charset.Equals("utf-8", StringComparison.OrdinalIgnoreCase);
    }
}
