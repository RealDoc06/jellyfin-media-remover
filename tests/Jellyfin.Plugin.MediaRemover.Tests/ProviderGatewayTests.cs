using System.Net;
using System.Text;
using Jellyfin.Plugin.MediaRemover.Providers;
using Xunit;

namespace Jellyfin.Plugin.MediaRemover.Tests;

public sealed class ProviderGatewayTests
{
    private static readonly ProviderOptions Options = new("https://sonarr.test/sonarr", "sonarr-secret", "https://seerr.test/seerr/", "seerr-secret",
        "https://radarr.test/radarr/", "radarr-secret");
    private static readonly SonarrSeries Series = new(12, 123, "Example", "/tv/Example");
    private static readonly SeerrMedia Media = new(34, 456, 123, "tv", 1);
    private const string SeriesJson = """{"id":12,"tvdbId":123,"title":"Example","path":"/tv/Example"}""";
    private const string TvJson = """{"id":456,"name":"Example","mediaInfo":{"id":34,"tmdbId":456,"tvdbId":123,"mediaType":"tv","requests":[{"id":7}]}}""";
    private static readonly RadarrMovie Movie = new(56, 789, "Example Movie", "/movies/Example Movie");
    private static readonly SeerrMedia MovieMedia = new(78, 789, null, "movie", 1);
    private const string MovieJson = """{"id":56,"tmdbId":789,"title":"Example Movie","path":"/movies/Example Movie"}""";
    private const string SeerrMovieJson = """{"id":789,"title":"Example Movie","mediaInfo":{"id":78,"tmdbId":789,"tvdbId":null,"mediaType":"movie","requests":[{"id":9}]}}""";

    [Fact]
    public async Task RadarrLookupUsesTmdbIdentityAndPreservesBasePath()
    {
        using var server = new StubServer(Json("[" + MovieJson + "]"));
        Assert.Equal(Movie, await server.Gateway.FindRadarrAsync(Options, 789, CancellationToken.None));
        var request = Assert.Single(server.Requests);
        Assert.Equal("https://radarr.test/radarr/api/v3/movie?tmdbId=789", request.Url);
        Assert.Equal("radarr-secret", request.ApiKey);
        Assert.Equal(HttpMethod.Get, request.Method);
    }

    [Fact]
    public async Task RadarrEmptyFilteredResultMeansAbsent()
    {
        using var server = new StubServer(Json("[]"));
        Assert.Null(await server.Gateway.FindRadarrAsync(Options, 789, CancellationToken.None));
    }

    [Theory]
    [InlineData(404)]
    [InlineData(401)]
    [InlineData(500)]
    public async Task RadarrLookupFailureDoesNotMeanAbsent(int status)
    {
        using var server = new StubServer(Json("[]", (HttpStatusCode)status));
        await Assert.ThrowsAsync<ProviderException>(() => server.Gateway.FindRadarrAsync(Options, 789, CancellationToken.None));
    }

    [Theory]
    [InlineData("[{\"id\":56,\"tmdbId\":999,\"title\":\"Example Movie\",\"path\":\"/movies/Example Movie\"}]")]
    [InlineData("[{\"id\":56,\"title\":\"Example Movie\",\"path\":\"/movies/Example Movie\"}]")]
    [InlineData("[{\"id\":0,\"tmdbId\":789,\"title\":\"Example Movie\",\"path\":\"/movies/Example Movie\"}]")]
    [InlineData("[{\"id\":56,\"tmdbId\":789,\"title\":\"Example Movie\"}]")]
    [InlineData("{}")]
    [InlineData("<html>secret upstream error</html>")]
    public async Task RadarrRejectsBadIdentityOrMalformedResponse(string body)
    {
        using var server = new StubServer(Json(body));
        await Assert.ThrowsAsync<ProviderException>(() => server.Gateway.FindRadarrAsync(Options, 789, CancellationToken.None));
    }

