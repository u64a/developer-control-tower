using System;
using System.IO;
using ControlTower.Infrastructure.Diagnostics;

namespace ControlTower.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AppLoggerSerialCollection
{
    public const string Name = "AppLogger serial";
}

[Collection(AppLoggerSerialCollection.Name)]
public class AppLoggerRedactionTests
{
    [Fact]
    public void Redact_GitHubPat_Removed()
    {
        var input = "Auth header: ghp_" + "AbCdEfGhIjKlMnOpQrStUvWxYz1234567890";
        var output = AppLogger.Redact(input);
        Assert.DoesNotContain("ghp_" + "AbCdEfGhIjKlMnOpQrStUvWxYz1234567890", output);
        Assert.Contains("[redacted", output);
    }

    [Fact]
    public void Redact_GitHubPatNewFormat_Removed()
    {
        var input = "token=github_pat_" + "11ABCDEFG0AbCdEfGhIjKlMnOpQrStUvWxYz12345678";
        var output = AppLogger.Redact(input);
        Assert.DoesNotContain("github_pat_11ABCDEFG0", output);
    }

    [Fact]
    public void Redact_Bearer_Removed()
    {
        var input = "Authorization is Bearer eyJabc.def.ghi-quite-a-long-token-value-123456";
        var output = AppLogger.Redact(input);
        Assert.DoesNotContain("eyJabc.def.ghi-quite-a-long-token-value-123456", output);
        Assert.Contains("[redacted", output);
    }

    [Fact]
    public void Redact_AuthorizationHeader_Removed()
    {
        // The Authorization regex consumes the whole header value, so the
        // secret token must be replaced regardless of internal spaces.
        var input = "Sending Authorization: Basic ZGVtbzpzZWNyZXQ=";
        var output = AppLogger.Redact(input);
        Assert.DoesNotContain("ZGVtbzpzZWNyZXQ", output);
    }

    [Fact]
    public void Redact_PasswordParam_Removed()
    {
        var input = "url?user=alice&password=hunter2&next=home";
        var output = AppLogger.Redact(input);
        Assert.DoesNotContain("hunter2", output);
        Assert.Contains("password=[redacted]", output);
    }

    [Fact]
    public void Redact_PlainShortText_Untouched()
    {
        var input = "Loaded project demo";
        var output = AppLogger.Redact(input);
        Assert.Equal(input, output);
    }

    [Fact]
    public void Redact_GitSha1_Preserved()
    {
        // Full 40-char hex commit SHA must survive the high-entropy
        // backstop - otherwise UpdateService logging shows "[redacte"
        // instead of the actual commit and the self-update script's
        // begin marker becomes useless.
        const string sha = "77aa1fed27c8e9f0a1b2c3d4e5f6a7b8c9d0e1f2";
        var input = "current=" + sha + " remote=" + sha + " ahead=0 behind=0";
        var output = AppLogger.Redact(input);
        Assert.Contains("current=" + sha, output);
        Assert.Contains("remote=" + sha, output);
    }

    [Fact]
    public void Redact_GitSha256_Preserved()
    {
        // 64-char hex SHA-256 (git's experimental object format) is also
        // preserved by the same rule.
        const string sha = "1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef";
        var input = "blob=" + sha;
        var output = AppLogger.Redact(input);
        Assert.Contains("blob=" + sha, output);
    }

    [Fact]
    public void Redact_NonShaHighEntropyBlob_StillRedacted()
    {
        // 40+ char alphanumeric strings that are NOT pure hex (e.g. mixed
        // case beyond [a-f], digits and underscores) are still redacted -
        // the SHA exemption must not become an attack surface.
        const string blob = "AbCdEfGhIjKlMnOpQrStUvWxYz1234567890XYZW";
        var input = "raw=" + blob + " trailing";
        var output = AppLogger.Redact(input);
        Assert.DoesNotContain(blob, output);
        Assert.Contains("[redacted-token]", output);
    }

    [Fact]
    public void Redact_OffSizeHexBlob_StillRedacted()
    {
        // Hex-only but not 40 or 64 chars (here 48) is not a git SHA - it
        // is treated as an unknown high-entropy blob.
        const string blob = "deadbeefcafebabe0123456789abcdef0011223344556677";
        var input = "key=" + blob;
        var output = AppLogger.Redact(input);
        Assert.DoesNotContain(blob, output);
        Assert.Contains("[redacted-token]", output);
    }

    [Fact]
    public void Error_WithExpectedException_DoesNotIncludeFullStackTrace()
    {
        // Route the logger to a per-test folder so we can inspect output.
        var tempFolder = Path.Combine(Path.GetTempPath(), "ct-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempFolder);
        SetLogFolder(tempFolder);
        try
        {
            FileNotFoundException ex;
            try { throw new FileNotFoundException("missing thing"); }
            catch (FileNotFoundException caught) { ex = caught; }

            AppLogger.Error("test", "could not find file", ex);

            var logFile = Directory.GetFiles(tempFolder, "app-*.log")[0];
            var content = File.ReadAllText(logFile);
            Assert.Contains("FileNotFoundException", content);
            Assert.Contains("missing thing", content);
            // For an expected exception the full call stack must not appear.
            Assert.DoesNotContain("at ControlTower.Tests", content);
        }
        finally
        {
            ResetLogFolder();
            try { Directory.Delete(tempFolder, true); } catch { }
        }
    }

    [Fact]
    public void Error_WithUnexpectedException_RedactsSecretsInStackText()
    {
        var tempFolder = Path.Combine(Path.GetTempPath(), "ct-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempFolder);
        SetLogFolder(tempFolder);
        try
        {
            InvalidOperationException ex;
            try { throw new InvalidOperationException("token=ghp_" + "AbCdEfGhIjKlMnOpQrStUvWxYz1234567890 blew up"); }
            catch (InvalidOperationException caught) { ex = caught; }

            AppLogger.Error("test", "boom", ex);

            var logFile = Directory.GetFiles(tempFolder, "app-*.log")[0];
            var content = File.ReadAllText(logFile);
            Assert.DoesNotContain("ghp_" + "AbCdEfGhIjKlMnOpQrStUvWxYz1234567890", content);
        }
        finally
        {
            ResetLogFolder();
            try { Directory.Delete(tempFolder, true); } catch { }
        }
    }

    // The static log folder field is private — flip it via reflection for
    // these tests so we don't pollute the real LocalAppData log.
    private static void SetLogFolder(string path)
    {
        var field = typeof(AppLogger).GetField("_logFolder",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        field!.SetValue(null, path);
    }

    private static void ResetLogFolder() => SetLogFolder(null!);
}
