namespace ControlTower.Core.Contracts
{
    public interface ISshConfigManager
    {
        /// <summary>
        /// Generates or updates ~/.ssh/config with entries for all SSH stores.
        /// Preserves user-managed entries outside the managed block.
        /// </summary>
        void UpdateSshConfig(System.Collections.Generic.IReadOnlyList<Core.Models.RepoStore> stores);

        /// <summary>
        /// Returns hostnames currently managed by Developer Control Tower.
        /// </summary>
        System.Collections.Generic.IReadOnlyList<string> GetManagedHosts();
    }
}