    [Fact]
    public async Task RadarrRejectsAmbiguousMatches()
    {
        using var server = new StubServer(Json("[" + MovieJson + "," + MovieJson + "]"));
        await Assert.ThrowsAsync<ProviderException>(() => server.Gateway.FindRadarrAsync(Options, 789, CancellationToken.None));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task RadarrDeletionRevalidatesIdentityAndUsesItsOwnExclusionParameter(bool deleteFiles, bool exclude)
    {
        using var server = new StubServer(Json(MovieJson), Empty(HttpStatusCode.NoContent));
        await server.Gateway.DeleteRadarrAsync(Options, Movie, deleteFiles, exclude, CancellationToken.None);
        Assert.Collection(server.Requests,
            request =>
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal("https://radarr.test/radarr/api/v3/movie/56", request.Url);
            },
            request =>
            {
                Assert.Equal(HttpMethod.Delete, request.Method);
                Assert.Equal($"https://radarr.test/radarr/api/v3/movie/56?deleteFiles={deleteFiles.ToString().ToLowerInvariant()}&addImportExclusion={exclude.ToString().ToLowerInvariant()}", request.Url);
                Assert.Equal("radarr-secret", request.ApiKey);
            });
    }

    [Theory]
    [InlineData("\"id\":56", "\"id\":99")]
    [InlineData("\"tmdbId\":789", "\"tmdbId\":999")]
    [InlineData("/movies/Example Movie", "/movies/Different")]
    public async Task RadarrDeletionStopsIfIdReusedOrPathChanged(string before, string after)
    {
        using var server = new StubServer(Json(MovieJson.Replace(before, after, StringComparison.Ordinal)));
        await Assert.ThrowsAsync<ProviderException>(() => server.Gateway.DeleteRadarrAsync(Options, Movie, true, true, CancellationToken.None));
        Assert.Equal(HttpMethod.Get, Assert.Single(server.Requests).Method);
    }

    [Fact]
    public async Task RadarrAlreadyRemovedSkipsDeletion()
    {
        using var server = new StubServer(Empty(HttpStatusCode.NotFound));
        await server.Gateway.DeleteRadarrAsync(Options, Movie, true, true, CancellationToken.None);
        Assert.Equal(HttpMethod.Get, Assert.Single(server.Requests).Method);
    }

    [Fact]
    public async Task RadarrFailedRevalidationNeverSendsDelete()
    {
        using var server = new StubServer(Empty(HttpStatusCode.ServiceUnavailable));
        await Assert.ThrowsAsync<ProviderException>(() => server.Gateway.DeleteRadarrAsync(Options, Movie, true, true, CancellationToken.None));
        Assert.Equal(HttpMethod.Get, Assert.Single(server.Requests).Method);
    }

    [Fact]
    public async Task RadarrConnectionTestVerifiesAppIdentity()
    {
        using var server = new StubServer(Json("""{"appName":"Radarr","version":"6.0.0"}"""), Json("""{"appName":"Sonarr","version":"4.0.0"}"""));
        await server.Gateway.TestRadarrAsync(Options, CancellationToken.None);
        await Assert.ThrowsAsync<ProviderException>(() => server.Gateway.TestRadarrAsync(Options, CancellationToken.None));
        Assert.All(server.Requests, request =>
        {
            Assert.Equal("https://radarr.test/radarr/api/v3/system/status", request.Url);
            Assert.Equal("radarr-secret", request.ApiKey);
        });
    }

    [Fact]
    public async Task SeerrMovieLookupUsesMovieCatalogMapping()
    {
        using var server = new StubServer(Json(SeerrMovieJson));
        Assert.Equal(MovieMedia, await server.Gateway.FindSeerrMovieAsync(Options, 789, CancellationToken.None));
        Assert.Equal("https://seerr.test/seerr/api/v1/movie/789", Assert.Single(server.Requests).Url);
    }

