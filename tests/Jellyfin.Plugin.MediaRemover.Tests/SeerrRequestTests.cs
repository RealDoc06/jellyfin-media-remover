using System.Net;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.MediaRemover.Providers;
using Xunit;

namespace Jellyfin.Plugin.MediaRemover.Tests;

public sealed class SeerrRequestTests
{
    private static readonly ProviderOptions Options = new("", "", "https://seerr.test/base/seerr/", "private-api-key");
    private static readonly Guid LinkedUser = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public async Task ReadsMultipleRequestersAndRetainsSeparateRequestsBySameUser()
    {
        using var server = new StubServer(Details("""
            [{"id":10,"requestedBy":{"id":1,"displayName":"Doc","jellyfinUserId":"11111111222233334444555555555555"}},
             {"id":11,"requestedBy":{"id":2,"username":"Alex"}},
             {"id":12,"requestedBy":{"id":1,"displayName":"Doc","jellyfinUserId":"11111111-2222-3333-4444-555555555555"}}]
            """));

        var result = await server.LookupAsync();

        Assert.NotNull(result);
        Assert.Equal(34, result.Id);
        Assert.Equal(456, result.TmdbId);
        Assert.Equal(123, result.TvdbId);
        Assert.Collection(result.Requests,
            request => Assert.Equal(new SeerrRequest(10, new SeerrRequester(1, "Doc", LinkedUser)), request),
            request => Assert.Equal(new SeerrRequest(11, new SeerrRequester(2, "Alex", null)), request),
            request => Assert.Equal(new SeerrRequest(12, new SeerrRequester(1, "Doc", LinkedUser)), request));

        var sent = Assert.Single(server.Requests);
        Assert.Equal(HttpMethod.Get, sent.Method);
        Assert.Equal("https://seerr.test/base/seerr/api/v1/tv/456", sent.Url);
        Assert.Equal("private-api-key", sent.ApiKey);
    }

    [Fact]
    public async Task MissingOrDeletedRequesterRetainsTheRequestAsUnknown()
    {
        using var server = new StubServer(Details("""[{"id":10},{"id":11,"requestedBy":null}]"""));
        var result = await server.LookupAsync();

        Assert.NotNull(result);
        Assert.Equal(new[] { new SeerrRequest(10, null), new SeerrRequest(11, null) }, result.Requests);
    }

    [Theory]
    [InlineData("{\"id\":1,\"displayName\":\" Display \",\"username\":\"Account\"}", "Display")]
    [InlineData("{\"id\":1,\"displayName\":null,\"username\":\"Account\"}", "Account")]
    [InlineData("{\"id\":1,\"displayName\":\"  \",\"username\":null,\"jellyfinUsername\":\"Linked name\"}", "Linked name")]
    [InlineData("{\"id\":1}", "Seerr user #1")]
    [InlineData("{\"id\":1,\"displayName\":\"private@example.test\",\"username\":\"Account\"}", "Account")]
    [InlineData("{\"id\":1,\"displayName\":\"private@example.test\",\"email\":\"private@example.test\"}", "Seerr user #1")]
    public async Task UsesAvailablePublicLabelsWithoutEmailFallback(string user, string expected)
    {
        using var server = new StubServer(Details("[{\"id\":10,\"requestedBy\":" + user + "}]"));
        var result = await server.LookupAsync();

        Assert.NotNull(result);
        var requester = Assert.Single(result.Requests).RequestedBy;
        Assert.NotNull(requester);
        Assert.Equal(expected, requester.Name);
        Assert.Null(requester.JellyfinUserId);
    }

    [Theory]
    [InlineData("{\"id\":1,\"username\":\"Doc\",\"jellyfinUserId\":null}")]
    [InlineData("{\"id\":1,\"username\":\"Doc\",\"jellyfinUsername\":\"Doc\"}")]
    public async Task MissingLinkageNeverGuessesJellyfinIdentityFromUsername(string user)
    {
        using var server = new StubServer(Details("[{\"id\":10,\"requestedBy\":" + user + "}]"));
        var result = await server.LookupAsync();

        Assert.NotNull(result);
        var requester = Assert.Single(result.Requests).RequestedBy;
        Assert.NotNull(requester);
        Assert.Equal("Doc", requester.Name);
        Assert.Null(requester.JellyfinUserId);
    }

