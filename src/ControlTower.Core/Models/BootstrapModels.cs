using System.Collections.Generic;

namespace ControlTower.Core.Models
{
    public sealed class MissingProject
    {
        public string ProjectId { get; init; } = string.Empty;
        public string StoreId { get; init; } = string.Empty;
        public string ExpectedPath { get; init; } = string.Empty;
        public string CloneUrl { get; init; } = string.Empty;
        public bool HasCloneUrl => !string.IsNullOrWhiteSpace(CloneUrl);
        public bool IsSsh { get; init; }
    }

    public sealed class BootstrapResult
    {
        public bool Success { get; init; }
        public string ProjectId { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;

        public static BootstrapResult Ok(string projectId, string message) =>
            new() { Success = true, ProjectId = projectId, Message = message };

        public static BootstrapResult Fail(string projectId, string message) =>
            new() { Success = false, ProjectId = projectId, Message = message };
    }
}