    [Theory]
    [InlineData("\"id\":789", "\"id\":999")]
    [InlineData("\"tmdbId\":789", "\"tmdbId\":999")]
    [InlineData("\"mediaType\":\"movie\"", "\"mediaType\":\"tv\"")]
    [InlineData("\"title\":\"Example Movie\"", "\"name\":\"Example Movie\"")]
    public async Task SeerrMovieRejectsIncorrectMappings(string before, string after)
    {
        using var server = new StubServer(Json(SeerrMovieJson.Replace(before, after, StringComparison.Ordinal)));
        await Assert.ThrowsAsync<ProviderException>(() => server.Gateway.FindSeerrMovieAsync(Options, 789, CancellationToken.None));
    }

    [Fact]
    public async Task SeerrMovieMetadataFailureDoesNotMeanAbsent()
    {
        using var server = new StubServer(Empty(HttpStatusCode.NotFound));
        await Assert.ThrowsAsync<ProviderException>(() => server.Gateway.FindSeerrMovieAsync(Options, 789, CancellationToken.None));
    }

    [Fact]
    public async Task SeerrMovieRequestsRemainReadableWhenBlocklistedButDeletionIsBlocked()
    {
        var body = SeerrMovieJson.Replace("\"mediaType\":\"movie\"", "\"mediaType\":\"movie\",\"status\":6", StringComparison.Ordinal)
            .Replace("{\"id\":9}", "{\"id\":9,\"requestedBy\":{\"id\":2,\"displayName\":\"secret@example.test\",\"username\":\"Doc\"}}", StringComparison.Ordinal);
        using var server = new StubServer(Json(body), Json(body));
        var result = await server.Gateway.GetSeerrMovieRequestsAsync(Options, 789, CancellationToken.None);
        Assert.NotNull(result);
        Assert.Equal(78, result.Id);
        Assert.Equal(789, result.TmdbId);
        Assert.Null(result.TvdbId);
        Assert.Equal(new SeerrRequest(9, new SeerrRequester(2, "Doc", null)), Assert.Single(result.Requests));
        var exception = await Assert.ThrowsAsync<ProviderException>(() => server.Gateway.DeleteSeerrAsync(Options, MovieMedia, CancellationToken.None));
        Assert.Contains("blocklist", exception.Message, StringComparison.Ordinal);
        Assert.All(server.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
    }

    [Fact]
    public async Task SeerrMovieRequestsRejectTvRecordWithSameTmdbId()
    {
        using var server = new StubServer(Json(SeerrMovieJson.Replace("\"mediaType\":\"movie\"", "\"mediaType\":\"tv\"", StringComparison.Ordinal)));
        await Assert.ThrowsAsync<ProviderException>(() => server.Gateway.GetSeerrMovieRequestsAsync(Options, 789, CancellationToken.None));
    }

    [Fact]
    public async Task SeerrMovieDeletionRechecksMappingAndDeletesOnlyLocalMedia()
    {
        using var server = new StubServer(Json(SeerrMovieJson), Empty(HttpStatusCode.NoContent));
        await server.Gateway.DeleteSeerrAsync(Options, MovieMedia, CancellationToken.None);
        Assert.Collection(server.Requests,
            request =>
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal("https://seerr.test/seerr/api/v1/movie/789", request.Url);
            },
            request =>
            {
                Assert.Equal(HttpMethod.Delete, request.Method);
                Assert.Equal("https://seerr.test/seerr/api/v1/media/78", request.Url);
            });
    }

    [Theory]
    [InlineData("\"id\":78", "\"id\":99")]
    [InlineData("\"mediaType\":\"movie\"", "\"mediaType\":\"tv\"")]
    public async Task SeerrMovieDeletionStopsIfRecordIdentityChanged(string before, string after)
    {
        using var server = new StubServer(Json(SeerrMovieJson.Replace(before, after, StringComparison.Ordinal)));
        await Assert.ThrowsAsync<ProviderException>(() => server.Gateway.DeleteSeerrAsync(Options, MovieMedia, CancellationToken.None));
        Assert.Equal(HttpMethod.Get, Assert.Single(server.Requests).Method);
    }

