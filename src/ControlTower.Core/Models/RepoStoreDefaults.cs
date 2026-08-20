namespace ControlTower.Core.Models
{
    /// <summary>
    /// Factories for fresh <see cref="RepoStore"/> instances created from the
    /// Settings UI. Kept here (not in the Desktop code-behind) so the rule
    /// "no hardcoded paths" (copilot-instructions §1/§3) is testable: new
    /// stores must come out of these factories with empty user-supplied
    /// fields, never seeded with a literal path like <c>C:\Repos</c>.
    /// </summary>
    public static class RepoStoreDefaults
    {
        public static RepoStore NewLocal(string id)
        {
            return new RepoStore
            {
                Id = id ?? string.Empty,
                Type = "local",
                Root = string.Empty
            };
        }

        public static RepoStore NewSsh(string id)
        {
            var safeId = id ?? string.Empty;
            return new RepoStore
            {
                Id = safeId,
                Type = "ssh",
                Root = string.Empty,
                Host = string.Empty,
                User = string.Empty,
                Port = 22,
                CredentialTarget = string.IsNullOrEmpty(safeId) ? string.Empty : "DCT-SSH-" + safeId
            };
        }
    }
}
