using System.ComponentModel.DataAnnotations;

namespace CitusManager.Contracts;

/// <summary>Exact database object category used by explorer actions.</summary>
public enum DatabaseObjectKind
{
    Schema,
    Table,
    PartitionedTable,
    ForeignTable,
    View,
    MaterializedView,
    Sequence
}

/// <summary>Citus placement mode of a table.</summary>
public enum DatabaseTableMode
{
    NotApplicable,
    Local,
    Reference,
    Distributed
}

/// <summary>Action exposed by one database tree node.</summary>
public enum DatabaseAction
{
    Browse,
    Structure,
    Inspect,
    Query,
    Refresh,
    Create,
    Rename,
    Edit,
    RefreshData,
    Truncate,
    Restart,
    Convert,
    Drop
}

/// <summary>One database object shown in the cluster data browser.</summary>
public sealed record DatabaseObjectResponse(
    string Schema,
    string Name,
    string Kind,
    string TableType,
    long EstimatedRows,
    long Bytes,
    int LocalShardCount,
    string PostgreSqlKind = "",
    DatabaseObjectKind ObjectKind = DatabaseObjectKind.Table,
    DatabaseTableMode TableMode = DatabaseTableMode.NotApplicable);

/// <summary>One PostgreSQL type accepted by the structured table creator.</summary>
public sealed record DatabaseTypeResponse(string Name, string DisplayName);

/// <summary>Capabilities and catalog choices used by database action dialogs.</summary>
public sealed record DatabaseActionMetadataResponse(
    IReadOnlyList<string> Schemas,
    IReadOnlyList<DatabaseTypeResponse> ColumnTypes,
    IReadOnlyList<string> DistributedTables,
    bool CanCreateReferenceTable,
    bool CanCreateDistributedTable,
    string? CitusVersion);

/// <summary>Result returned after a database object mutation.</summary>
public sealed record DatabaseMutationResponse(string Message, string? Schema, string? Name, string? RedirectUrl = null);

/// <summary>Stored SQL definition of a view.</summary>
public sealed record DatabaseObjectDefinitionResponse(string Schema, string Name, string Definition);

/// <summary>Sequence metadata shown by the inspector.</summary>
public sealed record SequenceInspectionResponse(
    string Schema, string Name, string DataType, long Start, long Minimum, long Maximum,
    long Increment, bool Cycle, long Cache, long? LastValue);

/// <summary>Bounded dependency preview shown before a destructive action.</summary>
public sealed record DatabaseDependencyResponse(int Count, IReadOnlyList<string> Items);

/// <summary>One column in a structured CREATE TABLE request.</summary>
public sealed record CreateTableColumnRequest
{
    [Required, MaxLength(63)] public required string Name { get; init; }
    [Required, MaxLength(128)] public required string DataType { get; init; }
    public bool Nullable { get; init; } = true;
    public bool PrimaryKey { get; init; }
    [MaxLength(4000)] public string? DefaultLiteral { get; init; }
    public bool DefaultCurrentTimestamp { get; init; }
}

/// <summary>Kind of table key created by the structured table designer.</summary>
public enum DatabaseKeyKind
{
    Primary,
    Unique
}

/// <summary>Supported PostgreSQL index access methods.</summary>
public enum DatabaseIndexMethod
{
    Btree,
    Hash,
    Gin,
    Gist,
    Brin
}

/// <summary>Supported referential action for a foreign key.</summary>
public enum DatabaseReferentialAction
{
    NoAction,
    Restrict,
    Cascade,
    SetNull,
    SetDefault
}

/// <summary>Primary or unique key definition in CREATE TABLE.</summary>
public sealed record CreateTableKeyRequest
{
    [MaxLength(63)] public string? Name { get; init; }
    public DatabaseKeyKind Kind { get; init; }
    [MinLength(1), MaxLength(64)] public required IReadOnlyList<string> Columns { get; init; }
}

/// <summary>Foreign-key definition in CREATE TABLE.</summary>
public sealed record CreateTableForeignKeyRequest
{
    [MaxLength(63)] public string? Name { get; init; }
    [MinLength(1), MaxLength(64)] public required IReadOnlyList<string> Columns { get; init; }
    [Required, MaxLength(63)] public required string ReferencedSchema { get; init; }
    [Required, MaxLength(63)] public required string ReferencedTable { get; init; }
    [MinLength(1), MaxLength(64)] public required IReadOnlyList<string> ReferencedColumns { get; init; }
    public DatabaseReferentialAction OnUpdate { get; init; } = DatabaseReferentialAction.NoAction;
    public DatabaseReferentialAction OnDelete { get; init; } = DatabaseReferentialAction.NoAction;
}

