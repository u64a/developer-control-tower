using ControlTower.Core.Models;

namespace ControlTower.Core.Contracts
{
    public interface IApplicationUninstallService
    {
        bool IsAvailable { get; }

        UninstallLaunchResult Launch(
            UninstallDataMode mode,
            int currentProcessId);
    }
}
