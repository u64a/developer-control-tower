using ControlTower.Infrastructure.Library;

namespace ControlTower.Tests;

public class AssetTransferRouterTests
{
    [Theory]
    [InlineData(@"C:\repos\foo", false)]
    [InlineData(@"D:\Profiles\example\projects\bar", false)]
    [InlineData("/home/user/proj", false)]
    [InlineData("user@host:/home/user/proj", true)]
    [InlineData("user@192.168.1.10:D:\\repos\\foo", true)]
    [InlineData("host:/path", true)]
    [InlineData("", false)]
    [InlineData("just-a-name", false)]
    public void IsSshTarget_DetectsCorrectly(string target, bool expected)
    {
        Assert.Equal(expected, AssetTransferRouter.IsSshTarget(target));
    }
}
