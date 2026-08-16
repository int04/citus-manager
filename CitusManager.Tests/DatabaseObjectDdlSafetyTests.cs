using System.Text.Json;
using CitusManager.Contracts;
using CitusManager.Domain;
using CitusManager.Services;
using Xunit;

namespace CitusManager.Tests;

public sealed class DatabaseObjectDdlSafetyTests
{
    [Fact]
    public void Literal_quotes_single_quotes()
    {
        Assert.Equal("'O''Reilly'", DatabaseObjectDdlSafety.QuoteLiteral("O'Reilly"));
    }

    [Fact]
    public void Identifier_rejects_more_than_63_utf8_bytes()
    {
        Assert.Throws<ArgumentException>(() =>
            DatabaseObjectDdlSafety.ValidateIdentifier(new string('ế', 32), "name"));
    }

    [Fact]
    public void Distributed_primary_key_requires_distribution_column()
    {
        var request = Table(DatabaseTableMode.Distributed, "tenant_id") with
        {
            Columns =
            [
                new() { Name = "tenant_id", DataType = "bigint", Nullable = false },
                new() { Name = "id", DataType = "bigint", PrimaryKey = true }
            ]
        };
        Assert.Throws<ArgumentException>(() => DatabaseObjectDdlSafety.ValidateCreateTable(request));
    }

    [Fact]
    public void Valid_distributed_table_passes()
    {
        var request = Table(DatabaseTableMode.Distributed, "tenant_id");
        DatabaseObjectDdlSafety.ValidateCreateTable(request);
    }

    [Fact]
    public void Table_rejects_two_primary_key_definitions()
    {
        var request = Table(DatabaseTableMode.Local, null!) with
        {
            DistributionColumn = null,
            Keys = [new() { Kind = DatabaseKeyKind.Primary, Columns = ["id"] }]
        };
        Assert.Throws<ArgumentException>(() => DatabaseObjectDdlSafety.ValidateCreateTable(request));
    }

    [Fact]
    public void Foreign_key_requires_matching_column_counts()
    {
        var request = Table(DatabaseTableMode.Local, null!) with
        {
            DistributionColumn = null,
            ForeignKeys =
            [
                new()
                {
                    Columns = ["tenant_id", "id"], ReferencedSchema = "public", ReferencedTable = "tenants",
                    ReferencedColumns = ["id"]
                }
            ]
        };
        Assert.Throws<ArgumentException>(() => DatabaseObjectDdlSafety.ValidateCreateTable(request));
    }

    [Fact]
    public void Initially_deferred_foreign_key_requires_deferrable()
    {
        var request = Table(DatabaseTableMode.Local, null!) with
        {
            DistributionColumn = null,
            ForeignKeys =
            [
                new()
                {
                    Name = "events_tenant_fk", Columns = ["tenant_id"], ReferencedSchema = "public",
                    ReferencedTable = "tenants", ReferencedColumns = ["id"], InitiallyDeferred = true
                }
            ]
        };
        Assert.Throws<ArgumentException>(() => DatabaseObjectDdlSafety.ValidateCreateTable(request));
    }

    [Fact]
    public void Foreign_key_comment_requires_named_constraint()
    {
        var request = Table(DatabaseTableMode.Local, null!) with
        {
            DistributionColumn = null,
            ForeignKeys =
            [
                new()
                {
                    Comment = "Tenant relationship", Columns = ["tenant_id"], ReferencedSchema = "public",
                    ReferencedTable = "tenants", ReferencedColumns = ["id"]
                }
            ]
        };
        Assert.Throws<ArgumentException>(() => DatabaseObjectDdlSafety.ValidateCreateTable(request));
    }

    [Fact]
    public void Key_rejects_column_not_in_table()
    {
        var request = Table(DatabaseTableMode.Local, null!) with
        {
            DistributionColumn = null,
            Keys = [new() { Kind = DatabaseKeyKind.Unique, Columns = ["missing"] }]
        };
        Assert.Throws<ArgumentException>(() => DatabaseObjectDdlSafety.ValidateCreateTable(request));
    }

    [Fact]
    public void Distributed_unique_index_requires_distribution_column()
    {
        var request = Table(DatabaseTableMode.Distributed, "tenant_id") with
        {
            Indexes = [new() { Name = "events_id_uidx", Unique = true, Columns = [new() { Name = "id" }] }]
        };
        Assert.Throws<ArgumentException>(() => DatabaseObjectDdlSafety.ValidateCreateTable(request));
    }

