#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ControlTower.Core.Contracts;
using ControlTower.Core.Models;
using ControlTower.Infrastructure.Diagnostics;

namespace ControlTower.Infrastructure.Archive
{
    /// <summary>
    /// Read-only GitHub Releases API client. Captures the latest release's
    /// metadata/assets and verifies downloaded bytes against the
    /// GitHub-computed SHA256 digest (available on assets uploaded since
    /// June 2025) before the caller is allowed to treat a download as
    /// trustworthy. Never pushes, deletes, or mutates anything on GitHub.
    /// </summary>
    public sealed class GitHubReleaseClient : IArchiveReleaseClient, IDisposable
    {
        private static readonly Regex GitHubRepoPattern = new(
            @"^https://github\.com/(?<owner>[^/]+)/(?<repo>[^/]+?)(\.git)?/?$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly HttpClient _http;

        public GitHubReleaseClient(string? gitHubToken = null)
        {
            _http = new HttpClient();
            _http.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("DeveloperControlTower", "1.0"));
            _http.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            if (!string.IsNullOrWhiteSpace(gitHubToken))
            {
                _http.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", gitHubToken);
            }
        }

        public async Task<ArchiveReleaseInfo?> GetLatestReleaseAsync(string remoteUrl, CancellationToken ct)
        {
            if (!TryParseOwnerRepo(remoteUrl, out var owner, out var repo))
            {
                return null;
            }

            var requestUrl = $"https://api.github.com/repos/{owner}/{repo}/releases/latest";
            try
            {
                using var response = await _http.GetAsync(requestUrl, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    // 404 == no releases published; treated the same as "none
                    // available" rather than an error.
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var tagName = GetString(root, "tag_name");
                var name = GetString(root, "name");
                var body = GetString(root, "body");
                DateTime? publishedAt = root.TryGetProperty("published_at", out var publishedEl) &&
                    publishedEl.ValueKind == JsonValueKind.String &&
                    DateTime.TryParse(publishedEl.GetString(), out var parsedDate)
                        ? parsedDate.ToUniversalTime()
                        : null;

                var assets = new List<ArchiveReleaseAsset>();
                if (root.TryGetProperty("assets", out var assetsEl) && assetsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var assetEl in assetsEl.EnumerateArray())
                    {
                        var assetName = GetString(assetEl, "name");
                        var downloadUrl = GetString(assetEl, "browser_download_url");
                        var size = assetEl.TryGetProperty("size", out var sizeEl) && sizeEl.TryGetInt64(out var sizeValue)
                            ? sizeValue
                            : 0;
                        string? digest = null;
                        if (assetEl.TryGetProperty("digest", out var digestEl) && digestEl.ValueKind == JsonValueKind.String)
                        {
                            var raw = digestEl.GetString();
                            if (!string.IsNullOrEmpty(raw) && raw.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                            {
                                digest = raw.Substring("sha256:".Length);
                            }
                        }

                        if (!string.IsNullOrEmpty(assetName) && !string.IsNullOrEmpty(downloadUrl))
                        {
                            assets.Add(new ArchiveReleaseAsset(assetName, downloadUrl, size, digest));
                        }
                    }
                }

                if (string.IsNullOrEmpty(tagName))
                {
                    return null;
                }

                return new ArchiveReleaseInfo(tagName, name, body, publishedAt, assets);
            }
            catch (HttpRequestException ex)
            {
                AppLogger.Warn("Archive", "GitHub release lookup failed for " + owner + "/" + repo + ": " + ex.Message);
                return null;
            }
            catch (JsonException ex)
            {
                AppLogger.Warn("Archive", "GitHub release response could not be parsed: " + ex.Message);
                return null;
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
            if (asset == null || string.IsNullOrWhiteSpace(asset.DownloadUrl))
            {
                return ArchiveReleaseIntegrity.Unverifiable;
            }

            var directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using (var response = await _http.GetAsync(
                asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
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
        }

        public async Task<ArchiveUpstreamRepo?> ResolveUpstreamAsync(string remoteUrl, CancellationToken ct)
        {
            if (!TryParseOwnerRepo(remoteUrl, out var owner, out var repo))
            {
                return null;
            }

            var requestUrl = $"https://api.github.com/repos/{owner}/{repo}";
            try
            {
                using var response = await _http.GetAsync(requestUrl, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
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

                // "source" is the root (non-fork) repo this fork ultimately
                // descends from, even through a chain of forks — exactly the
                // original project we want to track, not an intermediate fork.
                if (!root.TryGetProperty("source", out var sourceEl) || sourceEl.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                var fullName = GetString(sourceEl, "full_name");
                var cloneUrl = GetString(sourceEl, "clone_url");
                var defaultBranch = GetString(sourceEl, "default_branch");
                if (string.IsNullOrEmpty(fullName) || string.IsNullOrEmpty(cloneUrl))
                {
                    return null;
                }

                var parts = fullName.Split('/');
                if (parts.Length != 2)
                {
                    return null;
                }

                return new ArchiveUpstreamRepo(
                    parts[0], parts[1], cloneUrl, string.IsNullOrEmpty(defaultBranch) ? "main" : defaultBranch);
            }
            catch (HttpRequestException ex)
            {
                AppLogger.Warn("Archive", "Upstream lookup failed for " + owner + "/" + repo + ": " + ex.Message);
                return null;
            }
            catch (JsonException ex)
            {
                AppLogger.Warn("Archive", "Upstream lookup response could not be parsed: " + ex.Message);
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
            if (string.IsNullOrWhiteSpace(remoteUrl))
            {
                return false;
            }

            var match = GitHubRepoPattern.Match(remoteUrl.Trim());
            if (!match.Success)
            {
                return false;
            }

            owner = match.Groups["owner"].Value;
            repo = match.Groups["repo"].Value;
            return true;
        }

        private static string GetString(JsonElement element, string propertyName) =>
            element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;

        public void Dispose() => _http.Dispose();
    }
}
