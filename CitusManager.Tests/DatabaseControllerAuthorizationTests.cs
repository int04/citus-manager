using CitusManager.Controllers;
using Microsoft.AspNetCore.Authorization;
using Xunit;

namespace CitusManager.Tests;

public sealed class DatabaseControllerAuthorizationTests
{
    [Fact]
    public void InspectRow_IsViewerAccessibleAndAntiforgeryProtected()
    {
        var method = typeof(DatabaseController).GetMethod(nameof(DatabaseController.InspectRow));

        Assert.NotNull(method);
        Assert.Empty(method!.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), true));
        Assert.NotEmpty(method.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.ValidateAntiForgeryTokenAttribute), true));
    }

    [Fact]
    public void LocateRows_IsViewerAccessibleAndAntiforgeryProtected()
    {
        var method = typeof(DatabaseController).GetMethod(nameof(DatabaseController.LocateRows));

        Assert.NotNull(method);
        Assert.Empty(method!.GetCustomAttributes(typeof(AuthorizeAttribute), true));
        Assert.NotEmpty(method.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.ValidateAntiForgeryTokenAttribute), true));
    }

    [Theory]
    [InlineData(nameof(DatabaseController.CreateSchema), "Operator")]
    [InlineData(nameof(DatabaseController.CreateTable), "Operator")]
    [InlineData(nameof(DatabaseController.ModifyTable), "Operator")]
    [InlineData(nameof(DatabaseController.CreateView), "Operator")]
    [InlineData(nameof(DatabaseController.CreateSequence), "Operator")]
    [InlineData(nameof(DatabaseController.Rename), "Operator")]
    [InlineData(nameof(DatabaseController.RefreshMaterializedView), "Operator")]
    [InlineData(nameof(DatabaseController.PlanTableConversion), "Operator")]
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

    [Fact]
    public void Table_designer_definition_is_authenticated_viewer_accessible()
    {
        var method = typeof(DatabaseController).GetMethod(nameof(DatabaseController.TableDesignerDefinition));

        Assert.NotNull(method);
        Assert.Empty(method!.GetCustomAttributes(typeof(AuthorizeAttribute), true));
    }

    [Theory]
    [InlineData(nameof(DatabaseController.AnalyzeConsoleSql))]
    [InlineData(nameof(DatabaseController.ExecuteConsoleSql))]
    [InlineData(nameof(DatabaseController.SkipConsoleStatement))]
    [InlineData(nameof(DatabaseController.QueryConsoleResult))]
    [InlineData(nameof(DatabaseController.CountConsoleResult))]
    [InlineData(nameof(DatabaseController.ReadConsoleResultCell))]
    public void Query_console_is_authenticated_viewer_accessible_and_antiforgery_protected(string action)
    {
        var method = typeof(DatabaseController).GetMethod(action);
        Assert.NotNull(method);
        Assert.Empty(method!.GetCustomAttributes(typeof(AuthorizeAttribute), true));
        Assert.NotEmpty(method.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.ValidateAntiForgeryTokenAttribute), true));
    }
}
