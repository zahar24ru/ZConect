using ZConectService;
using Xunit;

namespace ZConect.Tests;

/// <summary>Tests for service installer constants and logic.</summary>
public sealed class ServiceInstallerTests
{
    [Fact]
    public void ServiceName_is_correct()
    {
        Assert.Equal("ZConectService", ServiceInstaller.ServiceName);
    }

    [Fact]
    public void DisplayName_is_descriptive()
    {
        Assert.NotEmpty(ServiceInstaller.DisplayName);
        Assert.Contains("ZConect", ServiceInstaller.DisplayName);
    }

    [Fact]
    public void Description_is_set()
    {
        Assert.NotEmpty(ServiceInstaller.Description);
    }
}