/// <summary>Index definition created atomically with a new table.</summary>
public sealed record CreateTableIndexRequest
{
    [Required, MaxLength(63)] public required string Name { get; init; }
    public bool Unique { get; init; }
    public DatabaseIndexMethod Method { get; init; } = DatabaseIndexMethod.Btree;
    [MinLength(1), MaxLength(64)] public required IReadOnlyList<string> Columns { get; init; }
}

/// <summary>CHECK constraint definition in CREATE TABLE.</summary>
public sealed record CreateTableCheckRequest
{
    [MaxLength(63)] public string? Name { get; init; }
    [Required, MinLength(1), MaxLength(4000)] public required string Expression { get; init; }
}

/// <summary>Payload for creating a schema.</summary>
public sealed record CreateSchemaRequest
{
    [Required, MaxLength(63)] public required string Name { get; init; }
}

/// <summary>Payload for creating a local, reference, or distributed table.</summary>
public sealed record CreateTableRequest
{
    [Required, MaxLength(63)] public required string Schema { get; init; }
    [Required, MaxLength(63)] public required string Name { get; init; }
    [MinLength(1), MaxLength(200)] public required IReadOnlyList<CreateTableColumnRequest> Columns { get; init; }
    [MaxLength(64)] public IReadOnlyList<CreateTableKeyRequest> Keys { get; init; } = [];
    [MaxLength(64)] public IReadOnlyList<CreateTableForeignKeyRequest> ForeignKeys { get; init; } = [];
    [MaxLength(128)] public IReadOnlyList<CreateTableIndexRequest> Indexes { get; init; } = [];
    [MaxLength(128)] public IReadOnlyList<CreateTableCheckRequest> Checks { get; init; } = [];
    public DatabaseTableMode Mode { get; init; } = DatabaseTableMode.Local;
    [MaxLength(63)] public string? DistributionColumn { get; init; }
    [MaxLength(255)] public string? ColocateWith { get; init; }
    [Range(1, 4096)] public int? ShardCount { get; init; }
}

/// <summary>Payload for creating or replacing a normal view.</summary>
public sealed record CreateViewRequest
{
    [Required, MaxLength(63)] public required string Schema { get; init; }
    [Required, MaxLength(63)] public required string Name { get; init; }
    [Required, MinLength(1), MaxLength(1_000_000)] public required string Definition { get; init; }
    public bool Replace { get; init; }
}

/// <summary>Payload for creating a PostgreSQL sequence.</summary>
public sealed record CreateSequenceRequest
{
    [Required, MaxLength(63)] public required string Schema { get; init; }
    [Required, MaxLength(63)] public required string Name { get; init; }
    public long? Start { get; init; }
    public long? Increment { get; init; }
    public long? Minimum { get; init; }
    public long? Maximum { get; init; }
    [Range(1, long.MaxValue)] public long? Cache { get; init; }
    public bool Cycle { get; init; }
}

/// <summary>Payload for renaming a database object.</summary>
public sealed record RenameDatabaseObjectRequest
{
    public required DatabaseObjectKind Kind { get; init; }
    [Required, MaxLength(63)] public required string Schema { get; init; }
    [MaxLength(63)] public string? Name { get; init; }
    [Required, MaxLength(63)] public required string NewName { get; init; }
}

/// <summary>Payload for dropping a database object after typed confirmation.</summary>
public sealed record DropDatabaseObjectRequest
{
    public required DatabaseObjectKind Kind { get; init; }
    [Required, MaxLength(63)] public required string Schema { get; init; }
    [MaxLength(63)] public string? Name { get; init; }
    public bool Cascade { get; init; }
    [Required, MaxLength(255)] public required string TypedConfirmation { get; init; }
}

/// <summary>Payload for truncating a table after typed confirmation.</summary>
public sealed record TruncateTableRequest
{
    [Required, MaxLength(63)] public required string Schema { get; init; }
    [Required, MaxLength(63)] public required string Name { get; init; }
    public bool RestartIdentity { get; init; }
    public bool Cascade { get; init; }
    [Required, MaxLength(255)] public required string TypedConfirmation { get; init; }
}