    [Fact]
    public async Task SeerrMovieAlreadyRemovedSkipsDeletion()
    {
        using var server = new StubServer(Json("""{"id":789,"title":"Example Movie","mediaInfo":null}"""));
        await server.Gateway.DeleteSeerrAsync(Options, MovieMedia, CancellationToken.None);
        Assert.Equal(HttpMethod.Get, Assert.Single(server.Requests).Method);
    }

    [Fact]
    public async Task SeerrMovieDeletionRejectsUnknownTypeBeforeNetworkCall()
    {
        using var server = new StubServer();
        await Assert.ThrowsAsync<ProviderException>(() => server.Gateway.DeleteSeerrAsync(Options, MovieMedia with { MediaType = "../media" }, CancellationToken.None));
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task SonarrLookupPreservesBasePathAndUsesHeaderAuthentication()
    {
        using var server = new StubServer(Json("[" + SeriesJson + "]"));
        var series = await server.Gateway.FindSonarrAsync(Options, 123, CancellationToken.None);

        Assert.Equal(Series, series);
        var request = Assert.Single(server.Requests);
        Assert.Equal("https://sonarr.test/sonarr/api/v3/series?tvdbId=123", request.Url);
        Assert.Equal("sonarr-secret", request.ApiKey);
        Assert.Equal(HttpMethod.Get, request.Method);
    }

    [Fact]
    public async Task SonarrEmptyFilteredResultMeansAbsent()
    {
        using var server = new StubServer(Json("[]"));
        Assert.Null(await server.Gateway.FindSonarrAsync(Options, 123, CancellationToken.None));
    }

    [Theory]
    [InlineData(404)]
    [InlineData(401)]
    [InlineData(500)]
    public async Task SonarrLookupFailureDoesNotMeanAbsent(int status)
    {
        using var server = new StubServer(Json("[]", (HttpStatusCode)status));
        await Assert.ThrowsAsync<ProviderException>(() => server.Gateway.FindSonarrAsync(Options, 123, CancellationToken.None));
    }

    [Theory]
    [InlineData("[{\"id\":12,\"tvdbId\":999,\"title\":\"Example\",\"path\":\"/tv/Example\"}]")]
    [InlineData("[{\"id\":12,\"title\":\"Example\",\"path\":\"/tv/Example\"}]")]
    [InlineData("[{\"id\":0,\"tvdbId\":123,\"title\":\"Example\",\"path\":\"/tv/Example\"}]")]
    [InlineData("[{\"id\":12,\"tvdbId\":123,\"title\":\"Example\"}]")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("<html>secret upstream error</html>")]
    public async Task SonarrRejectsBadIdentityOrMalformedResponse(string body)
    {
        using var server = new StubServer(Json(body));
        await Assert.ThrowsAsync<ProviderException>(() => server.Gateway.FindSonarrAsync(Options, 123, CancellationToken.None));
    }

    [Fact]
    public async Task SonarrRejectsAmbiguousMatches()
    {
        using var server = new StubServer(Json("[" + SeriesJson + "," + SeriesJson + "]"));
        await Assert.ThrowsAsync<ProviderException>(() => server.Gateway.FindSonarrAsync(Options, 123, CancellationToken.None));
    }

    [Fact]
    public async Task SeerrUsesTmdbMappingAndCountsRequests()
    {
        using var server = new StubServer(Json(TvJson));
        Assert.Equal(Media, await server.Gateway.FindSeerrAsync(Options, 456, CancellationToken.None));
        var request = Assert.Single(server.Requests);
        Assert.Equal("https://seerr.test/seerr/api/v1/tv/456", request.Url);
        Assert.Equal("seerr-secret", request.ApiKey);
    }

    [Theory]
    [InlineData("{\"id\":456,\"name\":\"Example\"}")]
    [InlineData("{\"id\":456,\"name\":\"Example\",\"mediaInfo\":null}")]
    public async Task SeerrValidTvDetailsWithoutLocalMediaMeansAbsent(string body)
    {
        using var server = new StubServer(Json(body));
        Assert.Null(await server.Gateway.FindSeerrAsync(Options, 456, CancellationToken.None));
    }

    [Theory]
    [InlineData(404)]
    [InlineData(403)]
    [InlineData(500)]
    public async Task SeerrMetadataFailureDoesNotMeanAbsent(int status)
    {
        using var server = new StubServer(Json("{}", (HttpStatusCode)status));
        await Assert.ThrowsAsync<ProviderException>(() => server.Gateway.FindSeerrAsync(Options, 456, CancellationToken.None));
    }

    [Theory]
    [InlineData("\"tmdbId\":456", "\"tmdbId\":999")]
    [InlineData("\"id\":456", "\"id\":999")]
    [InlineData("\"mediaType\":\"tv\"", "\"mediaType\":\"movie\"")]
    [InlineData("\"id\":34", "\"id\":0")]
    [InlineData("\"tvdbId\":123", "\"tvdbId\":0")]
    [InlineData("\"requests\":[{\"id\":7}]", "\"requests\":null")]
    public async Task SeerrRejectsIncorrectOrIncompleteMappings(string before, string after)
    {
        using var server = new StubServer(Json(TvJson.Replace(before, after, StringComparison.Ordinal)));
        await Assert.ThrowsAsync<ProviderException>(() => server.Gateway.FindSeerrAsync(Options, 456, CancellationToken.None));
    }

    [Fact]
    public async Task SeerrAcceptsMissingOptionalTvdbId()
    {
        using var server = new StubServer(Json(TvJson.Replace("\"tvdbId\":123,", "", StringComparison.Ordinal)));
        Assert.Equal(Media with { TvdbId = null }, await server.Gateway.FindSeerrAsync(Options, 456, CancellationToken.None));
    }

    [Fact]
    public async Task SeerrBlocklistedRecordCannotBeReportedAsDeletable()
    {
        using var server = new StubServer(Json(TvJson.Replace("\"mediaType\":\"tv\"", "\"mediaType\":\"tv\",\"status\":6", StringComparison.Ordinal)));
        var exception = await Assert.ThrowsAsync<ProviderException>(() => server.Gateway.FindSeerrAsync(Options, 456, CancellationToken.None));
        Assert.Contains("blocklist", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, true, "true", "true")]
    [InlineData(false, false, "false", "false")]
    public async Task SonarrDeletionRevalidatesIdentityAndSendsExplicitOptions(bool deleteFiles, bool exclude, string filesQuery, string excludeQuery)
    {
        using var server = new StubServer(Json(SeriesJson), Empty(HttpStatusCode.NoContent));
        await server.Gateway.DeleteSonarrAsync(Options, Series, deleteFiles, exclude, CancellationToken.None);

        Assert.Collection(server.Requests,
            request =>
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal("https://sonarr.test/sonarr/api/v3/series/12", request.Url);
            },
            request =>
            {
                Assert.Equal(HttpMethod.Delete, request.Method);
                Assert.Equal($"https://sonarr.test/sonarr/api/v3/series/12?deleteFiles={filesQuery}&addImportListExclusion={excludeQuery}", request.Url);
                Assert.Equal("sonarr-secret", request.ApiKey);
            });
    }

