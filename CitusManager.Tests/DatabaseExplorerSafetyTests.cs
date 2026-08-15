using CitusManager.Services;
using Xunit;

namespace CitusManager.Tests;

public sealed class DatabaseExplorerSafetyTests
{
    [Fact]
    public void Identifier_quoting_escapes_embedded_quotes() =>
        Assert.Equal("\"tenant\"\"data\"", DatabaseExplorerSafety.QuoteIdentifier("tenant\"data"));

    [Fact]
    public void Query_hash_never_contains_plain_sql()
    {
        const string sql = "DROP TABLE public.sensitive_table";
        var hash = DatabaseExplorerSafety.QueryHash(sql);
        Assert.Equal(64, hash.Length);
        Assert.DoesNotContain("sensitive_table", hash, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("events_102008", 102008L)]
    [InlineData("very_long_name_999999", 999999L)]
    public void Physical_relation_suffix_maps_to_shard_id(string relation, long expected)
    {
        Assert.True(DatabaseExplorerSafety.TryParseShardId(relation, out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("events")]
    [InlineData("events_not-a-number")]
    public void Non_shard_relation_is_rejected(string relation) =>
        Assert.False(DatabaseExplorerSafety.TryParseShardId(relation, out _));
}
