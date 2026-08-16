using CitusManager.Contracts;
using CitusManager.Services;
using Xunit;

namespace CitusManager.Tests;

public sealed class ConsoleSqlAnalyzerTests
{
    [Fact]
    public void Preserves_statement_ranges_with_dollar_quoted_body()
    {
        const string sql = "CREATE FUNCTION public.f() RETURNS void LANGUAGE plpgsql AS $$ BEGIN RAISE NOTICE 'a;b'; END $$;\nSELECT 1;";
        var statements = ConsoleSqlAnalyzer.Analyze(sql);

        Assert.Equal(2, statements.Count);
        Assert.Equal("CREATE", statements[0].Command);
        Assert.Equal(ConsoleRiskLevel.Write, statements[0].Risk);
        Assert.Equal("SELECT", statements[1].Command);
        Assert.Equal(sql.IndexOf("SELECT", StringComparison.Ordinal), statements[1].Start);
    }

    [Theory]
    [InlineData("UPDATE public.users SET enabled=false", ConsoleRiskLevel.Destructive)]
    [InlineData("DELETE FROM public.users", ConsoleRiskLevel.Destructive)]
    [InlineData("UPDATE public.users SET enabled=false WHERE id=1", ConsoleRiskLevel.Write)]
    [InlineData("SELECT * FROM public.users", ConsoleRiskLevel.ReadOnly)]
    public void Classifies_statement_risk(string sql, ConsoleRiskLevel expected) =>
        Assert.Equal(expected, Assert.Single(ConsoleSqlAnalyzer.Analyze(sql)).Risk);

    [Fact]
    public void Rejects_modifying_cte_as_read_only_result() => Assert.Throws<ArgumentException>(() =>
        ConsoleSqlAnalyzer.EnsureSingleReadOnly("WITH changed AS (DELETE FROM public.users RETURNING *) SELECT * FROM changed"));

    [Fact]
    public void Ignores_comment_only_tail_and_semicolon_inside_escape_string()
    {
        var statements = ConsoleSqlAnalyzer.Analyze("SELECT E'a\\';b'; -- trailing comment");
        Assert.Single(statements);
        Assert.Equal("SELECT", statements[0].Command);
    }

    [Fact]
    public void Splits_complete_statements_on_newline_without_semicolon()
    {
        const string sql = "select * from public.users\nselect * from users";
        var statements = ConsoleSqlAnalyzer.Analyze(sql);

        Assert.Equal(2, statements.Count);
        Assert.All(statements, statement => Assert.Equal("SELECT", statement.Command));
        Assert.Equal(sql.IndexOf("select * from users", StringComparison.Ordinal), statements[1].Start);
    }

    [Theory]
    [InlineData("select id,\n       name\nfrom public.users\nwhere id > 0")]
    [InlineData("select 1\nunion all\nselect 2")]
    [InlineData("select (\n  select max(id)\n  from public.users\n) as max_id")]
    public void Does_not_split_one_multiline_select(string sql) =>
        Assert.Single(ConsoleSqlAnalyzer.Analyze(sql));

    [Theory]
    [InlineData("select * from public.users", "tenant", "public", "users")]
    [InlineData("select id from users where id > 0", "tenant", "tenant", "users")]
    public void Detects_single_table_result_origin(string sql, string activeSchema, string schema, string table)
    {
        var origin = DatabaseQueryConsoleService.TryReadResultOrigin(sql, activeSchema);

        Assert.NotNull(origin);
        Assert.Equal(schema, origin!.Schema);
        Assert.Equal(table, origin.ObjectName);
    }

    [Theory]
    [InlineData("select * from public.users u join public.roles r on r.id=u.role_id")]
    [InlineData("with x as (select * from public.users) select * from x")]
    [InlineData("select 1")]
    public void Does_not_claim_origin_for_non_single_table_result(string sql) =>
        Assert.Null(DatabaseQueryConsoleService.TryReadResultOrigin(sql, "public"));

    [Theory]
    [InlineData("select * from admin_jobs")]
    [InlineData("SELECT jobs.* FROM citus_demo.admin_jobs AS jobs WHERE id > 0")]
    [InlineData("select \"jobs\".* from \"citus_demo\".\"admin_jobs\" as \"jobs\"")]
    public void Detects_direct_star_projection(string sql) =>
        Assert.True(DatabaseQueryConsoleService.IsDirectStarProjection(sql));

    [Theory]
    [InlineData("select id, status from admin_jobs")]
    [InlineData("select count(*) from admin_jobs")]
    [InlineData("select * + 1 from admin_jobs")]
    public void Does_not_treat_expression_projection_as_direct_star(string sql) =>
        Assert.False(DatabaseQueryConsoleService.IsDirectStarProjection(sql));

    [Fact]
    public void Preserves_actionable_citus_error_text()
    {
        const string message = "complex joins are only supported when all distributed tables are co-located and joined on their distribution columns";

        Assert.Equal(message, DatabaseQueryConsoleService.SanitizeConsoleServerText(message));
    }

    [Theory]
    [InlineData("password=secret host=worker", "password=[REDACTED] host=worker")]
    [InlineData("postgresql://admin:secret@worker:5432/citusdb", "postgresql://[REDACTED]@worker:5432/citusdb")]
    public void Redacts_credentials_from_console_server_errors(string message, string expected) =>
        Assert.Equal(expected, DatabaseQueryConsoleService.SanitizeConsoleServerText(message));
}
