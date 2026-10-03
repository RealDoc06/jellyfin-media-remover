using System.IO.Compression;
using System.Text;
using Jellyfin.Plugin.MediaRemover.WebIntegration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Jellyfin.Plugin.MediaRemover.Tests;

public sealed class WebIntegrationTests
{
    private const string Shell = "<!doctype html><html><head><title>Jellyfin</title></head><body>Library</body></html>";

    [Theory]
    [InlineData("/web", "", "", "/MediaRemover")]
    [InlineData("/web/", "", "", "/MediaRemover")]
    [InlineData("/web/index.html", "", "", "/MediaRemover")]
    [InlineData("/jellyfin/web/", "/jellyfin", "", "/jellyfin/MediaRemover")]
    [InlineData("/web/index.html", "/jellyfin", "/jellyfin", "/jellyfin/MediaRemover")]
    [InlineData("/jellyfin/web/", "/jellyfin", "/proxy", "/proxy/jellyfin/MediaRemover")]
    public async Task InjectsShellAtCorrectBaseUrl(string path, string configuredBaseUrl, string pathBase, string expectedPrefix)
    {
        var context = Context(path);
        context.Request.PathBase = pathBase;
        await new VotingWebMiddleware(WriteShell, configuredBaseUrl).InvokeAsync(context);
        var html = Body(context);
        Assert.Contains($"src=\"{expectedPrefix}/Voting/client.js?v={VotingClientAssets.Version}\"", html);
        Assert.Contains("<script defer data-media-remover-voting", html);
        Assert.True(html.IndexOf("data-media-remover-voting", StringComparison.Ordinal) < html.IndexOf("</head>", StringComparison.Ordinal));
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
        Assert.Null(context.Response.ContentLength);
        Assert.False(context.Response.Headers.ContainsKey("ETag"));
        Assert.False(context.Response.Headers.ContainsKey("Last-Modified"));
        Assert.False(context.Response.Headers.ContainsKey("Accept-Ranges"));
    }

    [Theory]
    [InlineData("/Videos/item/stream", "GET", "")]
    [InlineData("/web/main.js", "GET", "")]
    [InlineData("/MediaRemover/Voting/client.js", "GET", "")]
    [InlineData("/other/web/", "GET", "")]
    [InlineData("/web/", "POST", "")]
    [InlineData("/web/", "HEAD", "")]
    [InlineData("/web/", "GET", "/jellyfin")]
    public async Task DoesNotWrapUnrelatedTrafficOrHead(string path, string method, string baseUrl)
    {
        var context = Context(path);
        context.Request.Method = method;
        context.Request.Headers.AcceptEncoding = "gzip";
        var originalBody = context.Response.Body;
        await new VotingWebMiddleware(async inner =>
        {
            Assert.Same(originalBody, inner.Response.Body);
            Assert.Equal("gzip", inner.Request.Headers.AcceptEncoding);
            if (!HttpMethods.IsHead(method)) await WriteShell(inner);
        }, baseUrl).InvokeAsync(context);
        Assert.DoesNotContain("data-media-remover-voting", Body(context));
    }

    [Theory]
    [InlineData(302, "text/html", null)]
    [InlineData(404, "text/html", null)]
    [InlineData(206, "text/html", null)]
    [InlineData(200, "application/json", null)]
    [InlineData(200, "text/html; charset=iso-8859-1", null)]
    [InlineData(200, "text/html", "gzip")]
    public async Task PreservesNonSuccessfulOrIncompatibleResponses(int status, string contentType, string? encoding)
    {
        var context = Context();
        await new VotingWebMiddleware(async inner =>
        {
            await WriteShell(inner);
            inner.Response.StatusCode = status;
            inner.Response.ContentType = contentType;
            if (encoding is not null) inner.Response.Headers.ContentEncoding = encoding;
        }, "").InvokeAsync(context);
        Assert.Equal(Shell, Body(context));
        Assert.Equal(status, context.Response.StatusCode);
        Assert.Equal(Encoding.UTF8.GetByteCount(Shell), context.Response.ContentLength);
        Assert.Equal("original-etag", context.Response.Headers.ETag);
    }

    [Fact]
    public async Task RemovesConditionalCompressionAndRangeHeadersOnlyDuringShellCapture()
    {
        var context = Context();
        Dictionary<string, string> headers = new()
        {
            ["Accept-Encoding"] = "gzip, br", ["If-None-Match"] = "old-etag",
            ["If-Modified-Since"] = "Sat, 03 Oct 2026 00:00:00 GMT", ["Range"] = "bytes=0-20", ["If-Range"] = "old-etag"
        };
        foreach (var (name, value) in headers) context.Request.Headers[name] = value;
        context.Request.Headers.Authorization = "session-is-unchanged";
        await new VotingWebMiddleware(async inner =>
        {
            foreach (var (name, _) in headers) Assert.False(inner.Request.Headers.ContainsKey(name));
            Assert.Equal("session-is-unchanged", inner.Request.Headers.Authorization);
            await WriteShell(inner);
        }, "").InvokeAsync(context);
        foreach (var (name, value) in headers) Assert.Equal(value, context.Request.Headers[name]);
    }

