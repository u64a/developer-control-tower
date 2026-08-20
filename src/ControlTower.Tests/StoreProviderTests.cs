using ControlTower.Core.Models;
using ControlTower.Infrastructure.Configuration;

namespace ControlTower.Tests;

public class StoreProviderTests
{
    [Fact]
    public void GetStores_ReturnsAll()
    {
        var stores = new[]
        {
            new RepoStore { Id = "local", Type = "local", Root = @"C:\Repos" },
            new RepoStore { Id = "devbox", Type = "ssh", Root = @"D:\repos", Host = "192.168.64.10" }
        };
        var provider = new StoreProvider(stores);

        Assert.Equal(2, provider.GetStores().Count);
    }

    [Fact]
    public void GetStore_ById_CaseInsensitive()
    {
        var stores = new[] { new RepoStore { Id = "Local", Type = "local", Root = @"C:\Repos" } };
        var provider = new StoreProvider(stores);

        Assert.NotNull(provider.GetStore("local"));
        Assert.NotNull(provider.GetStore("LOCAL"));
        Assert.Null(provider.GetStore("nonexistent"));
    }

    [Fact]
    public void ResolveProjectPath_Local_CombinesRootAndId()
    {
        var stores = new[] { new RepoStore { Id = "local", Type = "local", Root = @"C:\Repos" } };
        var provider = new StoreProvider(stores);

        var path = provider.ResolveProjectPath("local", "my-project", null);
        Assert.Contains(@"Repos", path);
        Assert.Contains("my-project", path);
    }

    [Fact]
    public void ResolveProjectPath_Local_UsesFolder_WhenProvided()
    {
        var stores = new[] { new RepoStore { Id = "local", Type = "local", Root = @"C:\Repos" } };
        var provider = new StoreProvider(stores);

        var path = provider.ResolveProjectPath("local", "my-project", "custom-folder");
        Assert.Contains("custom-folder", path);
        Assert.DoesNotContain("my-project", path);
    }

    [Fact]
    public void ResolveProjectPath_Ssh_ReturnsUserAtHostColonPath()
    {
        var stores = new[] { new RepoStore { Id = "devbox", Type = "ssh", Root = @"D:\repos", Host = "192.168.64.10", User = "devuser" } };
        var provider = new StoreProvider(stores);

        var path = provider.ResolveProjectPath("devbox", "my-project", null);
        Assert.StartsWith("devuser@192.168.64.10:", path);
        Assert.Contains("my-project", path);
    }

    [Fact]
    public void ResolveProjectPath_Ssh_NoUser_OmitsPrefix()
    {
        var stores = new[] { new RepoStore { Id = "devbox", Type = "ssh", Root = @"D:\repos", Host = "192.168.64.10" } };
        var provider = new StoreProvider(stores);

        var path = provider.ResolveProjectPath("devbox", "my-project", null);
        Assert.StartsWith("192.168.64.10:", path);
        Assert.DoesNotContain("@", path);
    }

    [Fact]
    public void ResolveProjectPath_UnknownStore_ReturnsEmpty()
    {
        var provider = new StoreProvider(Array.Empty<RepoStore>());
        Assert.Equal(string.Empty, provider.ResolveProjectPath("nope", "proj", null));
    }
}
