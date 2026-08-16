using CitusManager.Services;
using Xunit;

namespace CitusManager.Tests;

public sealed class DatabaseWorkspaceColumnRulesTests
{
    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(false, false, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]
    public void Editability_respects_privilege_generated_and_distribution_rules(
        bool hasUpdatePrivilege, bool isGenerated, bool isDistributionColumn, bool expected) =>
        Assert.Equal(expected, DatabaseWorkspaceColumnRules.CanEdit(
            hasUpdatePrivilege, isGenerated, isDistributionColumn));
}
