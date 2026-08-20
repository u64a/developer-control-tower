namespace ControlTower.Core.Models
{
    public enum UninstallDataMode
    {
        KeepPortableData,
        RemovePortableConfigurationKeepLibrary,
        RemoveAllPortableData
    }

    public sealed record UninstallLaunchResult(
        bool Started,
        string Message);
}
