using ControlTower.Core.Models;

namespace ControlTower.Tests;

public class UrlSanitizerTests
{
    [Fact]
    public void StripCredentials_HttpsUserPass_Stripped()
    {
        var stripped = UrlSanitizer.StripCredentials("https:/" + "/user:token@github.com/owner/repo.git");
        Assert.Equal("https://github.com/owner/repo.git", stripped);
    }

    [Fact]
    public void StripCredentials_HttpsBearerToken_Stripped()
    {
        var stripped = UrlSanitizer.StripCredentials("https:/" + "/ghp_abc123@github.com/owner/repo.git");
        Assert.Equal("https://github.com/owner/repo.git", stripped);
    }

    [Fact]
    public void StripCredentials_SshUserPassword_KeepsUserDropsPassword()
    {
        var stripped = UrlSanitizer.StripCredentials("ssh://git:secret@github.com/owner/repo.git");
        Assert.Equal("ssh://git@github.com/owner/repo.git", stripped);
    }

    [Fact]
    public void StripCredentials_SshUserOnly_Preserved()
    {
        var stripped = UrlSanitizer.StripCredentials("ssh://git@github.com/owner/repo.git");
        Assert.Equal("ssh://git@github.com/owner/repo.git", stripped);
    }

    [Fact]
    public void StripCredentials_ScpLike_Unchanged()
    {
        var input = "git@github.com:owner/repo.git";
        Assert.Equal(input, UrlSanitizer.StripCredentials(input));
    }

    [Fact]
    public void StripCredentials_NoUserInfo_PassesThrough()
    {
        // Plain git URLs without any user-info should be unchanged.
        Assert.Equal("https://github.com/foo/bar.git",
            UrlSanitizer.StripCredentials("https://github.com/foo/bar.git"));
        Assert.Equal("https://github.com:8080/foo/bar.git",
            UrlSanitizer.StripCredentials("https://github.com:8080/foo/bar.git"));
    }

    [Fact]
    public void StripCredentials_Empty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, UrlSanitizer.StripCredentials(""));
        Assert.Equal(string.Empty, UrlSanitizer.StripCredentials("   "));
    }

    [Fact]
    public void HasCredentials_HttpsAnyUserInfo_True()
    {
        Assert.True(UrlSanitizer.HasCredentials("https:/" + "/user:pass@github.com/x/y.git"));
        Assert.True(UrlSanitizer.HasCredentials("https:/" + "/token@github.com/x/y.git"));
        Assert.True(UrlSanitizer.HasCredentials("https:/" + "/x-access-token@github.com/x/y.git"));
    }

    [Fact]
    public void HasCredentials_HttpsNoUserInfo_False()
    {
        Assert.False(UrlSanitizer.HasCredentials("https://github.com/x/y.git"));
    }

    [Fact]
    public void HasCredentials_SshUserOnly_False()
    {
        Assert.False(UrlSanitizer.HasCredentials("ssh://git@github.com/x/y.git"));
    }

    [Fact]
    public void HasCredentials_SshWithPassword_True()
    {
        Assert.True(UrlSanitizer.HasCredentials("ssh://git:secret@github.com/x/y.git"));
    }

    [Fact]
    public void HasCredentials_ScpLike_False()
    {
        Assert.False(UrlSanitizer.HasCredentials("git@github.com:owner/repo.git"));
    }

    [Fact]
    public void HasCredentials_Empty_False()
    {
        Assert.False(UrlSanitizer.HasCredentials(""));
        Assert.False(UrlSanitizer.HasCredentials(null!));
    }
}
