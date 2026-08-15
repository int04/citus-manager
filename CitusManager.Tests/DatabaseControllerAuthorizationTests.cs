using CitusManager.Controllers;
using Microsoft.AspNetCore.Authorization;
using Xunit;

namespace CitusManager.Tests;

public sealed class DatabaseControllerAuthorizationTests
{
    [Theory]
    [InlineData(nameof(DatabaseController.CreateSchema), "Operator")]
    [InlineData(nameof(DatabaseController.CreateTable), "Operator")]
    [InlineData(nameof(DatabaseController.CreateView), "Operator")]
    [InlineData(nameof(DatabaseController.CreateSequence), "Operator")]
    [InlineData(nameof(DatabaseController.Rename), "Operator")]
    [InlineData(nameof(DatabaseController.RefreshMaterializedView), "Operator")]
    [InlineData(nameof(DatabaseController.PlanTableConversion), "Operator")]
    [InlineData(nameof(DatabaseController.ExecuteSql), "Operator")]
    [InlineData(nameof(DatabaseController.Drop), "Admin")]
    [InlineData(nameof(DatabaseController.Truncate), "Admin")]
    [InlineData(nameof(DatabaseController.RestartSequence), "Admin")]
    public void Mutation_endpoint_has_expected_policy(string action, string policy)
    {
        var method = typeof(DatabaseController).GetMethod(action);
        Assert.NotNull(method);
        var authorization = Assert.Single(method.GetCustomAttributes(typeof(AuthorizeAttribute), true)
            .Cast<AuthorizeAttribute>());
        Assert.Equal(policy, authorization.Policy);
    }
}