    [Fact]
    public async Task SerializesOnlySafeRequesterProjection()
    {
        using var server = new StubServer(Details("""
            [{"id":10,"requestedBy":{"id":1,"displayName":"Doc","email":"private@example.test",
              "jellyfinAuthToken":"private-jellyfin-token","plexToken":"private-plex-token",
              "password":"private-password","settings":{"apiKey":"private-settings"}}}]
            """));
        var result = await server.LookupAsync();
        var serialized = JsonSerializer.Serialize(result);

        Assert.NotNull(result);
        Assert.Equal(new SeerrRequester(1, "Doc", null), Assert.Single(result.Requests).RequestedBy);
        Assert.DoesNotContain("private", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("email", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("[null]")]
    [InlineData("[{\"id\":0}]")]
    [InlineData("[{\"id\":\"10\"}]")]
    [InlineData("[{\"id\":10,\"requestedBy\":5}]")]
    [InlineData("[{\"id\":10,\"requestedBy\":{}}]")]
    [InlineData("[{\"id\":10,\"requestedBy\":{\"id\":0}}]")]
    [InlineData("[{\"id\":10,\"requestedBy\":{\"id\":1,\"jellyfinUserId\":123}}]")]
    [InlineData("[{\"id\":10,\"requestedBy\":{\"id\":1,\"jellyfinUserId\":\"private-bad-guid\"}}]")]
    [InlineData("[{\"id\":10,\"requestedBy\":{\"id\":1,\"jellyfinUserId\":\"\"}}]")]
    [InlineData("[{\"id\":10,\"requestedBy\":{\"id\":1,\"jellyfinUserId\":\"00000000-0000-0000-0000-000000000000\"}}]")]
    [InlineData("[{\"id\":10,\"requestedBy\":{\"id\":1,\"jellyfinUserId\":\"{11111111-2222-3333-4444-555555555555}\"}}]")]
    [InlineData("[{\"id\":10,\"requestedBy\":{\"id\":1,\"displayName\":{\"private\":true}}}]")]
    public async Task MalformedRequestIdentityFailsExplicitlyWithoutExposingPayload(string requests)
    {
        using var server = new StubServer(Details(requests));
        var error = await Assert.ThrowsAsync<ProviderException>(server.LookupAsync);

        Assert.DoesNotContain("private", error.ToString(), StringComparison.Ordinal);
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("{\"id\":456,\"name\":\"Example\"}")]
    [InlineData("{\"id\":456,\"name\":\"Example\",\"mediaInfo\":null}")]
    public async Task ValidDetailsWithoutLocalMediaReturnAbsent(string body)
    {
        using var server = new StubServer(body);
        Assert.Null(await server.LookupAsync());
    }

    [Fact]
    public async Task ExistingMediaWithoutRequestsRemainsDistinctFromAbsentMedia()
    {
        using var server = new StubServer(Details("[]"));
        var result = await server.LookupAsync();
        Assert.NotNull(result);
        Assert.Empty(result.Requests);
    }

    [Fact]
    public async Task BlocklistedMediaCanBeReadButCannotBePreviewedForDeletion()
    {
        var body = Details("""[{"id":10,"requestedBy":{"id":1,"displayName":"Doc"}}]""")
            .Replace("\"mediaType\":\"tv\"", "\"mediaType\":\"tv\",\"status\":6", StringComparison.Ordinal);
        using var server = new StubServer(body);
        var result = await server.LookupAsync();
        Assert.NotNull(result);
        Assert.Single(result.Requests);

        var error = await Assert.ThrowsAsync<ProviderException>(() => server.Gateway.FindSeerrAsync(Options, 456, CancellationToken.None));
        Assert.Contains("blocklist", error.Message, StringComparison.Ordinal);
        Assert.All(server.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
    }

    [Theory]
    [InlineData("\"id\":456", "\"id\":999")]
    [InlineData("\"id\":34", "\"id\":0")]
    [InlineData("\"tmdbId\":456", "\"tmdbId\":999")]
    [InlineData("\"tvdbId\":123", "\"tvdbId\":0")]
    [InlineData("\"mediaType\":\"tv\"", "\"mediaType\":\"movie\"")]
    [InlineData("\"requests\":[]", "\"requests\":null")]
    public async Task RejectsWrongOrIncompleteMediaMappings(string before, string after)
    {
        using var server = new StubServer(Details("[]").Replace(before, after, StringComparison.Ordinal));
        await Assert.ThrowsAsync<ProviderException>(server.LookupAsync);
    }

    [Fact]
    public async Task OptionalTvdbIdMayBeMissing()
    {
        using var server = new StubServer(Details("[]").Replace("\"tvdbId\":123,", "", StringComparison.Ordinal));
        var result = await server.LookupAsync();
        Assert.NotNull(result);
        Assert.Null(result.TvdbId);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"id\":456}")]
    [InlineData("{\"id\":456,\"name\":\"Example\",\"mediaInfo\":42}")]
    [InlineData("<html>private upstream body</html>")]
    public async Task MalformedTvDetailsNeverImplyNoRequests(string body)
    {
        using var server = new StubServer(body);
        await Assert.ThrowsAsync<ProviderException>(server.LookupAsync);
    }

    [Theory]
    [InlineData(302)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(500)]
    public async Task HttpFailureDoesNotMeanAbsentAndDoesNotLeakProviderBody(int status)
    {
        using var server = new StubServer("private upstream response", (HttpStatusCode)status);
        var error = await Assert.ThrowsAsync<ProviderException>(server.LookupAsync);
        Assert.DoesNotContain("private", error.ToString(), StringComparison.Ordinal);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task OversizedResponseIsBounded()
    {
        using var server = new StubServer(new string('x', 8 * 1024 * 1024 + 1));
        await Assert.ThrowsAsync<ProviderException>(server.LookupAsync);
    }

    private static string Details(string requests) =>
        "{\"id\":456,\"name\":\"Example\",\"mediaInfo\":{\"id\":34,\"tmdbId\":456,\"tvdbId\":123,\"mediaType\":\"tv\",\"requests\":" + requests + "}}";

    private sealed record Request(HttpMethod Method, string Url, string ApiKey);

    private sealed class StubServer(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler, IHttpClientFactory
    {
        public ProviderGateway Gateway => new(this);

        public List<Request> Requests { get; } = [];

        public Task<SeerrRequestInfo?> LookupAsync() => Gateway.GetSeerrRequestsAsync(Options, 456, CancellationToken.None);

        public HttpClient CreateClient(string name)
        {
            Assert.Equal("MediaRemover", name);
            return new HttpClient(this, disposeHandler: false);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(new Request(request.Method, request.RequestUri!.AbsoluteUri, request.Headers.GetValues("X-Api-Key").Single()));
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
