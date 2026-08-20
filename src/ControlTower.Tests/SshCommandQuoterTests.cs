using System;
using ControlTower.Core.Ssh;

namespace ControlTower.Tests;

/// <summary>
/// Tests for <see cref="SshCommandQuoter"/>. The quoter is the only thing
/// standing between a user-supplied folder name / path and the remote
/// shell, so the cases below cover normal paths plus every metacharacter
/// that could change command semantics on cmd.exe or POSIX sh.
/// </summary>
public class SshCommandQuoterTests
{
    [Fact]
    public void QuoteWindows_WrapsPlainPathInDoubleQuotes()
    {
        var result = SshCommandQuoter.QuoteWindows(@"D:\repos\sample");
        Assert.Equal("\"D:\\repos\\sample\"", result);
    }

    [Theory]
    [InlineData("%OS%")]
    [InlineData("!PATH!")]
    public void QuoteWindows_RejectsEnvironmentExpansion(string value)
    {
        Assert.Throws<ArgumentException>(() => SshCommandQuoter.QuoteWindows(value));
    }

    [Fact]
    public void QuoteWindows_RejectsEmbeddedDoubleQuote()
    {
        Assert.Throws<ArgumentException>(() => SshCommandQuoter.QuoteWindows("a\"b"));
    }

    [Fact]
    public void QuoteWindows_AllowsSpacesAndSpecials()
    {
        // Spaces, ampersands, pipes are neutralised by the wrapping quotes.
        var result = SshCommandQuoter.QuoteWindows("a b & c | d");
        Assert.Equal("\"a b & c | d\"", result);
    }

    [Fact]
    public void QuotePosix_WrapsPlainPathInSingleQuotes()
    {
        var result = SshCommandQuoter.QuotePosix("/home/user/repos/sample");
        Assert.Equal("'/home/user/repos/sample'", result);
    }

    [Fact]
    public void QuotePosix_EscapesEmbeddedSingleQuote()
    {
        var result = SshCommandQuoter.QuotePosix("it's");
        Assert.Equal("'it'\\''s'", result);
    }

    [Fact]
    public void QuotePosix_NeutralisesDollarAndBackticks()
    {
        // Single-quoting in sh preserves $ and ` literally.
        var result = SshCommandQuoter.QuotePosix("$(rm -rf /) `whoami`");
        Assert.Equal("'$(rm -rf /) `whoami`'", result);
    }

    [Theory]
    [InlineData("a\nb")]
    [InlineData("a\rb")]
    [InlineData("a\0b")]
    public void Quote_RejectsControlCharacters(string evil)
    {
        Assert.Throws<ArgumentException>(() => SshCommandQuoter.QuoteWindows(evil));
        Assert.Throws<ArgumentException>(() => SshCommandQuoter.QuotePosix(evil));
    }

    [Fact]
    public void Quote_RejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => SshCommandQuoter.QuoteWindows(null!));
        Assert.Throws<ArgumentNullException>(() => SshCommandQuoter.QuotePosix(null!));
    }
}
