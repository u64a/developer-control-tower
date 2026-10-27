namespace ControlTower.Core.Models
{
    /// <summary>Per-project, explicitly opted-in local VS Code maintenance.</summary>
    public sealed class VsCodeMaintenance
    {
        public bool UpdateVsCode { get; set; }

        public bool UpdateExtensions { get; set; }

        public bool IsDefault => !UpdateVsCode && !UpdateExtensions;

        public VsCodeMaintenance Clone()
        {
            return new VsCodeMaintenance
            {
                UpdateVsCode = UpdateVsCode,
                UpdateExtensions = UpdateExtensions
            };
        }
    }
}
