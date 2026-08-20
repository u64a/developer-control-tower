using ControlTower.Infrastructure.Library;

namespace ControlTower.Tests;

public class SftpUploadPathGuardTests
{
    [Fact]
    public void PrepareUpload_RejectsExistingSymlinkedParent()
    {
        var accessor = new FakeSftpPathAccessor();
        accessor.SetDirectory(
            "/srv/project",
            Entry("assets", SftpPathEntryKind.Directory));
        accessor.SetDirectory(
            "/srv/project/assets",
            Entry("linked", SftpPathEntryKind.SymbolicLink));

        var success = SftpUploadPathGuard.TryPrepareUpload(
            accessor,
            "/srv/project",
            new[] { "assets", "linked", "README.md" },
            caseInsensitiveNames: false,
            out _,
            out var issue);

        Assert.False(success);
        Assert.Contains("symbolic link", issue, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PrepareUpload_RejectsExistingSymlinkedFile()
    {
        var accessor = new FakeSftpPathAccessor();
        accessor.SetDirectory(
            "/srv/project",
            Entry("assets", SftpPathEntryKind.Directory));
        accessor.SetDirectory(
            "/srv/project/assets",
            Entry("README.md", SftpPathEntryKind.SymbolicLink));

        var success = SftpUploadPathGuard.TryPrepareUpload(
            accessor,
            "/srv/project",
            new[] { "assets", "README.md" },
            caseInsensitiveNames: false,
            out _,
            out var issue);

        Assert.False(success);
        Assert.Contains("symbolic link", issue, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PrepareUpload_AllowsExistingRegularFile()
    {
        var accessor = new FakeSftpPathAccessor();
        accessor.SetDirectory(
            "/srv/project",
            Entry("assets", SftpPathEntryKind.Directory));
        accessor.SetDirectory(
            "/srv/project/assets",
            Entry("README.md", SftpPathEntryKind.RegularFile));

        var success = SftpUploadPathGuard.TryPrepareUpload(
            accessor,
            "/srv/project",
            new[] { "assets", "README.md" },
            caseInsensitiveNames: false,
            out var uploadPath,
            out var issue);

        Assert.True(success, issue);
        Assert.Equal("/srv/project/assets/README.md", uploadPath);
    }

    [Fact]
    public void PrepareUpload_RejectsEntryWhoseTypeCannotBeProven()
    {
        var accessor = new FakeSftpPathAccessor();
        accessor.SetDirectory(
            "/srv/project",
            Entry("assets", SftpPathEntryKind.Other));

        var success = SftpUploadPathGuard.TryPrepareUpload(
            accessor,
            "/srv/project",
            new[] { "assets", "README.md" },
            caseInsensitiveNames: false,
            out _,
            out var issue);

        Assert.False(success);
        Assert.Contains("cannot be proven", issue, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PrepareUpload_InspectionFailureFailsClosed()
    {
        var accessor = new FakeSftpPathAccessor();

        var success = SftpUploadPathGuard.TryPrepareUpload(
            accessor,
            "/srv/project",
            new[] { "assets", "README.md" },
            caseInsensitiveNames: false,
            out _,
            out var issue);

        Assert.False(success);
        Assert.Contains("could not be inspected", issue, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PrepareUpload_CreatesAndRevalidatesMissingNestedDirectories()
    {
        var accessor = new FakeSftpPathAccessor();
        accessor.SetDirectory("/srv/project");

        var success = SftpUploadPathGuard.TryPrepareUpload(
            accessor,
            "/srv/project",
            new[] { "nested folder", "deeper", "file.txt" },
            caseInsensitiveNames: false,
            out var uploadPath,
            out var issue);

        Assert.True(success, issue);
        Assert.Equal("/srv/project/nested folder/deeper/file.txt", uploadPath);
        Assert.Equal(
            new[]
            {
                "/srv/project/nested folder",
                "/srv/project/nested folder/deeper",
            },
            accessor.CreatedDirectories);
    }

    [Fact]
    public void PrepareUpload_RejectsCreatedDirectoryThatReappearsAsLink()
    {
        var accessor = new FakeSftpPathAccessor
        {
            CreatedEntryKind = SftpPathEntryKind.SymbolicLink,
        };
        accessor.SetDirectory("/srv/project");

        var success = SftpUploadPathGuard.TryPrepareUpload(
            accessor,
            "/srv/project",
            new[] { "assets", "README.md" },
            caseInsensitiveNames: false,
            out _,
            out var issue);

        Assert.False(success);
        Assert.Contains("symbolic link", issue, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PrepareUpload_DoesNotInspectOrRejectTrustedRootItself()
    {
        var accessor = new FakeSftpPathAccessor();
        accessor.SetDirectory(
            "/srv/project",
            Entry("assets", SftpPathEntryKind.Directory));
        accessor.SetDirectory("/srv/project/assets");

        var success = SftpUploadPathGuard.TryPrepareUpload(
            accessor,
            "/srv/project",
            new[] { "assets", "README.md" },
            caseInsensitiveNames: false,
            out _,
            out var issue);

        Assert.True(success, issue);
        Assert.DoesNotContain("/srv", accessor.InspectedDirectories);
        Assert.Contains("/srv/project", accessor.InspectedDirectories);
    }

    [Fact]
    public void PrepareUpload_PreservesWindowsSftpDriveRoot()
    {
        var accessor = new FakeSftpPathAccessor();
        accessor.SetDirectory("/D:/");

        var success = SftpUploadPathGuard.TryPrepareUpload(
            accessor,
            "/D:/",
            new[] { "README.md" },
            caseInsensitiveNames: true,
            out var uploadPath,
            out var issue);

        Assert.True(success, issue);
        Assert.Equal("/D:/README.md", uploadPath);
        Assert.Contains("/D:/", accessor.InspectedDirectories);
    }

    private static SftpPathEntry Entry(string name, SftpPathEntryKind kind)
    {
        return new SftpPathEntry(name, kind);
    }

    private sealed class FakeSftpPathAccessor : ISftpPathAccessor
    {
        private readonly Dictionary<string, List<SftpPathEntry>> _directories =
            new(StringComparer.Ordinal);

        public SftpPathEntryKind CreatedEntryKind { get; set; } =
            SftpPathEntryKind.Directory;

        public List<string> CreatedDirectories { get; } = new();
        public List<string> InspectedDirectories { get; } = new();

        public void SetDirectory(string path, params SftpPathEntry[] entries)
        {
            _directories[path] = entries.ToList();
        }

        public IReadOnlyList<SftpPathEntry> ListDirectory(string path)
        {
            InspectedDirectories.Add(path);
            if (!_directories.TryGetValue(path, out var entries))
            {
                throw new InvalidOperationException("Directory not configured: " + path);
            }

            return entries.ToList();
        }

        public void CreateDirectory(string path)
        {
            CreatedDirectories.Add(path);
            var separator = path.LastIndexOf('/');
            var parent = separator == 0 ? "/" : path.Substring(0, separator);
            var name = path.Substring(separator + 1);

            if (!_directories.TryGetValue(parent, out var entries))
            {
                throw new InvalidOperationException("Parent not configured: " + parent);
            }

            entries.Add(new SftpPathEntry(name, CreatedEntryKind));
            _directories[path] = new List<SftpPathEntry>();
        }
    }
}