    [Theory]
    [InlineData("\"id\":12", "\"id\":99")]
    [InlineData("\"tvdbId\":123", "\"tvdbId\":999")]
    [InlineData("/tv/Example", "/tv/Different")]
    public async Task SonarrDeletionStopsIfIdReusedOrPathChanged(string before, string after)
    {
        using var server = new StubServer(Json(SeriesJson.Replace(before, after, StringComparison.Ordinal)));
        await Assert.ThrowsAsync<ProviderException>(() => server.Gateway.DeleteSonarrAsync(Options, Series, true, true, CancellationToken.None));
        Assert.Equal(HttpMethod.Get, Assert.Single(server.Requests).Method);
    }

    [Fact]
    public async Task SonarrAlreadyRemovedSkipsDeletion()
    {
        using var server = new StubServer(Empty(HttpStatusCode.NotFound));
        await server.Gateway.DeleteSonarrAsync(Options, Series, true, true, CancellationToken.None);
        Assert.Equal(HttpMethod.Get, Assert.Single(server.Requests).Method);
    }

    [Fact]
    public async Task SeerrDeletionChecksMappingAndDeletesOnlyLocalMedia()
    {
        using var server = new StubServer(Json(TvJson), Empty(HttpStatusCode.NoContent));
        await server.Gateway.DeleteSeerrAsync(Options, Media, CancellationToken.None);
        Assert.Collection(server.Requests,
            request => Assert.Equal("https://seerr.test/seerr/api/v1/tv/456", request.Url),
            request =>
            {
                Assert.Equal(HttpMethod.Delete, request.Method);
                Assert.Equal("https://seerr.test/seerr/api/v1/media/34", request.Url);
                Assert.Equal("seerr-secret", request.ApiKey);
            });
    }

