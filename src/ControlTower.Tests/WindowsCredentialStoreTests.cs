using System;
using System.Runtime.InteropServices;
using ControlTower.Infrastructure.Credentials;

namespace ControlTower.Tests;

// WindowsCredentialStore wraps the Win32 Credential Manager. These tests run
// only on Windows; on other platforms they early-return so the suite stays
// green cross-platform. Test targets use a guid-prefixed name so a flaky run
// cannot collide with real user credentials.
public class WindowsCredentialStoreTests : IDisposable
{
    private readonly string _prefix;
    private readonly List<string> _createdTargets = new();
    private readonly WindowsCredentialStore _store = new();

    public WindowsCredentialStoreTests()
    {
        _prefix = "ct-test-" + Guid.NewGuid().ToString("N") + "-";
    }

    public void Dispose()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        foreach (var target in _createdTargets)
        {
            try { _store.DeletePassword(target); } catch { /* best-effort */ }
        }
    }

    private string NewTarget(string suffix)
    {
        var t = _prefix + suffix;
        _createdTargets.Add(t);
        return t;
    }

    private static bool SkipIfNotWindows() => !RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    [Fact]
    public void SetThenGet_RoundtripsValue()
    {
        if (SkipIfNotWindows()) return;
        var target = NewTarget("roundtrip");

        _store.SetPassword(target, "s3cret-token!");
        var roundTripped = _store.GetPassword(target);

        Assert.Equal("s3cret-token!", roundTripped);
    }

    [Fact]
    public void Get_MissingTarget_ReturnsEmptyString()
    {
        if (SkipIfNotWindows()) return;
        var target = NewTarget("missing");
        // We never set this target.

        var value = _store.GetPassword(target);

        Assert.Equal(string.Empty, value);
    }

    [Fact]
    public void Delete_RemovesValue_SoNextGetIsEmpty()
    {
        if (SkipIfNotWindows()) return;
        var target = NewTarget("delete");
        _store.SetPassword(target, "to-be-deleted");
        Assert.Equal("to-be-deleted", _store.GetPassword(target));

        _store.DeletePassword(target);

        Assert.Equal(string.Empty, _store.GetPassword(target));
    }

    [Fact]
    public void Set_OverwritesExistingValue_NoCollisionError()
    {
        if (SkipIfNotWindows()) return;
        var target = NewTarget("collide");

        _store.SetPassword(target, "first");
        _store.SetPassword(target, "second");

        Assert.Equal("second", _store.GetPassword(target));
    }

    [Fact]
    public void Get_NullOrWhitespaceTarget_ReturnsEmpty()
    {
        if (SkipIfNotWindows()) return;

        Assert.Equal(string.Empty, _store.GetPassword(null!));
        Assert.Equal(string.Empty, _store.GetPassword(""));
        Assert.Equal(string.Empty, _store.GetPassword("   "));
    }

    [Fact]
    public void Set_NullOrWhitespaceTarget_NoThrow_NotPersisted()
    {
        if (SkipIfNotWindows()) return;

        // Implementation contract: silently ignore invalid targets rather
        // than throw, and definitely don't create a credential with an empty
        // target name in the user's vault.
        _store.SetPassword(null!, "value");
        _store.SetPassword("", "value");
        _store.SetPassword("   ", "value");

        Assert.Equal(string.Empty, _store.GetPassword(""));
    }

    [Fact]
    public void Delete_NullOrWhitespaceTarget_NoThrow()
    {
        if (SkipIfNotWindows()) return;

        _store.DeletePassword(null!);
        _store.DeletePassword("");
        _store.DeletePassword("   ");
    }

    [Fact]
    public void Delete_MissingTarget_NoThrow()
    {
        if (SkipIfNotWindows()) return;
        var target = NewTarget("never-set");

        // Calling delete on a target that was never written must be a no-op,
        // not an exception — credential cleanup paths rely on this.
        _store.DeletePassword(target);
    }

    [Fact]
    public void Set_EmptyPassword_RoundtripsAsEmpty()
    {
        if (SkipIfNotWindows()) return;
        var target = NewTarget("empty-pwd");

        _store.SetPassword(target, string.Empty);

        Assert.Equal(string.Empty, _store.GetPassword(target));
    }

    [Fact]
    public void Set_NullPassword_TreatedAsEmpty()
    {
        if (SkipIfNotWindows()) return;
        var target = NewTarget("null-pwd");

        _store.SetPassword(target, null!);

        Assert.Equal(string.Empty, _store.GetPassword(target));
    }

    [Fact]
    public void Set_UnicodePassword_RoundtripsExactly()
    {
        if (SkipIfNotWindows()) return;
        var target = NewTarget("unicode");
        var value = "pä$$wörd-✓-日本語";

        _store.SetPassword(target, value);

        Assert.Equal(value, _store.GetPassword(target));
    }
}