/// <summary>Payload for restarting a sequence.</summary>
public sealed record RestartSequenceRequest
{
    [Required, MaxLength(63)] public required string Schema { get; init; }
    [Required, MaxLength(63)] public required string Name { get; init; }
    public long RestartWith { get; init; } = 1;
}

/// <summary>Payload for refreshing a materialized view.</summary>
public sealed record RefreshMaterializedViewRequest
{
    [Required, MaxLength(63)] public required string Schema { get; init; }
    [Required, MaxLength(63)] public required string Name { get; init; }
    public bool Concurrently { get; init; }
}

/// <summary>Preflight payload for converting a local table through a durable operation.</summary>
public sealed record CreateTableConversionOperationRequest
{
    [Required, MaxLength(63)] public required string Schema { get; init; }
    [Required, MaxLength(63)] public required string Table { get; init; }
    public required DatabaseTableMode TargetMode { get; init; }
    [MaxLength(63)] public string? DistributionColumn { get; init; }
    [MaxLength(255)] public string? ColocateWith { get; init; }
    [Range(1, 4096)] public int? ShardCount { get; init; }
    public bool ExternalCapacityAndBackupChecksAcknowledged { get; init; }
    [Required, MaxLength(255)] public required string TypedConfirmation { get; init; }
}

/// <summary>One column returned by a database query.</summary>
public sealed record ResultColumnResponse(string Name, string DataType);

/// <summary>One safely formatted database value.</summary>
public sealed record CellValueResponse(string? Value, bool IsNull, bool IsTruncated);

/// <summary>One page of data from a logical table or worker-local shard set.</summary>
public sealed record TableDataResponse(
    string Schema,
    string Table,
    IReadOnlyList<ResultColumnResponse> Columns,
    IReadOnlyList<IReadOnlyList<CellValueResponse>> Rows,
    int Page,
    int PageSize,
    bool HasPrevious,
    bool HasNext,
    bool HasStableOrder,
    TimeSpan Duration);

/// <summary>PostgreSQL column metadata for one table.</summary>
public sealed record TableColumnResponse(
    string Name,
    string DataType,
    bool IsNullable,
    string? DefaultValue,
    bool IsPrimaryKey);

/// <summary>PostgreSQL index metadata for one table.</summary>
public sealed record TableIndexResponse(string Name, string Definition);

/// <summary>Structure metadata for a logical table or one worker shard.</summary>
public sealed record TableStructureResponse(
    string Schema,
    string Table,
    IReadOnlyList<TableColumnResponse> Columns,
    IReadOnlyList<TableIndexResponse> Indexes,
    IReadOnlyList<long> LocalShardIds);

/// <summary>Request for browsing a table with bounded pagination.</summary>
public sealed record BrowseTableRequest
{
    [Required, MaxLength(63)] public required string Schema { get; init; }
    [Required, MaxLength(255)] public required string Table { get; init; }
    public int? NodeId { get; init; }
    [Range(1, 1_000_000)] public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 50;
}

/// <summary>Request for reading table structure.</summary>
public sealed record TableStructureRequest
{
    [Required, MaxLength(63)] public required string Schema { get; init; }
    [Required, MaxLength(255)] public required string Table { get; init; }
    public int? NodeId { get; init; }
}

/// <summary>Unrestricted SQL submitted to the registered coordinator.</summary>
public sealed record ExecuteSqlRequest
{
    [Required, MinLength(1)] public required string Sql { get; init; }
    public bool Confirmed { get; init; }
}

/// <summary>One result set produced by the SQL console.</summary>
public sealed record SqlResultSetResponse(
    IReadOnlyList<ResultColumnResponse> Columns,
    IReadOnlyList<IReadOnlyList<CellValueResponse>> Rows,
    bool IsTruncated);

/// <summary>Bounded response from unrestricted coordinator SQL execution.</summary>
public sealed record SqlExecutionResponse(
    IReadOnlyList<SqlResultSetResponse> ResultSets,
    IReadOnlyList<string> CommandTags,
    int RecordsAffected,
    bool ResultSetLimitReached,
    TimeSpan Duration,
    string QueryHash);
