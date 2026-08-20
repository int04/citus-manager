using CitusManager.Services;
using Xunit;

namespace CitusManager.Tests;

public sealed class CitusVersionTests
{
    [Theory]
    [InlineData("14.1-1", "14")]
    [InlineData("14.1.0", "14")]
    [InlineData("Citus 14.1.0 on x86_64-pc-linux-gnu", "14")]
    [InlineData("13.2.0.citus-1", "13")]
    public void MajorVersion_accepts_extension_and_display_formats(string version, string expected) =>
        Assert.Equal(expected, CitusMutator.MajorVersion(version));

    [Theory]
    [InlineData("")]
    [InlineData("Citus")]
    [InlineData("unknown")]
    public void MajorVersion_rejects_unknown_formats(string version) =>
        Assert.Throws<InvalidOperationException>(() => CitusMutator.MajorVersion(version));
}
