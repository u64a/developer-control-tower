using System.Security.Cryptography;
using ControlTower.Infrastructure.Ssh;
using Renci.SshNet;

namespace ControlTower.Tests;

public class SshNetClientFactoryTests
{
    private static readonly byte[] PresentedHostKey = { 1, 2, 3, 4, 5 };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CreatedClient_MissingPolicy_SetsCanTrustFalse(bool useSftp)
    {
        var factory = new SshNetClientFactory(null);
        using var client = CreateClient(factory, useSftp);
        var canTrust = true;

        var ex = Assert.Throws<HostKeyUnconfirmedException>(() =>
            factory.ApplyHostKeyValidation(client, PresentedHostKey, value => canTrust = value));

        Assert.False(canTrust);
        Assert.Equal("ssh/no-host-key-store", ex.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CreatedClient_Mismatch_SetsCanTrustFalse(bool useSftp)
    {
        var policy = new FixedPolicy(new HostKeyDecision(
            HostKeyVerdict.Mismatch,
            accept: false,
            "ssh/host-key-mismatch",
            "Host key mismatch."));
        var factory = new SshNetClientFactory(policy);
        using var client = CreateClient(factory, useSftp);
        var canTrust = true;

        Assert.Throws<HostKeyMismatchException>(() =>
            factory.ApplyHostKeyValidation(client, PresentedHostKey, value => canTrust = value));

        Assert.False(canTrust);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CreatedClient_AcceptedPolicy_SetsCanTrustTrue(bool useSftp)
    {
        var policy = new FixedPolicy(new HostKeyDecision(
            HostKeyVerdict.Trusted,
            accept: true,
            string.Empty,
            string.Empty));
        var factory = new SshNetClientFactory(policy);
        using var client = CreateClient(factory, useSftp);
        var canTrust = false;

        factory.ApplyHostKeyValidation(client, PresentedHostKey, value => canTrust = value);

        Assert.True(canTrust);
        Assert.Equal("host.example", policy.Host);
        Assert.Equal(2222, policy.Port);
        Assert.Equal(ExpectedFingerprint(PresentedHostKey), policy.Fingerprint);
    }

    private static BaseClient CreateClient(SshNetClientFactory factory, bool useSftp)
    {
        return useSftp
            ? factory.CreateSftpClient(
                "host.example", 2222, "user", "password", TimeSpan.FromSeconds(1))
            : factory.CreateSshClient(
                "host.example", 2222, "user", "password", TimeSpan.FromSeconds(1));
    }

    private static string ExpectedFingerprint(byte[] hostKey)
    {
        var hash = SHA256.HashData(hostKey);
        return "SHA256:" + Convert.ToBase64String(hash).TrimEnd('=');
    }

    private sealed class FixedPolicy : IHostKeyPolicy
    {
        private readonly HostKeyDecision _decision;

        public FixedPolicy(HostKeyDecision decision)
        {
            _decision = decision;
        }

        public string Host { get; private set; } = string.Empty;

        public int Port { get; private set; }

        public string Fingerprint { get; private set; } = string.Empty;

        public HostKeyDecision Evaluate(string host, int port, string fingerprint)
        {
            Host = host;
            Port = port;
            Fingerprint = fingerprint;
            return _decision;
        }
    }
}
