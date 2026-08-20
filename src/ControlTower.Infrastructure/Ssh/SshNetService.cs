using System;
using ControlTower.Core.Contracts;
using ControlTower.Core.Ssh;
using Renci.SshNet;

namespace ControlTower.Infrastructure.Ssh
{
    public sealed class SshNetService : ISshService
    {
        private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(10);

        private readonly SshNetClientFactory _clientFactory;

        public SshNetService()
            : this((IHostKeyPolicy)null)
        {
        }

        public SshNetService(ICredentialStore credentialStore)
            : this(new StrictHostKeyPolicy(credentialStore))
        {
        }

        public SshNetService(IHostKeyPolicy hostKeyPolicy)
            : this(new SshNetClientFactory(hostKeyPolicy))
        {
        }

        public SshNetService(SshNetClientFactory clientFactory)
        {
            _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        }

        /// <summary>
        /// Evaluates a host-key fingerprint against the configured policy.
        /// Exposed for tests so the fail-closed behaviour can be verified
        /// without standing up a real SSH server.
        /// </summary>
        internal HostKeyDecision EvaluateHostKey(string host, int port, string fingerprint)
        {
            return _clientFactory.EvaluateFingerprint(host, port, fingerprint);
        }

        public SshResult TestConnection(string host, int port, string user, string password)
        {
            var unavailable = CheckPolicyAvailable();
            if (unavailable != null)
            {
                return unavailable;
            }

            try
            {
                using var client = CreateClient(host, port, user, password);
                client.Connect();
                client.Disconnect();
                return SshResult.Ok("Connection successful");
            }
            catch (HostKeyMismatchException ex)
            {
                return SshResult.Fail("ssh/host-key-mismatch", ex.Message);
            }
            catch (HostKeyUnconfirmedException ex)
            {
                return SshResult.Fail(ex.Code, ex.Message);
            }
            catch (Exception)
            {
                return SshResult.Fail("Connection failed. Verify host, port, user, and password.");
            }
        }

        public SshResult CreateDirectory(string host, int port, string user, string password, string remotePath)
        {
            // Detect remote OS: try PowerShell (Windows) first, fall back to mkdir -p (Linux/macOS)
            var detect = RunCommand(host, port, user, password, "echo %OS%");
            bool isWindows = detect.Success && detect.Output.Contains("Windows", StringComparison.OrdinalIgnoreCase);

            string mkdirCmd;
            try
            {
                mkdirCmd = isWindows
                    ? $"if not exist {SshCommandQuoter.QuoteWindows(remotePath)} mkdir {SshCommandQuoter.QuoteWindows(remotePath)}"
                    : $"mkdir -p {SshCommandQuoter.QuotePosix(remotePath)}";
            }
            catch (ArgumentException)
            {
                return SshResult.Fail("ssh/unsafe-path", "Refusing to build a remote command: the path contains unsafe characters.");
            }

            return RunCommand(host, port, user, password, mkdirCmd);
        }

        public SshResult RunCommand(string host, int port, string user, string password, string command)
        {
            var unavailable = CheckPolicyAvailable();
            if (unavailable != null)
            {
                return unavailable;
            }

            try
            {
                using var client = CreateClient(host, port, user, password);
                client.Connect();

                using var cmd = client.RunCommand(command);
                if (cmd.ExitStatus != 0 && !string.IsNullOrEmpty(cmd.Error))
                {
                    return SshResult.Fail(cmd.Error);
                }

                return SshResult.Ok(cmd.Result?.TrimEnd() ?? string.Empty);
            }
            catch (HostKeyMismatchException ex)
            {
                return SshResult.Fail("ssh/host-key-mismatch", ex.Message);
            }
            catch (HostKeyUnconfirmedException ex)
            {
                return SshResult.Fail(ex.Code, ex.Message);
            }
            catch (Exception)
            {
                return SshResult.Fail("SSH command failed. Verify host, port, user, and password.");
            }
        }

        private SshResult CheckPolicyAvailable()
        {
            if (!_clientFactory.HasHostKeyPolicy)
            {
                return SshResult.Unavailable(
                    "ssh/no-host-key-store",
                    "SSH refused: no host-key policy is configured. " +
                    "Configure a credential store so host fingerprints can be verified (ADR-005).");
            }

            return null;
        }

        private SshClient CreateClient(string host, int port, string user, string password)
        {
            return _clientFactory.CreateSshClient(
                host,
                port,
                user,
                password,
                ConnectionTimeout);
        }
    }
}
