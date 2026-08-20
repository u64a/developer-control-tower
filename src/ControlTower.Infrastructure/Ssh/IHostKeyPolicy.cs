using System;
using ControlTower.Core.Contracts;

namespace ControlTower.Infrastructure.Ssh
{
    /// <summary>
    /// Verdict returned by an <see cref="IHostKeyPolicy"/> when evaluating a
    /// server-presented host key fingerprint. Per ADR-005 the SSH service must
    /// fail closed when no policy is available and on any mismatch.
    /// </summary>
    public enum HostKeyVerdict
    {
        /// <summary>Fingerprint matches a previously trusted record.</summary>
        Trusted,

        /// <summary>No prior fingerprint was recorded for this host. The
        /// strict policy treats this as failure until a UI confirmation is
        /// captured (ADR-005 §2). A TOFU test double may accept and record.</summary>
        FirstSeen,

        /// <summary>Stored fingerprint differs from the one presented.</summary>
        Mismatch,

        /// <summary>No host-key store is available; cannot verify. Per
        /// ADR-005 §1 this MUST fail the connection.</summary>
        NoStore
    }

    /// <summary>
    /// Decision an SSH client should take in response to a host-key verdict.
    /// </summary>
    public sealed class HostKeyDecision
    {
        public HostKeyDecision(HostKeyVerdict verdict, bool accept, string code, string message)
        {
            Verdict = verdict;
            Accept = accept;
            Code = code ?? string.Empty;
            Message = message ?? string.Empty;
        }

        public HostKeyVerdict Verdict { get; }

        /// <summary>True iff the SSH client should proceed with the connection.</summary>
        public bool Accept { get; }

        /// <summary>Machine-readable failure code when <see cref="Accept"/> is false.</summary>
        public string Code { get; }

        public string Message { get; }
    }

    /// <summary>
    /// Policy that decides whether a server's host-key fingerprint is trusted.
    /// See ADR-005. Implementations MUST fail closed on missing storage and on
    /// any mismatch.
    /// </summary>
    public interface IHostKeyPolicy
    {
        HostKeyDecision Evaluate(string host, int port, string fingerprint);
    }

    /// <summary>
    /// Production policy: rejects everything except an exact match with a
    /// previously stored fingerprint. First-seen hosts are rejected because
    /// ADR-005 §2 requires explicit user confirmation captured via the
    /// desktop UI before the fingerprint is persisted.
    /// </summary>
    public sealed class StrictHostKeyPolicy : IHostKeyPolicy
    {
        private const string HostKeyTargetPrefix = "DCT-SshHostKey-";

        private readonly ICredentialStore _credentialStore;

        public StrictHostKeyPolicy(ICredentialStore credentialStore)
        {
            _credentialStore = credentialStore;
        }

        public HostKeyDecision Evaluate(string host, int port, string fingerprint)
        {
            if (_credentialStore == null)
            {
                return new HostKeyDecision(
                    HostKeyVerdict.NoStore,
                    accept: false,
                    "ssh/no-host-key-store",
                    "No host-key store is available. Refusing to connect (ADR-005).");
            }

            var target = HostKeyTargetPrefix + (host ?? string.Empty) + ":" + port;
            var stored = _credentialStore.GetPassword(target);

            if (string.IsNullOrEmpty(stored))
            {
                return new HostKeyDecision(
                    HostKeyVerdict.FirstSeen,
                    accept: false,
                    "ssh/host-key-unconfirmed",
                    $"No trusted host key recorded for {host}:{port}. Confirm and save the fingerprint before connecting.");
            }

            if (string.Equals(stored, fingerprint, StringComparison.Ordinal))
            {
                return new HostKeyDecision(HostKeyVerdict.Trusted, accept: true, string.Empty, string.Empty);
            }

            return new HostKeyDecision(
                HostKeyVerdict.Mismatch,
                accept: false,
                "ssh/host-key-mismatch",
                $"SSH host key for {host}:{port} does not match the trusted fingerprint. " +
                "If the host changed legitimately, remove the saved fingerprint via " +
                $"Credential Manager (target: {target}) and reconnect.");
        }
    }

    /// <summary>
    /// Test-only policy that auto-persists first-seen fingerprints and
    /// continues to verify on subsequent connections. Never use in production
    /// per ADR-005.
    /// </summary>
    public sealed class TrustOnFirstUseHostKeyPolicy : IHostKeyPolicy
    {
        private const string HostKeyTargetPrefix = "DCT-SshHostKey-";

        private readonly ICredentialStore _credentialStore;

        public TrustOnFirstUseHostKeyPolicy(ICredentialStore credentialStore)
        {
            _credentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
        }

        public HostKeyDecision Evaluate(string host, int port, string fingerprint)
        {
            var target = HostKeyTargetPrefix + (host ?? string.Empty) + ":" + port;
            var stored = _credentialStore.GetPassword(target);

            if (string.IsNullOrEmpty(stored))
            {
                _credentialStore.SetPassword(target, fingerprint ?? string.Empty);
                return new HostKeyDecision(HostKeyVerdict.FirstSeen, accept: true, string.Empty, string.Empty);
            }

            if (string.Equals(stored, fingerprint, StringComparison.Ordinal))
            {
                return new HostKeyDecision(HostKeyVerdict.Trusted, accept: true, string.Empty, string.Empty);
            }

            return new HostKeyDecision(
                HostKeyVerdict.Mismatch,
                accept: false,
                "ssh/host-key-mismatch",
                $"SSH host key for {host}:{port} does not match the trusted fingerprint.");
        }
    }
}
