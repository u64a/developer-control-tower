using System.Collections.Generic;
using ControlTower.Core.Models;

namespace ControlTower.Core.Contracts
{
    public interface IRepoBootstrapService
    {
        /// <summary>
        /// Scans the portfolio for projects whose local folder is missing.
        /// </summary>
        IReadOnlyList<MissingProject> DetectMissing();

        /// <summary>
        /// Clones a missing project from its clone URL into the appropriate store path.
        /// </summary>
        BootstrapResult Clone(MissingProject project);
    }
}
