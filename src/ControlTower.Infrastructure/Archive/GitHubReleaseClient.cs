#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ControlTower.Core.Contracts;
using ControlTower.Core.Models;
using ControlTower.Infrastructure.Diagnostics;

namespace ControlTower.Infrastructure.Archive
{
    /// <summary>
    /// Read-only hosted-release API client. It captures every asset from one
    /// current/latest release exposed by GitHub or a Forgejo-compatible API,
    /// and verifies downloaded bytes when the provider supplies a SHA256
    /// digest. It never pushes, deletes, or mutates anything remotely.
    /// </summary>
    public sealed class GitHubReleaseClient : IArchiveReleaseClient, IDisposable
    {
        private const int MaximumDownloadRedirects = 5;

        private readonly HttpClient _http;
        private readonly string? _gitHubToken;

        public GitHubReleaseClient(string? gitHubToken = null)
            : this(CreateHttpMessageHandler(), gitHubToken)
        {
        }

        /// <summary>
        /// Creates a release client over a caller-supplied HTTP handler.
        /// The client owns and disposes <paramref name="handler"/>. This is
        /// primarily useful for deterministic offline callers and tests.
        /// </summary>
        public GitHubReleaseClient(HttpMessageHandler handler, string? gitHubToken)
        {
            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            if (handler is HttpClientHandler httpClientHandler)
            {
                httpClientHandler.AllowAutoRedirect = false;
            }

            _http = new HttpClient(handler, disposeHandler: true);
            _http.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("DeveloperControlTower", "1.0"));
            _gitHubToken = string.IsNullOrWhiteSpace(gitHubToken) ? null : gitHubToken;
        }

        public async Task<ArchiveReleaseInfo?> GetLatestReleaseAsync(string remoteUrl, CancellationToken ct)
        {
            if (!TryParseRepositoryTarget(remoteUrl, out var repository))
            {
                AppLogger.Warn(
                    "Archive",
                    "Release lookup skipped because the remote URL is not a supported HTTPS GitHub or Forgejo repository URL.");
                throw new NotSupportedException(
                    "The remote URL is not a supported HTTPS GitHub or Forgejo repository URL.");
            }

            var requestUrl = BuildLatestReleaseUri(repository);
            try
            {
                using var request = CreateGetRequest(requestUrl, acceptsJson: true);
                using var response = await _http.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    ct).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    await ConfirmRepositoryReleaseApiAsync(repository, ct).ConfigureAwait(false);
                    AppLogger.Info(
                        "Archive",
                        "No current release was found for " + repository.FullName + ".");
                    return null;
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException(
                        "Latest release lookup for " + repository.FullName +
                        " returned HTTP " + (int)response.StatusCode + ".");
                }

