using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ControlTower.Core.Models;
using ControlTower.Infrastructure.Archive;

namespace ControlTower.Tests;

public class GitHubReleaseClientTests
{
    [Theory]
    [InlineData("https://forge.example.test/eden-emu/eden.git", "https://forge.example.test/api/v1/repos/eden-emu/eden/releases/latest")]
    [InlineData("https://forge.example.test:8443/eden-emu/eden/", "https://forge.example.test:8443/api/v1/repos/eden-emu/eden/releases/latest")]
    public async Task GetLatestReleaseAsync_Forgejo_PreservesEveryPlatformAndBuildWithoutSendingGitHubToken(
        string remote,
        string expectedApi)
    {
        using var handler = new ReleaseHttpHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ForgejoReleaseFixture.Json)
            }));
        using var client = new GitHubReleaseClient(handler, "test-github-token");

        var release = await client.GetLatestReleaseAsync(remote, CancellationToken.None);

        Assert.NotNull(release);
        Assert.Equal("v0.2.1", release.TagName);
        Assert.Equal(ForgejoReleaseFixture.AssetNames, release.Assets.Select(asset => asset.Name));
        Assert.All(release.Assets, asset =>
        {
            Assert.Equal(0, asset.SizeBytes);
            Assert.Null(asset.DigestSha256);
            Assert.Equal(ForgejoReleaseFixture.DownloadUrl(asset.Name), asset.DownloadUrl);
        });
        var request = Assert.Single(handler.Requests);
        Assert.Equal(expectedApi, request.Uri.AbsoluteUri);
        Assert.Null(request.Authorization);
        Assert.Equal("application/json", request.Accept);
    }

    [Fact]
    public async Task GetLatestReleaseAsync_GitHub_UsesExistingApiAndScopedToken()
    {
        using var handler = new ReleaseHttpHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"tag_name":"v1","assets":[{"name":"app.zip","browser_download_url":"https://downloads.example.test/app.zip","size":123,"digest":"sha256:abcd"}]}
                    """)
            }));
        using var client = new GitHubReleaseClient(handler, "test-github-token");

        var release = await client.GetLatestReleaseAsync("https://github.com/org/repo.git", CancellationToken.None);

        Assert.NotNull(release);
        Assert.Equal("abcd", Assert.Single(release.Assets).DigestSha256);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.github.com/repos/org/repo/releases/latest", request.Uri.AbsoluteUri);
        Assert.Equal("Bearer test-github-token", request.Authorization);
        Assert.Equal("application/vnd.github+json", request.Accept);
    }

    [Fact]
    public async Task ResolveUpstreamAsync_GitHub_PreservesRootSourceLookup()
    {
        using var handler = new ReleaseHttpHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"fork":true,"source":{"full_name":"upstream/original","clone_url":"https://github.com/upstream/original.git","default_branch":"master"}}
                    """)
            }));
        using var client = new GitHubReleaseClient(handler, "test-github-token");

        var upstream = await client.ResolveUpstreamAsync("https://github.com/user/fork.git", CancellationToken.None);

        Assert.NotNull(upstream);
        Assert.Equal("upstream/original", upstream.FullName);
        Assert.Equal("master", upstream.DefaultBranch);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.github.com/repos/user/fork", request.Uri.AbsoluteUri);
        Assert.Equal("Bearer test-github-token", request.Authorization);
    }

    [Theory]
    [InlineData("http://forge.example.test/org/repo.git")]
    [InlineData("https://user:password@forge.example.test/org/repo.git")]
    [InlineData("https://forge.example.test/org/repo.git?token=example")]
    [InlineData("https://forge.example.test/org/repo.git#fragment")]
    [InlineData("https://forge.example.test/org%2Frepo/other.git")]
    [InlineData("https://127.0.0.1/org/repo.git")]
    [InlineData("https://github.com:8443/org/repo.git")]
    [InlineData("https://dev.azure.com/org/project/_git/repo")]
    public async Task GetLatestReleaseAsync_UnsupportedOrUnsafeRemote_DoesNotSendRequest(string remote)
    {
        using var handler = new ReleaseHttpHandler((_, _) =>
            throw new InvalidOperationException("An unsafe remote must not be requested."));
        using var client = new GitHubReleaseClient(handler, "test-github-token");

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            client.GetLatestReleaseAsync(remote, CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task GetLatestReleaseAsync_UnsuccessfulLookup_FailsInsteadOfClaimingNoRelease(HttpStatusCode status)
    {
        using var handler = new ReleaseHttpHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(status)));
        using var client = new GitHubReleaseClient(handler, gitHubToken: null);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetLatestReleaseAsync(
            "https://forge.example.test/org/repo.git", CancellationToken.None));
    }

    [Fact]
    public async Task GetLatestReleaseAsync_NoCurrentRelease_ConfirmsRepositoryBeforeReturningNull()
    {
        using var handler = new ReleaseHttpHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("/releases/latest", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"id":1,"full_name":"org/repo"}""")
                }));
        using var client = new GitHubReleaseClient(handler, gitHubToken: null);

        Assert.Null(await client.GetLatestReleaseAsync(
            "https://forge.example.test/org/repo.git", CancellationToken.None));
        Assert.Equal(
            new[]
            {
                "https://forge.example.test/api/v1/repos/org/repo/releases/latest",
                "https://forge.example.test/api/v1/repos/org/repo"
            },
            handler.Requests.Select(request => request.Uri.AbsoluteUri));
    }

    [Theory]
    [InlineData("<html>Unrelated website</html>")]
    [InlineData("""{"message":"not a repository"}""")]
    public async Task GetLatestReleaseAsync_RepositoryProbeIsNotMetadata_DoesNotClaimNoRelease(string body)
    {
        using var handler = new ReleaseHttpHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("/releases/latest", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }));
        using var client = new GitHubReleaseClient(handler, gitHubToken: null);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetLatestReleaseAsync(
            "https://forge.example.test/org/repo.git", CancellationToken.None));
    }

    [Fact]
    public async Task GetLatestReleaseAsync_ValidReleaseWithoutBinaryAssets_PreservesSourceOnlyRelease()
    {
        using var handler = new ReleaseHttpHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"tag_name":"v1","assets":[]}""")
            }));
        using var client = new GitHubReleaseClient(handler, gitHubToken: null);

        var release = await client.GetLatestReleaseAsync(
            "https://forge.example.test/org/repo.git", CancellationToken.None);

        Assert.NotNull(release);
        Assert.Equal("v1", release.TagName);
        Assert.Empty(release.Assets);
    }

    [Fact]
    public async Task GetLatestReleaseAsync_NullDigest_PreservesAssetWithoutInventingChecksum()
    {
        using var handler = new ReleaseHttpHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"tag_name":"v1","assets":[{"name":"app.zip","browser_download_url":"https://downloads.example.test/app.zip","size":0,"digest":null}]}
                    """)
            }));
        using var client = new GitHubReleaseClient(handler, gitHubToken: null);

        var release = await client.GetLatestReleaseAsync(
            "https://github.com/org/repo.git", CancellationToken.None);

        Assert.NotNull(release);
        Assert.Null(Assert.Single(release.Assets).DigestSha256);
    }

    [Fact]
    public async Task GetLatestReleaseAsync_CancelledRequest_PropagatesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new ReleaseHttpHandler((_, token) =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<HttpResponseMessage>(token);
        });
        using var client = new GitHubReleaseClient(handler, gitHubToken: null);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetLatestReleaseAsync(
            "https://forge.example.test/org/repo.git", cancellation.Token));
    }

    [Theory]
    [InlineData("not JSON")]
    [InlineData("[]")]
    [InlineData("""{"assets":[]}""")]
    [InlineData("""{"tag_name":"v0.2.1"}""")]
    [InlineData("""{"tag_name":"v0.2.1","assets":{}}""")]
    [InlineData("""{"tag_name":"v0.2.1","assets":[null]}""")]
    [InlineData("""{"tag_name":"v0.2.1","assets":[{"name":"app.apk"}]}""")]
    [InlineData("""{"tag_name":"v0.2.1","assets":[{"name":"valid.zip","browser_download_url":"https://downloads.example.test/valid.zip"},{"name":"missing.apk"}]}""")]
    [InlineData("""{"tag_name":"v0.2.1","assets":[{"name":"","browser_download_url":"https://downloads.example.test/app.apk"}]}""")]
    [InlineData("""{"tag_name":"v0.2.1","assets":[{"name":"app.apk","browser_download_url":"http://downloads.example.test/app.apk"}]}""")]
    [InlineData("""{"tag_name":"v0.2.1","assets":[{"name":"app.apk","browser_download_url":"https://downloads.example.test/app.apk","size":"invalid"}]}""")]
    public async Task GetLatestReleaseAsync_MalformedMetadata_FailsInsteadOfOmittingAssets(string json)
    {
        using var handler = new ReleaseHttpHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) }));
        using var client = new GitHubReleaseClient(handler, gitHubToken: null);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetLatestReleaseAsync(
            "https://forge.example.test/org/repo.git", CancellationToken.None));
    }

    [Fact]
    public async Task DownloadAndVerifyAsync_ZeroSizeExternalAsset_WritesBytesAndRemainsUnverifiable()
    {
        var directory = Path.Combine(Path.GetTempPath(), "release-zero-size-" + Guid.NewGuid().ToString("N"));
        var destination = Path.Combine(directory, "app.apk");
        var bytes = Encoding.UTF8.GetBytes("nonempty Android package fixture");
        using var handler = new ReleaseHttpHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
        using var client = new GitHubReleaseClient(handler, "test-github-token");
        try
        {
            var integrity = await client.DownloadAndVerifyAsync(
                new ArchiveReleaseAsset("app.apk", "https://downloads.example.test/app.apk", 0, null),
                destination,
                CancellationToken.None);

            Assert.Equal(ArchiveReleaseIntegrity.Unverifiable, integrity);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(destination));
            Assert.Null(Assert.Single(handler.Requests).Authorization);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DownloadAndVerifyAsync_CrossOriginRedirect_ScopesTokenAndChecksDigest(bool matchingDigest)
    {
        var directory = Path.Combine(Path.GetTempPath(), "release-redirect-" + Guid.NewGuid().ToString("N"));
        var destination = Path.Combine(directory, "app.zip");
        var bytes = Encoding.UTF8.GetBytes("Windows package fixture");
        using var handler = new ReleaseHttpHandler((request, _) =>
        {
            if (request.RequestUri!.Host == "api.github.com")
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri("https://downloads.example.test/app.zip?signature=example");
                return Task.FromResult(redirect);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes)
            });
        });
        using var client = new GitHubReleaseClient(handler, "test-github-token");
        try
        {
            var digest = Convert.ToHexString(SHA256.HashData(matchingDigest ? bytes : new byte[] { 0 }));
            var integrity = await client.DownloadAndVerifyAsync(
                new ArchiveReleaseAsset("app.zip", "https://api.github.com/repos/org/repo/releases/assets/1", 0, digest),
                destination,
                CancellationToken.None);

            Assert.Equal(
                matchingDigest ? ArchiveReleaseIntegrity.Verified : ArchiveReleaseIntegrity.Mismatch,
                integrity);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(destination));
            Assert.Equal(2, handler.Requests.Count);
            Assert.Equal("Bearer test-github-token", handler.Requests[0].Authorization);
            Assert.Null(handler.Requests[1].Authorization);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("http://downloads.example.test/app.zip")]
    [InlineData("https://user:password@downloads.example.test/app.zip")]
    [InlineData("https://downloads.example.test/app.zip#fragment")]
    [InlineData("https://127.0.0.1/app.zip")]
    public async Task DownloadAndVerifyAsync_UnsafeRedirect_RejectsBeforeWriting(string target)
    {
        var directory = Path.Combine(Path.GetTempPath(), "release-unsafe-redirect-" + Guid.NewGuid().ToString("N"));
        var destination = Path.Combine(directory, "app.zip");
        using var handler = new ReleaseHttpHandler((_, _) =>
        {
            var redirect = new HttpResponseMessage(HttpStatusCode.Found);
            redirect.Headers.Location = new Uri(target);
            return Task.FromResult(redirect);
        });
        using var client = new GitHubReleaseClient(handler, gitHubToken: null);
        try
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => client.DownloadAndVerifyAsync(
                new ArchiveReleaseAsset("app.zip", "https://downloads.example.test/redirect", 0, null),
                destination,
                CancellationToken.None));

            Assert.Single(handler.Requests);
            Assert.False(File.Exists(destination));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}

internal sealed class ReleaseHttpHandler(
    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    internal sealed record Request(Uri Uri, string? Authorization, string Accept);
    public List<Request> Requests { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(new Request(
            request.RequestUri!,
            request.Headers.Authorization?.ToString(),
            string.Join(",", request.Headers.Accept)));
        return respond(request, cancellationToken);
    }
}

internal static class ForgejoReleaseFixture
{
    public static readonly string[] AssetNames =
    [
        "Eden-Android-v0.2.1-chromeos.apk",
        "Eden-Android-v0.2.1-legacy.apk",
        "Eden-Android-v0.2.1-optimized.apk",
        "Eden-Android-v0.2.1-standard.apk",
        "Eden-Linux-aarch64-clang-pgo.AppImage.zsync",
        "Eden-Linux-aarch64-gcc-standard.AppImage.zsync",
        "Eden-Linux-amd64-clang-pgo.AppImage.zsync",
        "Eden-Linux-amd64-gcc-standard.AppImage.zsync",
        "Eden-Linux-legacy-clang-pgo.AppImage.zsync",
        "Eden-Linux-legacy-gcc-standard.AppImage.zsync",
        "Eden-Linux-rog-ally-clang-pgo.AppImage.zsync",
        "Eden-Linux-rog-ally-gcc-standard.AppImage.zsync",
        "Eden-Linux-steamdeck-clang-pgo.AppImage.zsync",
        "Eden-Linux-steamdeck-gcc-standard.AppImage.zsync",
        "Eden-Linux-v0.2.1-aarch64-clang-pgo.AppImage",
        "Eden-Linux-v0.2.1-aarch64-gcc-standard.AppImage",
        "Eden-Linux-v0.2.1-amd64-clang-pgo.AppImage",
        "Eden-Linux-v0.2.1-amd64-gcc-standard.AppImage",
        "Eden-Linux-v0.2.1-legacy-clang-pgo.AppImage",
        "Eden-Linux-v0.2.1-legacy-gcc-standard.AppImage",
        "Eden-Linux-v0.2.1-rog-ally-clang-pgo.AppImage",
        "Eden-Linux-v0.2.1-rog-ally-gcc-standard.AppImage",
        "Eden-Linux-v0.2.1-steamdeck-clang-pgo.AppImage",
        "Eden-Linux-v0.2.1-steamdeck-gcc-standard.AppImage",
        "Eden-Windows-v0.2.1-amd64-clang-pgo.zip",
        "Eden-Windows-v0.2.1-amd64-gcc-standard.zip",
        "Eden-Windows-v0.2.1-amd64-msvc-standard.zip",
        "Eden-Windows-v0.2.1-arm64-clang-pgo.zip",
        "Eden-Windows-v0.2.1-arm64-clang-standard.zip",
        "Eden-Windows-v0.2.1-rog-ally-clang-pgo.zip",
        "Eden-Windows-v0.2.1-rog-ally-gcc-standard.zip",
        "Eden-macOS-v0.2.1.dmg",
        "Eden-v0.2.1.torrent",
        "eden-room-aarch64-unknown-linux-musl",
        "eden-room-x86_64-unknown-linux-musl"
    ];

    public static string DownloadUrl(string name) => "https://downloads.example.test/v0.2.1/" + name;
    public static byte[] Contents(string name) => Encoding.UTF8.GetBytes("Release asset fixture: " + name);

    public static string Json => JsonSerializer.Serialize(new
    {
        tag_name = "v0.2.1",
        name = "Eden v0.2.1",
        body = "Current stable release",
        published_at = "2026-06-01T17:57:41Z",
        prerelease = false,
        draft = false,
        assets = AssetNames.Select(name => new { name, browser_download_url = DownloadUrl(name), size = 0 })
    });

    public static Task<HttpResponseMessage> Respond(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri!.AbsoluteUri == "https://forge.example.test/api/v1/repos/eden-emu/eden/releases/latest")
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Json) });
        }

        var name = AssetNames.SingleOrDefault(name => DownloadUrl(name) == request.RequestUri.AbsoluteUri);
        if (name == null)
        {
            throw new InvalidOperationException("Unexpected release request: " + request.RequestUri);
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Contents(name))
        });
    }
}
