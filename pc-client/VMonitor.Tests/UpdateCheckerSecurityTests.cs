using System.Net;
using System.Text;
using VMonitor.UI;

namespace VMonitor.Tests;

public sealed class UpdateCheckerSecurityTests
{
    [Fact]
    public async Task Check_RequiresMatchingChecksumAsset()
    {
        const string installerUrl = "https://example.test/vmonitor-2.0.0-setup.exe";
        const string checksumUrl = installerUrl + ".sha256";
        var releases = $$"""
            [{
              "tag_name":"v2.0.0",
              "draft":false,
              "body":"notes",
              "assets":[
                {"name":"vmonitor-2.0.0-setup.exe","browser_download_url":"{{installerUrl}}","size":3},
                {"name":"vmonitor-2.0.0-setup.exe.sha256","browser_download_url":"{{checksumUrl}}","size":64}
              ]
            }]
            """;

        using var http = Client(_ => Text(releases, "application/json"));
        var update = await new UpdateChecker(new Version(1, 0, 0), http).CheckAsync();

        Assert.NotNull(update);
        Assert.Equal(checksumUrl, update.ChecksumUrl);
    }

    [Fact]
    public async Task Check_IgnoresExecutableWithoutChecksum()
    {
        const string releases = """
            [{
              "tag_name":"v2.0.0",
              "draft":false,
              "assets":[
                {"name":"vmonitor-2.0.0-setup.exe","browser_download_url":"https://example.test/setup.exe","size":3}
              ]
            }]
            """;

        using var http = Client(_ => Text(releases, "application/json"));
        var update = await new UpdateChecker(new Version(1, 0, 0), http).CheckAsync();

        Assert.Null(update);
    }

    [Fact]
    public async Task Download_DeletesExecutableWhenChecksumDoesNotMatch()
    {
        var tag = "test-" + Guid.NewGuid().ToString("N");
        var update = new AvailableUpdate(
            new Version(2, 0, 0), tag,
            "https://example.test/setup.exe",
            "https://example.test/setup.exe.sha256",
            3,
            string.Empty);

        using var http = Client(request =>
            request.RequestUri!.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal)
                ? Text(new string('0', 64), "text/plain")
                : Bytes([1, 2, 3]));

        var checker = new UpdateChecker(new Version(1, 0, 0), http);
        await Assert.ThrowsAsync<InvalidDataException>(() => checker.DownloadAsync(update));

        var expectedPath = Path.Combine(
            Path.GetTempPath(), "vmonitor-update", $"vmonitor-{tag}-setup.exe");
        Assert.False(File.Exists(expectedPath));
    }

    private static HttpClient Client(
        Func<HttpRequestMessage, HttpResponseMessage> response) =>
        new(new StubHandler(response));

    private static HttpResponseMessage Text(string value, string mediaType) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, mediaType),
    };

    private static HttpResponseMessage Bytes(byte[] value) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(value),
    };

    private sealed class StubHandler(
        Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }
}
