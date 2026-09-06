using NNA.Wallpaper.Host;
using Xunit;

namespace NNA.Wallpaper.Tests;

public class SmokeTests
{
    [Fact]
    public void HostInfo_HasProductNameAndVersion()
    {
        Assert.Equal("NNA Wallpaper", HostInfo.AppName);
        Assert.Equal("0.1.0", HostInfo.Version);
    }
}