                var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidDataException("Latest release response root must be a JSON object.");
                }

                var tagName = GetRequiredString(root, "tag_name", "Latest release response");
                var name = GetString(root, "name");
                var body = GetString(root, "body");
                DateTime? publishedAt = root.TryGetProperty("published_at", out var publishedEl) &&
                    publishedEl.ValueKind == JsonValueKind.String &&
                    DateTime.TryParse(publishedEl.GetString(), out var parsedDate)
                        ? parsedDate.ToUniversalTime()
                        : null;

                var assets = ParseAssets(root);

                return new ArchiveReleaseInfo(tagName, name, body, publishedAt, assets);
            }
            catch (HttpRequestException ex)
            {
                AppLogger.Warn(
                    "Archive",
                    "Release lookup failed for " + repository.FullName + ": " + ex.Message);
                throw;
            }
            catch (NotSupportedException ex)
            {
                AppLogger.Warn(
                    "Archive",
                    "Release lookup is unsupported for " + repository.FullName + ": " + ex.Message);
                throw;
            }
            catch (JsonException ex)
            {
                AppLogger.Warn(
                    "Archive",
                    "Release response could not be parsed for " + repository.FullName + ": " + ex.Message);
                throw new InvalidDataException(
                    "Latest release response for " + repository.FullName + " is not valid JSON.",
                    ex);
            }
            catch (InvalidDataException ex)
            {
                AppLogger.Warn(
                    "Archive",
                    "Latest release response for " + repository.FullName + " is invalid: " + ex.Message);
                throw;
            }
            catch (TaskCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
        }

        public async Task<ArchiveReleaseIntegrity> DownloadAndVerifyAsync(
            ArchiveReleaseAsset asset,
            string destinationPath,
            CancellationToken ct)
        {
            try
            {
                if (asset == null || !TryGetSecureDownloadUri(asset.DownloadUrl, out var downloadUri))
                {
                    throw new HttpRequestException(
                        "Release asset URL must be an absolute HTTPS URL with a DNS host and no embedded credentials.");
                }

                var directory = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                using var response = await SendDownloadRequestAsync(downloadUri, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException(
                        "Release asset download returned HTTP " + (int)response.StatusCode + ".");
                }

                await using var httpStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var fileStream = File.Create(destinationPath);
                using var sha256 = SHA256.Create();
                await using var hashingStream = new CryptoStream(fileStream, sha256, CryptoStreamMode.Write);
                await httpStream.CopyToAsync(hashingStream, ct).ConfigureAwait(false);
                await hashingStream.FlushFinalBlockAsync(ct).ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(asset.DigestSha256))
                {
                    return ArchiveReleaseIntegrity.Unverifiable;
                }

                var computed = Convert.ToHexString(sha256.Hash ?? Array.Empty<byte>());
                return string.Equals(computed, asset.DigestSha256, StringComparison.OrdinalIgnoreCase)
                    ? ArchiveReleaseIntegrity.Verified
                    : ArchiveReleaseIntegrity.Mismatch;
            }
            catch (HttpRequestException ex)
            {
                AppLogger.Warn("Archive", "Release asset download failed: " + ex.Message);
                throw;
            }
            catch (TaskCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
        }

        public async Task<ArchiveUpstreamRepo?> ResolveUpstreamAsync(string remoteUrl, CancellationToken ct)
        {
            if (!TryParseRepositoryTarget(remoteUrl, out var repository))
            {
                AppLogger.Warn(
                    "Archive",
                    "Upstream lookup skipped because the remote URL is not a supported HTTPS GitHub or Forgejo repository URL.");
                return null;
            }

            // GitHub's "source" field is specifically the root repository
            // of a fork network. Forgejo does not promise the same field or
            // semantics, so preserve its configured remote as the source.
            if (repository.Provider != ReleaseProvider.GitHub)
            {
                return null;
            }

            var requestUrl = BuildRepositoryUri(repository);
            try
            {
                using var request = CreateGetRequest(requestUrl, acceptsJson: true);
                using var response = await _http.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    AppLogger.Warn(
                        "Archive",
                        "Upstream lookup for " + repository.FullName +
                        " returned HTTP " + (int)response.StatusCode + ".");
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var isFork = root.TryGetProperty("fork", out var forkEl) && forkEl.ValueKind == JsonValueKind.True;
                if (!isFork)
                {
                    return null;
                }

                if (!root.TryGetProperty("source", out var sourceEl) || sourceEl.ValueKind != JsonValueKind.Object)
                {
                    AppLogger.Warn(
                        "Archive",
                        "GitHub fork response for " + repository.FullName + " did not contain its source repository.");
                    return null;
                }

                var fullName = GetString(sourceEl, "full_name");
                var cloneUrl = GetString(sourceEl, "clone_url");
                var defaultBranch = GetString(sourceEl, "default_branch");
                if (!TryParseRepositoryTarget(cloneUrl, out var sourceRepository) ||
                    sourceRepository.Provider != ReleaseProvider.GitHub ||
                    !string.Equals(fullName, sourceRepository.FullName, StringComparison.OrdinalIgnoreCase))
                {
                    AppLogger.Warn(
                        "Archive",
                        "GitHub fork response for " + repository.FullName +
                        " contained an invalid source repository.");
                    return null;
                }

                return new ArchiveUpstreamRepo(
                    sourceRepository.Owner,
                    sourceRepository.Repo,
                    cloneUrl,
                    string.IsNullOrEmpty(defaultBranch) ? "main" : defaultBranch);
            }
            catch (HttpRequestException ex)
            {
                AppLogger.Warn("Archive", "Upstream lookup failed for " + repository.FullName + ": " + ex.Message);
                return null;
            }
            catch (JsonException ex)
            {
                AppLogger.Warn(
                    "Archive",
                    "Upstream lookup response could not be parsed for " + repository.FullName + ": " + ex.Message);
                return null;
            }
            catch (TaskCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
        }

        internal static bool TryParseOwnerRepo(string remoteUrl, out string owner, out string repo)
        {
            owner = string.Empty;
            repo = string.Empty;
            if (!TryParseRepositoryTarget(remoteUrl, out var repository))
            {
                return false;
            }

            owner = repository.Owner;
            repo = repository.Repo;
            return true;
        }

        private static HttpMessageHandler CreateHttpMessageHandler() =>
            new HttpClientHandler { AllowAutoRedirect = false };

        private static bool TryParseRepositoryTarget(string remoteUrl, out RepositoryTarget repository)
        {
            repository = null!;
            if (string.IsNullOrWhiteSpace(remoteUrl) ||
                !Uri.TryCreate(remoteUrl.Trim(), UriKind.Absolute, out var remoteUri) ||
                !IsSecureRepositoryUri(remoteUri))
            {
                return false;
            }

            var path = remoteUri.GetComponents(UriComponents.Path, UriFormat.Unescaped).Trim('/');
            var segments = path.Split('/', StringSplitOptions.None);
            if (segments.Length != 2 ||
                !IsSafeRepositorySegment(segments[0]) ||
                !IsSafeRepositorySegment(segments[1]))
            {
                return false;
            }

            var repo = segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)
                ? segments[1].Substring(0, segments[1].Length - ".git".Length)
                : segments[1];
            if (!IsSafeRepositorySegment(repo))
            {
                return false;
            }

            var provider = string.Equals(remoteUri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
                ? ReleaseProvider.GitHub
                : ReleaseProvider.Forgejo;
            if (provider == ReleaseProvider.GitHub && !remoteUri.IsDefaultPort && remoteUri.Port != 443)
            {
                return false;
            }

            repository = new RepositoryTarget(provider, remoteUri, segments[0], repo);
            return true;
        }

        private static bool IsSecureRepositoryUri(Uri uri) =>
            IsSecureHttpUri(uri) &&
            string.IsNullOrEmpty(uri.Query) &&
            string.IsNullOrEmpty(uri.Fragment);

        private static bool TryGetSecureDownloadUri(string? value, out Uri downloadUri)
        {
            downloadUri = null!;
            if (string.IsNullOrWhiteSpace(value) ||
                !Uri.TryCreate(value, UriKind.Absolute, out var parsed) ||
                !IsSecureHttpUri(parsed))
            {
                return false;
            }

            downloadUri = parsed;
            return true;
        }

        private static bool IsSecureHttpUri(Uri uri) =>
            uri.IsAbsoluteUri &&
            string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrEmpty(uri.UserInfo) &&
            string.IsNullOrEmpty(uri.Fragment) &&
            uri.HostNameType == UriHostNameType.Dns &&
            !string.IsNullOrEmpty(uri.Host);

        private static bool IsSafeRepositorySegment(string value)
        {
            if (string.IsNullOrWhiteSpace(value) ||
                string.Equals(value, ".", StringComparison.Ordinal) ||
                string.Equals(value, "..", StringComparison.Ordinal))
            {
                return false;
            }

            foreach (var character in value)
            {
                if (char.IsControl(character) ||
                    char.IsWhiteSpace(character) ||
                    character == '/' ||
                    character == '\\')
                {
                    return false;
                }
            }

            return true;
        }

        private static Uri BuildLatestReleaseUri(RepositoryTarget repository) =>
            BuildApiUri(repository, "/repos/" + Escape(repository.Owner) + "/" + Escape(repository.Repo) + "/releases/latest");

        private static Uri BuildRepositoryUri(RepositoryTarget repository) =>
            BuildApiUri(repository, "/repos/" + Escape(repository.Owner) + "/" + Escape(repository.Repo));

        private static Uri BuildApiUri(RepositoryTarget repository, string path)
        {
            if (repository.Provider == ReleaseProvider.GitHub)
            {
                return new Uri("https://api.github.com" + path, UriKind.Absolute);
            }

            var builder = new UriBuilder(
                repository.RemoteUri.Scheme,
                repository.RemoteUri.Host,
                repository.RemoteUri.IsDefaultPort ? -1 : repository.RemoteUri.Port)
            {
                Path = "/api/v1" + path
            };
            return builder.Uri;
        }

        private HttpRequestMessage CreateGetRequest(Uri requestUri, bool acceptsJson)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            if (acceptsJson)
            {
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(
                    IsGitHubFirstParty(requestUri)
                        ? "application/vnd.github+json"
                        : "application/json"));
            }

            if (!string.IsNullOrEmpty(_gitHubToken) && IsGitHubFirstParty(requestUri))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _gitHubToken);
            }

            return request;
        }

        private async Task<HttpResponseMessage> SendDownloadRequestAsync(Uri downloadUri, CancellationToken ct)
        {
            var currentUri = downloadUri;
            for (var redirectCount = 0; redirectCount <= MaximumDownloadRedirects; redirectCount++)
            {
                using var request = CreateGetRequest(currentUri, acceptsJson: false);
                var response = await _http.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    ct).ConfigureAwait(false);
                if (!IsRedirect(response.StatusCode))
                {
                    return response;
                }

                if (redirectCount == MaximumDownloadRedirects ||
                    !TryGetRedirectUri(currentUri, response.Headers.Location, out var nextUri))
                {
                    response.Dispose();
                    throw new HttpRequestException(
                        "Release asset download was redirected to an invalid location or exceeded the redirect limit.");
                }

                response.Dispose();
                currentUri = nextUri;
            }

            throw new HttpRequestException("Release asset download exceeded the redirect limit.");
        }

        private static bool TryGetRedirectUri(Uri currentUri, Uri? location, out Uri nextUri)
        {
            nextUri = null!;
            if (location == null)
            {
                return false;
            }

            try
            {
                var candidate = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
                if (!IsSecureHttpUri(candidate))
                {
                    return false;
                }

                nextUri = candidate;
                return true;
            }
            catch (UriFormatException)
            {
                return false;
            }
        }

        private static bool IsRedirect(HttpStatusCode statusCode)
        {
            var status = (int)statusCode;
            return status == 301 || status == 302 || status == 303 || status == 307 || status == 308;
        }

        private static bool IsGitHubFirstParty(Uri uri) =>
            string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            (uri.IsDefaultPort || uri.Port == 443) &&
            (string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(uri.Host, "api.github.com", StringComparison.OrdinalIgnoreCase));

        private static IReadOnlyList<ArchiveReleaseAsset> ParseAssets(JsonElement root)
        {
            var assets = new List<ArchiveReleaseAsset>();
            var capturedAssetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!root.TryGetProperty("assets", out var assetsEl) || assetsEl.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("Latest release response must contain an assets array.");
            }

            foreach (var assetEl in assetsEl.EnumerateArray())
            {
                if (assetEl.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidDataException("Every release asset must be a JSON object.");
                }

                var assetName = GetRequiredString(assetEl, "name", "Release asset");
                var downloadUrl = GetRequiredString(assetEl, "browser_download_url", "Release asset");
                if (!TryGetSecureDownloadUri(downloadUrl, out _))
                {
                    throw new InvalidDataException(
                        "Release asset '" + assetName +
                        "' has an invalid browser_download_url. Asset URLs must be absolute HTTPS URLs.");
                }

                var size = 0L;
                if (assetEl.TryGetProperty("size", out var sizeEl))
                {
                    if (sizeEl.ValueKind != JsonValueKind.Number ||
                        !sizeEl.TryGetInt64(out size) ||
                        size < 0)
                    {
                        throw new InvalidDataException(
                            "Release asset '" + assetName + "' has an invalid size.");
                    }
                }

                string? digest = null;
                if (assetEl.TryGetProperty("digest", out var digestEl))
                {
                    if (digestEl.ValueKind != JsonValueKind.Null &&
                        digestEl.ValueKind != JsonValueKind.String)
                    {
                        throw new InvalidDataException(
                            "Release asset '" + assetName + "' has an invalid digest.");
                    }

                    var raw = digestEl.ValueKind == JsonValueKind.String
                        ? digestEl.GetString()
                        : null;
                    if (!string.IsNullOrEmpty(raw) && raw.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    {
                        digest = raw.Substring("sha256:".Length);
                    }
                }

                if (!capturedAssetNames.Add(ArchiveSyncService.Slugify(assetName)))
                {
                    throw new InvalidDataException(
                        "Release asset '" + assetName +
                        "' collides with another asset after local filename normalization.");
                }

                assets.Add(new ArchiveReleaseAsset(assetName, downloadUrl, size, digest));
            }

            return assets;
        }

        private async Task ConfirmRepositoryReleaseApiAsync(RepositoryTarget repository, CancellationToken ct)
        {
            using var request = CreateGetRequest(BuildRepositoryUri(repository), acceptsJson: true);
            using var response = await _http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    "Could not confirm that " + repository.FullName +
                    " supports its release API after /releases/latest returned HTTP 404; " +
                    "repository metadata returned HTTP " + (int)response.StatusCode + ".");
            }

            try
            {
                var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidDataException(
                        "Repository metadata confirmation must be a JSON object.");
                }

                var fullName = GetRequiredString(root, "full_name", "Repository metadata confirmation");
                if (!string.Equals(fullName, repository.FullName, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "Repository metadata confirmation did not match " + repository.FullName + ".");
                }
            }
            catch (JsonException ex)
            {
                AppLogger.Warn(
                    "Archive",
                    "Repository metadata confirmation could not be parsed for " +
                    repository.FullName + ": " + ex.Message);
                throw new InvalidDataException(
                    "Repository metadata confirmation for " + repository.FullName + " is not valid JSON.",
                    ex);
            }
            catch (InvalidDataException ex)
            {
                AppLogger.Warn(
                    "Archive",
                    "Repository metadata confirmation for " + repository.FullName +
                    " is invalid: " + ex.Message);
                throw;
            }
        }

        private static string Escape(string value) => Uri.EscapeDataString(value);

        private static string GetString(JsonElement element, string propertyName) =>
            element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;

        private static string GetRequiredString(JsonElement element, string propertyName, string context)
        {
            if (!element.TryGetProperty(propertyName, out var value) ||
                value.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(value.GetString()))
            {
                throw new InvalidDataException(context + " must contain a non-empty " + propertyName + " string.");
            }

            return value.GetString()!;
        }

        public void Dispose() => _http.Dispose();

        private enum ReleaseProvider
        {
            GitHub,
            Forgejo
        }

        private sealed class RepositoryTarget
        {
            public RepositoryTarget(ReleaseProvider provider, Uri remoteUri, string owner, string repo)
            {
                Provider = provider;
                RemoteUri = remoteUri;
                Owner = owner;
                Repo = repo;
            }

            public ReleaseProvider Provider { get; }
            public Uri RemoteUri { get; }
            public string Owner { get; }
            public string Repo { get; }
            public string FullName => Owner + "/" + Repo;
        }
    }
}
