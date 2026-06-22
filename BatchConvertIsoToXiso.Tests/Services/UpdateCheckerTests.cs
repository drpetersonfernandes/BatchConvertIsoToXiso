using System.Net;
using System.Text.Json;
using BatchConvertIsoToXiso.Models;
using BatchConvertIsoToXiso.Services;
using Moq;
using Moq.Protected;
using Xunit;

namespace BatchConvertIsoToXiso.Tests.Services;

public class UpdateCheckerTests
{
    private static HttpClient CreateHttpClient(HttpStatusCode statusCode, string content)
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = statusCode,
                Content = new StringContent(content)
            });
        return new HttpClient(handlerMock.Object);
    }

    private static string CreateReleaseJson(string tagName, string htmlUrl)
    {
        var release = new GitHubReleaseInfo { TagName = tagName, HtmlUrl = htmlUrl };
        return JsonSerializer.Serialize(release);
    }

    [Fact]
    public async Task CheckForUpdateAsyncNewVersionAvailableReturnsTrueAndVersionInfo()
    {
        var json = CreateReleaseJson("v2.4.0", "https://github.com/test/releases/tag/v2.4.0");
        var httpClient = CreateHttpClient(HttpStatusCode.OK, json);
        var checker = new UpdateChecker(httpClient, "2.3.1");

        var (isNew, latestVersion, downloadUrl) = await checker.CheckForUpdateAsync();

        Assert.True(isNew);
        Assert.Equal("2.4.0", latestVersion);
        Assert.Equal("https://github.com/test/releases/tag/v2.4.0", downloadUrl);
    }

    [Fact]
    public async Task CheckForUpdateAsyncSameVersionReturnsFalse()
    {
        var json = CreateReleaseJson("v2.3.1", "https://github.com/test/releases/tag/v2.3.1");
        var httpClient = CreateHttpClient(HttpStatusCode.OK, json);
        var checker = new UpdateChecker(httpClient, "2.3.1");

        var (isNew, latestVersion, downloadUrl) = await checker.CheckForUpdateAsync();

        Assert.False(isNew);
        Assert.Null(latestVersion);
        Assert.Null(downloadUrl);
    }

    [Fact]
    public async Task CheckForUpdateAsyncRemoteVersionOlderReturnsFalse()
    {
        var json = CreateReleaseJson("v2.2.0", "https://github.com/test/releases/tag/v2.2.0");
        var httpClient = CreateHttpClient(HttpStatusCode.OK, json);
        var checker = new UpdateChecker(httpClient, "2.3.1");

        var (isNew, latestVersion, downloadUrl) = await checker.CheckForUpdateAsync();

        Assert.False(isNew);
        Assert.Null(latestVersion);
        Assert.Null(downloadUrl);
    }

    [Fact]
    public async Task CheckForUpdateAsyncTagWithoutVersionPrefixDetectsVersion()
    {
        var json = CreateReleaseJson("release-3.0.0", "https://github.com/test/releases/tag/release-3.0.0");
        var httpClient = CreateHttpClient(HttpStatusCode.OK, json);
        var checker = new UpdateChecker(httpClient, "2.3.1");

        var (isNew, latestVersion, _) = await checker.CheckForUpdateAsync();

        Assert.True(isNew);
        Assert.Equal("3.0.0", latestVersion);
    }

    [Fact]
    public async Task CheckForUpdateAsyncNullTagNameReturnsFalse()
    {
        var json = CreateReleaseJson(null!, "https://github.com/test/releases/tag/v1.0.0");
        var httpClient = CreateHttpClient(HttpStatusCode.OK, json);
        var checker = new UpdateChecker(httpClient, "1.0.0");

        var (isNew, _, _) = await checker.CheckForUpdateAsync();

        Assert.False(isNew);
    }

    [Fact]
    public async Task CheckForUpdateAsyncNullHtmlUrlReturnsFalse()
    {
        var json = CreateReleaseJson("v2.0.0", null!);
        var httpClient = CreateHttpClient(HttpStatusCode.OK, json);
        var checker = new UpdateChecker(httpClient, "1.0.0");

        var (isNew, _, _) = await checker.CheckForUpdateAsync();

        Assert.False(isNew);
    }

    [Fact]
    public async Task CheckForUpdateAsyncNoVersionInTagReturnsFalse()
    {
        var json = CreateReleaseJson("latest-stable", "https://github.com/test/releases/tag/latest-stable");
        var httpClient = CreateHttpClient(HttpStatusCode.OK, json);
        var checker = new UpdateChecker(httpClient, "1.0.0");

        var (isNew, latestVersion, downloadUrl) = await checker.CheckForUpdateAsync();

        Assert.False(isNew);
        Assert.Null(latestVersion);
        Assert.Null(downloadUrl);
    }

    [Fact]
    public async Task CheckForUpdateAsyncHttpRequestFailsReturnsFalse()
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Network error"));
        var httpClient = new HttpClient(handlerMock.Object);
        var checker = new UpdateChecker(httpClient, "1.0.0");

        var (isNew, latestVersion, downloadUrl) = await checker.CheckForUpdateAsync();

        Assert.False(isNew);
        Assert.Null(latestVersion);
        Assert.Null(downloadUrl);
    }

    [Fact]
    public async Task CheckForUpdateAsyncInvalidJsonReturnsFalse()
    {
        var httpClient = CreateHttpClient(HttpStatusCode.OK, "{invalid-json");
        var checker = new UpdateChecker(httpClient, "1.0.0");

        var (isNew, latestVersion, downloadUrl) = await checker.CheckForUpdateAsync();

        Assert.False(isNew);
        Assert.Null(latestVersion);
        Assert.Null(downloadUrl);
    }

    [Fact]
    public async Task CheckForUpdateAsyncUnexpectedVersionFormatReturnsFalse()
    {
        var json = CreateReleaseJson("not-a-version-v1.2.3", "https://github.com/test/releases/tag/nonsense");
        var httpClient = CreateHttpClient(HttpStatusCode.OK, json);
        var checker = new UpdateChecker(httpClient, "not-a-valid-ver");

        var (isNew, _, _) = await checker.CheckForUpdateAsync();

        Assert.False(isNew);
    }

    [Fact]
    public async Task CheckForUpdateAsyncServerErrorReturnsFalse()
    {
        var httpClient = CreateHttpClient(HttpStatusCode.InternalServerError, "Server error");
        var checker = new UpdateChecker(httpClient, "1.0.0");

        var (isNew, latestVersion, downloadUrl) = await checker.CheckForUpdateAsync();

        Assert.False(isNew);
        Assert.Null(latestVersion);
        Assert.Null(downloadUrl);
    }

    [Fact]
    public async Task CheckForUpdateAsyncMajorVersionBumpReturnsTrue()
    {
        var json = CreateReleaseJson("v3.0.0", "https://github.com/test/releases/tag/v3.0.0");
        var httpClient = CreateHttpClient(HttpStatusCode.OK, json);
        var checker = new UpdateChecker(httpClient, "2.9.9");

        var (isNew, latestVersion, _) = await checker.CheckForUpdateAsync();

        Assert.True(isNew);
        Assert.Equal("3.0.0", latestVersion);
    }

    [Fact]
    public async Task CheckForUpdateAsyncFourComponentVersionReturnsTrue()
    {
        var json = CreateReleaseJson("v1.2.3.4", "https://github.com/test/releases/tag/v1.2.3.4");
        var httpClient = CreateHttpClient(HttpStatusCode.OK, json);
        var checker = new UpdateChecker(httpClient, "1.2.3.3");

        var (isNew, latestVersion, _) = await checker.CheckForUpdateAsync();

        Assert.True(isNew);
        Assert.Equal("1.2.3.4", latestVersion);
    }
}
