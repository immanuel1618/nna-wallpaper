using System.Text.RegularExpressions;
using NNA.Wallpaper.Host;
using Xunit;

namespace NNA.Wallpaper.Tests;

public class SmokeTests
{
    [Fact]
    public void HostInfo_HasProductNameAndVersion()
    {
        Assert.Equal("NNA Wallpaper", HostInfo.AppName);
        Assert.Matches(new Regex(@"^\d+\.\d+\.\d+$"), HostInfo.Version);
        Assert.Equal(typeof(HostInfo).Assembly.GetName().Version?.ToString(3), HostInfo.Version);
    }
}
