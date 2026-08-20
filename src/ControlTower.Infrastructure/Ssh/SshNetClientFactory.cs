using System;
using System.Security.Cryptography;
using Renci.SshNet;

namespace ControlTower.Infrastructure.Ssh
{
    /// <summary>
    /// Creates SSH.NET clients with fail-closed host-key validation attached.
    /// </summary>
    public sealed class SshNetClientFactory
    {
        private readonly IHostKeyPolicy _hostKeyPolicy;

        public SshNetClientFactory(IHostKeyPolicy hostKeyPolicy)
        {
            _hostKeyPolicy = hostKeyPolicy;
        }

        internal bool HasHostKeyPolicy => _hostKeyPolicy != null;

        internal void EnsureHostKeyPolicyConfigured()
        {
            if (!HasHostKeyPolicy)
            {
                throw new HostKeyUnconfirmedException(
                    "ssh/no-host-key-store",
                    "No host-key policy is configured. Refusing to connect (ADR-005).");
            }
        }

        public SshClient CreateSshClient(
            string host,
            int port,
            string user,
            string password,
            TimeSpan timeout)
        {
            var client = new SshClient(CreateConnectionInfo(host, port, user, password, timeout));
            ConfigureHostKeyValidation(client);
            return client;
        }

        public SftpClient CreateSftpClient(
            string host,
            int port,
            string user,
            string password,
            TimeSpan timeout)
        {
            var client = new SftpClient(CreateConnectionInfo(host, port, user, password, timeout));
            ConfigureHostKeyValidation(client);
            return client;
        }

        internal HostKeyDecision EvaluateFingerprint(string host, int port, string fingerprint)
        {
            if (_hostKeyPolicy == null)
            {
                return new HostKeyDecision(
                    HostKeyVerdict.NoStore,
                    accept: false,
                    "ssh/no-host-key-store",
                    "No host-key policy is configured. Refusing to connect (ADR-005).");
            }

            return _hostKeyPolicy.Evaluate(host, port, fingerprint);
        }

        /// <summary>
        /// Narrow seam used by both SSH.NET event handlers and unit tests.
        /// The trust setter is set false before policy evaluation so failures
        /// cannot retain SSH.NET's default trust value.
        /// </summary>
        internal void ApplyHostKeyValidation(
            BaseClient client,
            byte[] hostKey,
            Action<bool> setCanTrust)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (hostKey == null) throw new ArgumentNullException(nameof(hostKey));
            if (setCanTrust == null) throw new ArgumentNullException(nameof(setCanTrust));

            setCanTrust(false);

            var decision = EvaluateFingerprint(
                client.ConnectionInfo.Host,
                client.ConnectionInfo.Port,
                ComputeFingerprint(hostKey));

            if (decision.Accept)
            {
                setCanTrust(true);
                return;
            }

            if (decision.Verdict == HostKeyVerdict.Mismatch)
            {
                throw new HostKeyMismatchException(decision.Message);
            }

            throw new HostKeyUnconfirmedException(decision.Code, decision.Message);
        }

        private static ConnectionInfo CreateConnectionInfo(
            string host,
            int port,
            string user,
            string password,
            TimeSpan timeout)
        {
            return new ConnectionInfo(
                host,
                port,
                user,
                new PasswordAuthenticationMethod(user, password))
            {
                Timeout = timeout
            };
        }

        private void ConfigureHostKeyValidation(BaseClient client)
        {
            client.HostKeyReceived += (_, e) =>
                ApplyHostKeyValidation(client, e.HostKey, canTrust => e.CanTrust = canTrust);
        }

        private static string ComputeFingerprint(byte[] hostKey)
        {
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(hostKey);
            return "SHA256:" + Convert.ToBase64String(hash).TrimEnd('=');
        }
    }

    /// <summary>Raised when a stored SSH host key fingerprint does not match the server.</summary>
    public sealed class HostKeyMismatchException : Exception
    {
        public HostKeyMismatchException(string message) : base(message) { }
    }

    /// <summary>
    /// Raised when an SSH connection cannot proceed because the host key has
    /// not been explicitly trusted.
    /// </summary>
    public sealed class HostKeyUnconfirmedException : Exception
    {
        public HostKeyUnconfirmedException(string code, string message) : base(message)
        {
            Code = code ?? string.Empty;
        }

        public string Code { get; }
    }
}
