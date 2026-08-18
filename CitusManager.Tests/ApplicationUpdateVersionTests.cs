using CitusManager.Services;
using Xunit;

namespace CitusManager.Tests;

public sealed class ApplicationUpdateVersionTests
{
    [Theory]
    [InlineData("26.08.18.1028", "26.08.18.1028")]
    [InlineData("v26.08.18.1028+sha.123", "26.08.18.1028")]
    [InlineData("1.0.0", "Development")]
    [InlineData(null, "Development")]
    public void Informational_version_is_normalized(string? input, string expected) =>
        Assert.Equal(expected, ApplicationUpdateService.NormalizeVersion(input));

    [Theory]
    [InlineData("26.08.18.1028", true)]
    [InlineData("26.02.29.1200", false)]
    [InlineData("26.08.18.2460", false)]
    [InlineData("latest", false)]
    public void Release_tag_is_strict(string input, bool expected) =>
        Assert.Equal(expected, ApplicationUpdateService.IsReleaseVersion(input));

    [Fact]
    public void Timestamp_versions_sort_chronologically()
    {
        Assert.True(ApplicationUpdateService.CompareVersions("26.08.18.1029", "26.08.18.1028") > 0);
        Assert.Equal(0, ApplicationUpdateService.CompareVersions("26.08.18.1028", "26.08.18.1028"));
    }
}
