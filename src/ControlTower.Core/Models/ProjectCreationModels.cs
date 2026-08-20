namespace ControlTower.Core.Models
{
    public sealed class ProjectCreationRequest
    {
        public string ProjectId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;

        /// <summary>
        /// Lifecycle stage of the project (e.g. "active", "incubating", "parked").
        /// Defaults to "active" so a freshly created project gets a sensible value;
        /// the edit flow round-trips whatever was previously persisted.
        /// </summary>
        public string LifecycleState { get; set; } = "active";

        /// <summary>Store to create the project in (e.g. "local", "devbox").</summary>
        public string StoreId { get; set; } = string.Empty;

        /// <summary>Override folder name. Defaults to ProjectId if empty.</summary>
        public string Folder { get; set; } = string.Empty;

        /// <summary>GitHub URL for external refs (optional).</summary>
        public string GitHubUrl { get; set; } = string.Empty;

        /// <summary>ADO URL for external refs (optional).</summary>
        public string AdoUrl { get; set; } = string.Empty;

        /// <summary>Optional organisational folder, e.g. "Customer Projects". Empty = ungrouped.</summary>
        public string Group { get; set; } = string.Empty;

        /// <summary>If true, adopt existing folder rather than failing when it exists.</summary>
        public bool AdoptExisting { get; set; }
    }

    public sealed class ProjectCreationResult
    {
        public bool Success { get; init; }
        public string ProjectId { get; init; } = string.Empty;
        public string ResolvedPath { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;
        public bool FolderAlreadyExists { get; init; }

        public static ProjectCreationResult Ok(string projectId, string path, string message) =>
            new() { Success = true, ProjectId = projectId, ResolvedPath = path, Message = message };

        public static ProjectCreationResult Fail(string message) =>
            new() { Success = false, Message = message };

        public static ProjectCreationResult Exists(string projectId, string path) =>
            new() { Success = false, ProjectId = projectId, ResolvedPath = path, FolderAlreadyExists = true,
                     Message = "Folder already exists. Set AdoptExisting = true to use it." };
    }
}