    [Fact]
    public void Check_rejects_statement_separator()
    {
        var request = Table(DatabaseTableMode.Local, null!) with
        {
            DistributionColumn = null,
            Checks = [new() { Name = "unsafe", Expression = "id > 0; DROP TABLE x" }]
        };
        Assert.Throws<ArgumentException>(() => DatabaseObjectDdlSafety.ValidateCreateTable(request));
    }

    [Fact]
    public void With_oids_is_rejected_for_supported_postgresql_versions()
    {
        var request = Table(DatabaseTableMode.Local, null!) with { DistributionColumn = null, WithOids = true };
        Assert.Throws<ArgumentException>(() => DatabaseObjectDdlSafety.ValidateCreateTable(request));
    }

    [Fact]
    public void Partitioned_unique_key_requires_partition_key()
    {
        var request = Table(DatabaseTableMode.Local, null!) with
        {
            DistributionColumn = null,
            PartitionStrategy = DatabasePartitionStrategy.Range,
            PartitionKey = "tenant_id",
            Keys = [new() { Kind = DatabaseKeyKind.Unique, Columns = ["id"] }]
        };
        Assert.Throws<ArgumentException>(() => DatabaseObjectDdlSafety.ValidateCreateTable(request));
    }

    [Fact]
    public void Unlogged_distributed_table_is_rejected()
    {
        var request = Table(DatabaseTableMode.Distributed, "tenant_id") with { Persistence = DatabaseTablePersistence.Unlogged };
        Assert.Throws<ArgumentException>(() => DatabaseObjectDdlSafety.ValidateCreateTable(request));
    }

    [Fact]
    public void Index_include_columns_cannot_duplicate_key_columns()
    {
        var request = Table(DatabaseTableMode.Local, null!) with
        {
            DistributionColumn = null,
            Indexes = [new() { Name = "events_id_idx", Columns = [new() { Name = "id" }], IncludeColumns = ["id"] }]
        };
        Assert.Throws<ArgumentException>(() => DatabaseObjectDdlSafety.ValidateCreateTable(request));
    }

    [Fact]
    public void Nulls_not_distinct_requires_unique_index()
    {
        var request = Table(DatabaseTableMode.Local, null!) with
        {
            DistributionColumn = null,
            Indexes = [new() { Name = "events_id_idx", NullsNotDistinct = true, Columns = [new() { Name = "id" }] }]
        };
        Assert.Throws<ArgumentException>(() => DatabaseObjectDdlSafety.ValidateCreateTable(request));
    }

    [Theory]
    [InlineData("id IS NOT NULL")]
    [InlineData("tenant_id = 1")]
    public void Safe_partial_index_conditions_pass(string condition)
    {
        var request = Table(DatabaseTableMode.Local, null!) with
        {
            DistributionColumn = null,
            Indexes = [new() { Name = "events_partial_idx", Condition = condition, Columns = [new() { Name = "id" }] }]
        };
        DatabaseObjectDdlSafety.ValidateCreateTable(request);
    }

    [Fact]
    public void Partial_index_condition_rejects_statement_separator()
    {
        var request = Table(DatabaseTableMode.Local, null!) with
        {
            DistributionColumn = null,
            Indexes = [new() { Name = "events_partial_idx", Condition = "id > 0; DROP TABLE x", Columns = [new() { Name = "id" }] }]
        };
        Assert.Throws<ArgumentException>(() => DatabaseObjectDdlSafety.ValidateCreateTable(request));
    }

    [Theory]
    [InlineData("CURRENT_TIMESTAMP")]
    [InlineData("gen_random_uuid()")]
    [InlineData("'guest'")]
    [InlineData("42")]
    public void Approved_default_expressions_pass(string expression)
    {
        var request = Table(DatabaseTableMode.Local, null!) with
        {
            DistributionColumn = null,
            Columns = [new() { Name = "id", DataType = "bigint", Nullable = false, DefaultExpression = expression }]
        };
        DatabaseObjectDdlSafety.ValidateCreateTable(request);
    }

    [Theory]
    [InlineData("nextval('unsafe')")]
    [InlineData("1; DROP TABLE users")]
    [InlineData("now() -- comment")]
    public void Arbitrary_default_expressions_are_rejected(string expression)
    {
        var request = Table(DatabaseTableMode.Local, null!) with
        {
            DistributionColumn = null,
            Columns = [new() { Name = "id", DataType = "bigint", Nullable = false, DefaultExpression = expression }]
        };
        Assert.Throws<ArgumentException>(() => DatabaseObjectDdlSafety.ValidateCreateTable(request));
    }

