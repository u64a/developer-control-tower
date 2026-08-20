using ControlTower.Infrastructure.Yaml;

namespace ControlTower.Tests;

public class YamlScalarTests
{
    [Fact]
    public void Quote_PlainValue_WrapsInSingleQuotes()
    {
        Assert.Equal("'hello'", YamlScalar.Quote("hello"));
    }

    [Fact]
    public void Quote_Null_ReturnsEmptyQuoted()
    {
        Assert.Equal("''", YamlScalar.Quote(null));
    }

    [Fact]
    public void Quote_Empty_ReturnsEmptyQuoted()
    {
        Assert.Equal("''", YamlScalar.Quote(""));
    }

    [Fact]
    public void Quote_ContainsSingleQuote_DoublesIt()
    {
        Assert.Equal("'it''s fine'", YamlScalar.Quote("it's fine"));
    }

    [Fact]
    public void Quote_ContainsColon_StaysQuoted()
    {
        // The single-quote wrap protects against YAML key:value injection.
        Assert.Equal("'a: b'", YamlScalar.Quote("a: b"));
    }

    [Fact]
    public void Quote_ContainsBraces_StaysQuoted()
    {
        Assert.Equal("'{evil: payload}'", YamlScalar.Quote("{evil: payload}"));
    }

    [Fact]
    public void Quote_Newline_UsesDoubleQuotedWithEscape()
    {
        var result = YamlScalar.Quote("line1\nline2");
        Assert.Equal("\"line1\\nline2\"", result);
    }

    [Fact]
    public void Quote_BackslashAndQuote_EscapedInDoubleQuoted()
    {
        var result = YamlScalar.Quote("a\\b\nc\"d");
        Assert.Equal("\"a\\\\b\\nc\\\"d\"", result);
    }
}