    [Theory]
    [InlineData("\"id\":34", "\"id\":99")]
    [InlineData("\"tvdbId\":123", "\"tvdbId\":999")]
    public async Task SeerrDeletionStopsIfRecordIdentityChanged(string before, string after)
    {
        using var server = new StubServer(Json(TvJson.Replace(before, after, StringComparison.Ordinal)));
        await Assert.ThrowsAsync<ProviderException>(() => server.Gateway.DeleteSeerrAsync(Options, Media, CancellationToken.None));
        Assert.Equal(HttpMethod.Get, Assert.Single(server.Requests).Method);
    }

    [Fact]
    public async Task SeerrDeletionIncludesCurrentRequestsForTheSameMediaRecord()
    {
        using var server = new StubServer(Json(TvJson.Replace("\"requests\":[{\"id\":7}]", "\"requests\":[{\"id\":7},{\"id\":8}]", StringComparison.Ordinal)),
            Empty(HttpStatusCode.NoContent));
        await server.Gateway.DeleteSeerrAsync(Options, Media, CancellationToken.None);
        Assert.Equal(HttpMethod.Delete, server.Requests[1].Method);
    }

    [Fact]
    public async Task SeerrAlreadyRemovedSkipsDeletion()
    {
        using var server = new StubServer(Json("""{"id":456,"name":"Example"}"""));
        await server.Gateway.DeleteSeerrAsync(Options, Media, CancellationToken.None);
        Assert.Equal(HttpMethod.Get, Assert.Single(server.Requests).Method);
    }

    [Fact]
    public async Task SeerrDelete404IsNotReportedAsSuccessfulRemoval()
    {
        using var server = new StubServer(Json(TvJson), Empty(HttpStatusCode.NotFound));
        await Assert.ThrowsAsync<ProviderException>(() => server.Gateway.DeleteSeerrAsync(Options, Media, CancellationToken.None));
        Assert.Equal(2, server.Requests.Count);
    }