    [Fact]
    public async Task OversizedResponsePassesThroughExactlyWithoutKeepingAnUnboundedBuffer()
    {
        var context = Context();
        var beginning = Encoding.UTF8.GetBytes(Shell);
        var tail = new byte[VotingWebMiddleware.MaximumShellBytes + 50];
        Array.Fill(tail, (byte)'x');
        await new VotingWebMiddleware(async inner =>
        {
            inner.Response.ContentType = "text/html";
            inner.Response.ContentLength = beginning.Length + tail.Length;
            await inner.Response.Body.WriteAsync(beginning);
            await inner.Response.Body.WriteAsync(tail);
            await inner.Response.Body.FlushAsync();
        }, "").InvokeAsync(context);
        Assert.Equal(beginning.Concat(tail), ((MemoryStream)context.Response.Body).ToArray());
        Assert.Equal(beginning.Length + tail.Length, context.Response.ContentLength);
    }

    [Fact]
    public async Task InjectionIsIdempotentAcrossNestedMiddleware()
    {
        var context = Context();
        var inner = new VotingWebMiddleware(WriteShell, "");
        await new VotingWebMiddleware(inner.InvokeAsync, "").InvokeAsync(context);
        Assert.Equal(1, Body(context).Split("data-media-remover-voting").Length - 1);
    }

    [Fact]
    public async Task ExceptionsRestoreRequestHeadersAndResponseBodyWithoutSendingPartialShell()
    {
        var context = Context();
        var body = context.Response.Body;
        context.Request.Headers.AcceptEncoding = "br";
        await Assert.ThrowsAsync<InvalidOperationException>(() => new VotingWebMiddleware(async inner =>
        {
            await WriteShell(inner);
            throw new InvalidOperationException("Static file middleware failure");
        }, "").InvokeAsync(context));
        Assert.Same(body, context.Response.Body);
        Assert.Equal("br", context.Request.Headers.AcceptEncoding);
        Assert.Empty(Body(context));
    }

    [Fact]
    public async Task CancellationDoesNotSendBufferedHtmlAndRestoresPipeline()
    {
        var context = Context();
        var body = context.Response.Body;
        using var cancellation = new CancellationTokenSource();
        context.RequestAborted = cancellation.Token;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new VotingWebMiddleware(async inner =>
        {
            await WriteShell(inner);
            cancellation.Cancel();
        }, "").InvokeAsync(context));
        Assert.Same(body, context.Response.Body);
        Assert.Empty(Body(context));
    }

    [Fact]
    public async Task InvalidUtf8RemainsUntouched()
    {
        var context = Context();
        byte[] invalid = [0xff, 0xfe, 0xff];
        await new VotingWebMiddleware(async inner =>
        {
            inner.Response.ContentType = "text/html";
            await inner.Response.Body.WriteAsync(invalid);
        }, "").InvokeAsync(context);
        Assert.Equal(invalid, ((MemoryStream)context.Response.Body).ToArray());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CoexistsWithResponseCompressionInEitherPipelineOrder(bool compressionFirst)
    {
        var services = new ServiceCollection().AddLogging().AddResponseCompression(options =>
        {
            options.Providers.Add<GzipCompressionProvider>();
        }).BuildServiceProvider();
        await using (services)
        {
            var app = new ApplicationBuilder(services);
            if (compressionFirst) app.UseResponseCompression();
            app.Use(next => new VotingWebMiddleware(next, "").InvokeAsync);
            if (!compressionFirst) app.UseResponseCompression();
            app.Run(WriteShell);
            var context = Context();
            context.RequestServices = services;
            context.Request.Headers.AcceptEncoding = "gzip";
            await app.Build()(context);
            var bytes = ((MemoryStream)context.Response.Body).ToArray();
            if (context.Response.Headers.ContentEncoding == "gzip")
            {
                await using var gzip = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
                using var decoded = new MemoryStream();
                await gzip.CopyToAsync(decoded);
                bytes = decoded.ToArray();
            }
            Assert.Contains("data-media-remover-voting", Encoding.UTF8.GetString(bytes));
            Assert.Null(context.Response.ContentLength);
        }
    }

    private static DefaultHttpContext Context(string path = "/web/")
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task WriteShell(HttpContext context)
    {
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength = Encoding.UTF8.GetByteCount(Shell);
        context.Response.Headers.ETag = "original-etag";
        context.Response.Headers.LastModified = "Sat, 03 Oct 2026 00:00:00 GMT";
        context.Response.Headers.AcceptRanges = "bytes";
        await context.Response.WriteAsync(Shell);
    }

    private static string Body(HttpContext context) => Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());
}
