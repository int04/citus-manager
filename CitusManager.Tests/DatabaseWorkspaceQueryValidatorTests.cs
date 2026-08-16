using CitusManager.Services;
using Xunit;

namespace CitusManager.Tests;

public sealed class DatabaseWorkspaceQueryValidatorTests
{
    [Fact]
    public void Accepts_one_select_with_postgresql_expression() =>
        DatabaseWorkspaceQueryValidator.Validate(
            "SELECT * FROM \"public\".\"users\" AS cm WHERE (lower(name) LIKE 'a%' AND id IN (SELECT 1)) ORDER BY id DESC",
            "public", "users");

    [Fact]
    public void Rejects_multiple_statements() => Assert.Throws<ArgumentException>(() =>
        DatabaseWorkspaceQueryValidator.Validate(
            "SELECT * FROM \"public\".\"users\" AS cm; DELETE FROM public.users", "public", "users"));

    [Fact]
    public void Rejects_changed_target() => Assert.Throws<ArgumentException>(() =>
        DatabaseWorkspaceQueryValidator.Validate("SELECT * FROM public.secrets", "public", "users"));

    [Fact]
    public void Rejects_modifying_cte() => Assert.Throws<ArgumentException>(() =>
        DatabaseWorkspaceQueryValidator.Validate(
            "WITH changed AS (DELETE FROM public.users RETURNING *) SELECT * FROM public.users", "public", "users"));

    [Fact]
    public void Worker_sql_accepts_select_and_rejects_mutation()
    {
        DatabaseWorkspaceQueryValidator.ValidateReadOnlySql("select 1; select now()");
        Assert.Throws<ArgumentException>(() =>
            DatabaseWorkspaceQueryValidator.ValidateReadOnlySql("update public.users set name='x'"));
    }
}