    [Fact]
    public async Task FailedRevalidationNeverSendsDelete()
    {
        using var server = new StubServer(Empty(HttpStatusCode.ServiceUnavailable));
        await Assert.ThrowsAsync<ProviderException>(() => server.Gateway.DeleteSonarrAsync(Options, Series, true, true, CancellationToken.None));
        Assert.Equal(HttpMethod.Get, Assert.Single(server.Requests).Method);
    }

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(307)]
    [InlineData(308)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(503)]
    public async Task ProviderErrorsAreRedactedAndRedirectsAreNotRetried(int status)
    {
        var response = Json("secret upstream body sonarr-secret https://user:password@example.test", (HttpStatusCode)status);
        response.Headers.Location = new Uri("https://attacker.test/?secret=sonarr-secret");
        using var server = new StubServer(response);
        var exception = await Assert.ThrowsAsync<ProviderException>(() => server.Gateway.FindSonarrAsync(Options, 123, CancellationToken.None));
        Assert.DoesNotContain("secret", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("password", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("attacker", exception.ToString(), StringComparison.Ordinal);
        Assert.Single(server.Requests);
    }

    [Theory]
    [InlineData("ftp://sonarr.test")]
    [InlineData("https://user:password@sonarr.test")]
    [InlineData("https://sonarr.test?apiKey=secret")]
    [InlineData("https://sonarr.test#secret")]
    [InlineData("relative/path")]
    [InlineData("")]
    public async Task InvalidProviderUrlIsRejectedBeforeSending(string url)
    {
        using var server = new StubServer();
        await Assert.ThrowsAsync<ProviderException>(() => server.Gateway.FindSonarrAsync(Options with { SonarrUrl = url }, 123, CancellationToken.None));
        Assert.Empty(server.Requests);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("key\r\nInjected: secret")]
    public async Task InvalidApiKeyIsRejectedBeforeSending(string key)
    {
        using var server = new StubServer();
        await Assert.ThrowsAsync<ProviderException>(() => server.Gateway.FindSonarrAsync(Options with { SonarrApiKey = key }, 123, CancellationToken.None));
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task TransportExceptionDoesNotLeakCredentials()
    {
        using var server = new StubServer(new HttpRequestException("secret URL and key"));
        var exception = await Assert.ThrowsAsync<ProviderException>(() => server.Gateway.FindSonarrAsync(Options, 123, CancellationToken.None));
        Assert.DoesNotContain("secret", exception.ToString(), StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public async Task ProviderTimeoutHasActionableSafeMessage()
    {
        using var server = new StubServer(new TaskCanceledException("secret URL and key"));
        var exception = await Assert.ThrowsAsync<ProviderException>(() => server.Gateway.FindSonarrAsync(Options, 123, CancellationToken.None));
        Assert.Contains("timed out", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallerCancellationIsPreserved()
    {
        using var server = new StubServer();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => server.Gateway.FindSonarrAsync(Options, 123, cancellation.Token));
    }

    [Fact]
    public async Task ConnectionTestsUseAuthenticatedEndpoints()
    {
        using var server = new StubServer(Json("""{"appName":"Sonarr","version":"4.0.0"}"""), Json("""{"id":1,"permissions":2}"""));
        await server.Gateway.TestSonarrAsync(Options, CancellationToken.None);
        await server.Gateway.TestSeerrAsync(Options, CancellationToken.None);
        Assert.Collection(server.Requests,
            request => Assert.Equal("https://sonarr.test/sonarr/api/v3/system/status", request.Url),
            request => Assert.Equal("https://seerr.test/seerr/api/v1/auth/me", request.Url));
    }

    [Fact]
    public async Task SeerrConnectionTestRejectsUserWithoutManagePermission()
    {
        using var server = new StubServer(Json("""{"id":1,"permissions":32}"""));
        await Assert.ThrowsAsync<ProviderException>(() => server.Gateway.TestSeerrAsync(Options, CancellationToken.None));
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage Empty(HttpStatusCode status) => new(status);

    private sealed record Request(HttpMethod Method, string Url, string ApiKey);

    private sealed class StubServer : HttpMessageHandler, IHttpClientFactory
    {
        private readonly Queue<HttpResponseMessage> _responses;
        private readonly Exception? _exception;

        public StubServer(params HttpResponseMessage[] responses)
        {
            _responses = new Queue<HttpResponseMessage>(responses);
            Gateway = new ProviderGateway(this);
        }

        public StubServer(Exception exception) : this()
        {
            _exception = exception;
        }

        public ProviderGateway Gateway { get; }

        public List<Request> Requests { get; } = [];

        public HttpClient CreateClient(string name)
        {
            Assert.Equal("MediaRemover", name);
            return new HttpClient(this, disposeHandler: false);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(new Request(request.Method, request.RequestUri!.AbsoluteUri, request.Headers.GetValues("X-Api-Key").Single()));
            if (_exception is not null)
            {
                return Task.FromException<HttpResponseMessage>(_exception);
            }

            Assert.NotEmpty(_responses);
            return Task.FromResult(_responses.Dequeue());
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (var response in _responses)
                {
                    response.Dispose();
                }
            }

            base.Dispose(disposing);
        }
    }
}
