using System;
using System.Collections.Generic;
using ControlTower.Core.Models;

namespace ControlTower.Core.Contracts
{
    public interface IWorkspaceProfileProvider
    {
        WorkspaceProfileCatalog LoadProfiles();

        void SaveProfiles(IReadOnlyList<WorkspaceProfile> profiles);
    }

    public interface IActiveProfileSelectionStore
    {
        ActiveProfileSelection Load();

        void Save(Guid profileId);
    }
}
