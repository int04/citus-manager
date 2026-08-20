using CitusManager.Services;
using Xunit;

namespace CitusManager.Tests;

public sealed class CitusConnectionFactoryTests
{
    [Fact]
    public void Windows_host_process_connects_to_Docker_host_alias_through_localhost()
    {
        var resolved = CitusConnectionFactory.ResolveConnectionHost("host.docker.internal");

        Assert.Equal(OperatingSystem.IsWindows() ? "localhost" : "host.docker.internal", resolved);
    }

    [Theory]
    [InlineData("coordinator.internal")]
    [InlineData("10.20.30.40")]
    [InlineData("localhost")]
    public void Other_hosts_are_not_rewritten(string host) =>
        Assert.Equal(host, CitusConnectionFactory.ResolveConnectionHost(host));
}
