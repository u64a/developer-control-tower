using System.Collections.Generic;
using ControlTower.Core.Contracts;
using ControlTower.Infrastructure.Ssh;

namespace ControlTower.Tests;

public class SshNetServiceTests
{
    private sealed class FakeCredentialStore : ICredentialStore
    {
        private readonly Dictionary<string, string> _store = new();
        public int SetCount { get; private set; }

        public string GetPassword(string target)
            => _store.TryGetValue(target, out var v) ? v : string.Empty;

        public void SetPassword(string target, string password)
        {
            _store[target] = password;
            SetCount++;
        }

        public void DeletePassword(string target) => _store.Remove(target);
    }

    // --- SshNetService level: refuses to run without a host-key policy. ---

    [Fact]
    public void TestConnection_NoPolicy_ReturnsUnavailable()
    {
        var svc = new SshNetService();
        var result = svc.TestConnection("host", 22, "user", "password");

        Assert.False(result.Success);
        Assert.Equal("ssh/no-host-key-store", result.Code);
    }

    [Fact]
    public void RunCommand_NoPolicy_ReturnsUnavailable()
    {
        var svc = new SshNetService();
        var result = svc.RunCommand("host", 22, "user", "password", "echo hi");

        Assert.False(result.Success);
        Assert.Equal("ssh/no-host-key-store", result.Code);
    }

    [Fact]
    public void EvaluateHostKey_NoPolicy_NoStoreVerdict()
    {
        var svc = new SshNetService();
        var decision = svc.GetType().GetMethod("EvaluateHostKey",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            !.Invoke(svc, new object[] { "host", 22, "fp" }) as HostKeyDecision;

        Assert.NotNull(decision);
        Assert.False(decision!.Accept);
        Assert.Equal(HostKeyVerdict.NoStore, decision.Verdict);
        Assert.Equal("ssh/no-host-key-store", decision.Code);
    }

    // --- StrictHostKeyPolicy: fails closed on no-store, first-seen, mismatch. ---

    [Fact]
    public void Strict_NoCredentialStore_NoStoreVerdict()
    {
        IHostKeyPolicy policy = new StrictHostKeyPolicy(null);
        var d = policy.Evaluate("h", 22, "SHA256:abc");
        Assert.False(d.Accept);
        Assert.Equal(HostKeyVerdict.NoStore, d.Verdict);
        Assert.Equal("ssh/no-host-key-store", d.Code);
    }

    [Fact]
    public void Strict_FirstTimeHost_Rejects()
    {
        var store = new FakeCredentialStore();
        IHostKeyPolicy policy = new StrictHostKeyPolicy(store);

        var d = policy.Evaluate("host", 22, "SHA256:fp");

        Assert.False(d.Accept);
        Assert.Equal(HostKeyVerdict.FirstSeen, d.Verdict);
        Assert.Equal("ssh/host-key-unconfirmed", d.Code);
        // Strict must not silently persist the fingerprint.
        Assert.Equal(0, store.SetCount);
    }

    [Fact]
    public void Strict_MatchingFingerprint_Trusts()
    {
        var store = new FakeCredentialStore();
        store.SetPassword("DCT-SshHostKey-host:22", "SHA256:fp");

        IHostKeyPolicy policy = new StrictHostKeyPolicy(store);
        var d = policy.Evaluate("host", 22, "SHA256:fp");

        Assert.True(d.Accept);
        Assert.Equal(HostKeyVerdict.Trusted, d.Verdict);
    }

    [Fact]
    public void Strict_FingerprintMismatch_Rejects()
    {
        var store = new FakeCredentialStore();
        store.SetPassword("DCT-SshHostKey-host:22", "SHA256:trusted");

        IHostKeyPolicy policy = new StrictHostKeyPolicy(store);
        var d = policy.Evaluate("host", 22, "SHA256:rogue");

        Assert.False(d.Accept);
        Assert.Equal(HostKeyVerdict.Mismatch, d.Verdict);
        Assert.Equal("ssh/host-key-mismatch", d.Code);
    }

    // --- TrustOnFirstUseHostKeyPolicy: test-only TOFU behaviour. ---

    [Fact]
    public void Tofu_FirstSeen_AcceptsAndPersists()
    {
        var store = new FakeCredentialStore();
        IHostKeyPolicy policy = new TrustOnFirstUseHostKeyPolicy(store);

        var d = policy.Evaluate("host", 22, "SHA256:fp");

        Assert.True(d.Accept);
        Assert.Equal(HostKeyVerdict.FirstSeen, d.Verdict);
        Assert.Equal("SHA256:fp", store.GetPassword("DCT-SshHostKey-host:22"));
    }

    [Fact]
    public void Tofu_Mismatch_Rejects()
    {
        var store = new FakeCredentialStore();
        store.SetPassword("DCT-SshHostKey-host:22", "SHA256:trusted");

        IHostKeyPolicy policy = new TrustOnFirstUseHostKeyPolicy(store);
        var d = policy.Evaluate("host", 22, "SHA256:rogue");

        Assert.False(d.Accept);
        Assert.Equal(HostKeyVerdict.Mismatch, d.Verdict);
        Assert.Equal("ssh/host-key-mismatch", d.Code);
    }

    [Fact]
    public void Tofu_Match_Trusts()
    {
        var store = new FakeCredentialStore();
        store.SetPassword("DCT-SshHostKey-host:22", "SHA256:fp");

        IHostKeyPolicy policy = new TrustOnFirstUseHostKeyPolicy(store);
        var d = policy.Evaluate("host", 22, "SHA256:fp");

        Assert.True(d.Accept);
        Assert.Equal(HostKeyVerdict.Trusted, d.Verdict);
    }
}
