using CitusManager.Services;
using Xunit;

namespace CitusManager.Tests;

public sealed class DatabaseRowInspectionRulesTests
{
    [Theory]
    [InlineData("r", "RANGE")]
    [InlineData("l", "LIST")]
    [InlineData("h", "HASH")]
    [InlineData(null, null)]
    public void PartitionStrategy_MapsPostgreSqlCatalogValue(string? value, string? expected)
    {
        Assert.Equal(expected, DatabaseRowInspectionRules.PartitionStrategy(value));
    }

    [Fact]
    public void Truncate_EnforcesCellAndRemainingPayloadLimits()
    {
        var remaining = 7;

        var first = DatabaseRowInspectionRules.Truncate("abcdefgh", 5, ref remaining);
        var second = DatabaseRowInspectionRules.Truncate("wxyz", 5, ref remaining);

        Assert.Equal("abcde", first.Value);
        Assert.True(first.IsTruncated);
        Assert.Equal("wx", second.Value);
        Assert.True(second.IsTruncated);
        Assert.Equal(0, remaining);
    }

    [Fact]
    public void Truncate_PreservesNullWithoutConsumingBudget()
    {
        var remaining = 3;
        var result = DatabaseRowInspectionRules.Truncate(null, 2, ref remaining);

        Assert.Null(result.Value);
        Assert.False(result.IsTruncated);
        Assert.Equal(3, remaining);
    }
}
