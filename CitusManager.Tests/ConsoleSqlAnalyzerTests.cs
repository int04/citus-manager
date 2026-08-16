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
}
