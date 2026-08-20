namespace ControlTower.Core.Models
{
    public sealed class RepoStore
    {
        public RepoStore()
        {
            Id = string.Empty;
            Type = "local";
            Root = string.Empty;
            Host = string.Empty;
            User = string.Empty;
            CredentialTarget = string.Empty;
        }

        public string Id { get; set; }

        /// <summary>"local" or "ssh"</summary>
        public string Type { get; set; }

        /// <summary>Root path for repos in this store (e.g. C:\Repos or D:\repos).</summary>
        public string Root { get; set; }

        /// <summary>SSH hostname or IP. Empty for local stores.</summary>
        public string Host { get; set; }

        /// <summary>SSH username. Empty for local stores.</summary>
        public string User { get; set; }

        /// <summary>Windows Credential Manager target for SSH password.</summary>
        public string CredentialTarget { get; set; }

        /// <summary>SSH port. Defaults to 22 when zero or negative.</summary>
        public int Port { get; set; }

        public bool IsSsh => string.Equals(Type, "ssh", System.StringComparison.OrdinalIgnoreCase);
    }
}
