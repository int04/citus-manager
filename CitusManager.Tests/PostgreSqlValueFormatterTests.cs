using CitusManager.Services;
using Xunit;

namespace CitusManager.Tests;

public sealed class PostgreSqlValueFormatterTests
{
    [Fact]
    public void Formats_string_array_as_postgresql_array_literal() =>
        Assert.Equal("{admin,operator,viewer}", PostgreSqlValueFormatter.Format(new[] { "admin", "operator", "viewer" }));

    [Fact]
    public void Quotes_and_escapes_special_array_values() =>
        Assert.Equal("{\"hello, world\",\"NULL\",\"a\\\"b\",\"\",NULL}",
            PostgreSqlValueFormatter.Format(new string?[] { "hello, world", "NULL", "a\"b", "", null }));

    [Fact]
    public void Formats_multidimensional_array() =>
        Assert.Equal("{{1,2},{3,4}}", PostgreSqlValueFormatter.Format(new[,] { { 1, 2 }, { 3, 4 } }));

    [Fact]
    public void Formats_bytea_as_postgresql_hex() =>
        Assert.Equal("\\x00abcdef", PostgreSqlValueFormatter.Format(new byte[] { 0, 0xab, 0xcd, 0xef }));
}