    [Fact]
    public void Identity_column_requires_not_null()
    {
        var request = Table(DatabaseTableMode.Local, null!) with
        {
            DistributionColumn = null,
            Columns = [new() { Name = "id", DataType = "bigint", Nullable = true, Identity = true }]
        };
        Assert.Throws<ArgumentException>(() => DatabaseObjectDdlSafety.ValidateCreateTable(request));
    }

    [Fact]
    public void Identity_column_rejects_default_and_invalid_sequence_options()
    {
        var withDefault = Table(DatabaseTableMode.Local, null!) with
        {
            DistributionColumn = null,
            Columns = [new() { Name = "id", DataType = "bigint", Nullable = false, Identity = true, DefaultExpression = "42" }]
        };
        var invalidRange = withDefault with
        {
            Columns = [new() { Name = "id", DataType = "bigint", Nullable = false, Identity = true, IdentityMinimum = 10, IdentityMaximum = 1 }]
        };
        var zeroStep = withDefault with
        {
            Columns = [new() { Name = "id", DataType = "bigint", Nullable = false, Identity = true, IdentityIncrement = 0 }]
        };

        Assert.Throws<ArgumentException>(() => DatabaseObjectDdlSafety.ValidateCreateTable(withDefault));
        Assert.Throws<ArgumentException>(() => DatabaseObjectDdlSafety.ValidateCreateTable(invalidRange));
        Assert.Throws<ArgumentException>(() => DatabaseObjectDdlSafety.ValidateCreateTable(zeroStep));
    }

    [Theory]
    [InlineData("SELECT")]
    [InlineData("REFERENCES")]
    [InlineData("TRIGGER")]
    public void Table_privileges_are_closed_tokens(string privilege) =>
        Assert.Equal(privilege, DatabaseObjectDdlSafety.TablePrivilegeSql(privilege));

    [Theory]
    [InlineData(DatabaseReferentialAction.NoAction, "NO ACTION")]
    [InlineData(DatabaseReferentialAction.SetNull, "SET NULL")]
    [InlineData(DatabaseReferentialAction.Cascade, "CASCADE")]
    public void Referential_actions_are_closed_tokens(DatabaseReferentialAction action, string expected) =>
        Assert.Equal(expected, DatabaseObjectDdlSafety.ReferentialActionSql(action));

    [Fact]
    public void Typed_confirmation_is_ordinal_and_exact()
    {
        Assert.Throws<ArgumentException>(() =>
            DatabaseObjectDdlSafety.RequireTypedConfirmation("public.Events", "public.events"));
        DatabaseObjectDdlSafety.RequireTypedConfirmation("public.Events", "public.Events");
    }

    [Theory]
    [InlineData("SELECT 1; DROP TABLE x")]
    [InlineData("DELETE FROM users")]
    public void View_definition_rejects_multiple_or_non_query_sql(string sql)
    {
        Assert.Throws<ArgumentException>(() => DatabaseObjectDdlSafety.ValidateViewDefinition(sql));
    }

    [Fact]
    public void Legacy_operation_plan_deserializes_without_conversion_payload()
    {
        const string json = """
            {"Kind":"Rebalance","WorkerHost":null,"WorkerPort":null,"CitusVersion":"13.0",
             "Functions":[],"PreviewJson":"[]","PlacementsOnTarget":null,"Warnings":[],
             "CreatedAt":"2026-01-01T00:00:00+00:00"}
            """;
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        var plan = JsonSerializer.Deserialize<OperationPlan>(json, options);
        Assert.NotNull(plan);
        Assert.Null(plan.TableConversion);
    }

    [Fact]
    public void Convert_table_is_impact_risk() =>
        Assert.Equal(OperationRisk.Impact, OperationSafety.RiskFor(OperationKind.ConvertTable));

    private static CreateTableRequest Table(DatabaseTableMode mode, string distributionColumn) => new()
    {
        Schema = "public",
        Name = "events",
        Mode = mode,
        DistributionColumn = distributionColumn,
        Columns =
        [
            new() { Name = "tenant_id", DataType = "bigint", Nullable = false, PrimaryKey = true },
            new() { Name = "id", DataType = "bigint", Nullable = false, PrimaryKey = true }
        ]
    };
}
