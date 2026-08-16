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
    DatabaseTableMode TableMode = DatabaseTableMode.NotApplicable,
    int ColumnCount = 0,
    int KeyCount = 0,
    int ForeignKeyCount = 0,
    int IndexCount = 0,
    int CheckCount = 0,
    int PartitionCount = 0,
    bool IsPartition = false,
    string? ParentSchema = null,
    string? ParentName = null,
    string? PartitionBound = null);

/// <summary>One lazily loaded catalog item inside a database tree group.</summary>
public sealed record DatabaseTreeChildResponse(
    string Name,
    string? Detail = null,
    string? Schema = null,
    string? Kind = null,
    DatabaseObjectKind? ObjectKind = null,
    DatabaseTableMode? TableMode = null,
    string? PostgreSqlKind = null);

/// <summary>Items returned when one database tree group is expanded.</summary>
public sealed record DatabaseTreeChildrenResponse(string Group, IReadOnlyList<DatabaseTreeChildResponse> Items);

/// <summary>One PostgreSQL type accepted by the structured table creator.</summary>
public sealed record DatabaseTypeResponse(string Name, string DisplayName);

/// <summary>One PostgreSQL operator class available to an index access method.</summary>
public sealed record DatabaseOperatorClassResponse(string AccessMethod, string Name);

/// <summary>One catalog table eligible as a foreign-key target.</summary>
public sealed record DatabaseForeignKeyTargetResponse(string Schema, string Name, IReadOnlyList<string> Columns);

/// <summary>Capabilities and catalog choices used by database action dialogs.</summary>
public sealed record DatabaseActionMetadataResponse(
    IReadOnlyList<string> Schemas,
    IReadOnlyList<DatabaseTypeResponse> ColumnTypes,
    IReadOnlyList<string> DistributedTables,
    IReadOnlyList<string> TableAccessMethods,
    IReadOnlyList<string> IndexAccessMethods,
    IReadOnlyList<string> Collations,
    IReadOnlyList<DatabaseOperatorClassResponse> OperatorClasses,
    IReadOnlyList<DatabaseForeignKeyTargetResponse> ForeignKeyTargets,
    IReadOnlyList<string> Tablespaces,
    IReadOnlyList<string> Roles,
    bool SupportsNullsNotDistinct,
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
    [MaxLength(4000)] public string? Comment { get; init; }
    public bool Nullable { get; init; } = true;
    public bool PrimaryKey { get; init; }
    [MaxLength(4000)] public string? DefaultLiteral { get; init; }
    public bool DefaultCurrentTimestamp { get; init; }
    [MaxLength(4000)] public string? DefaultExpression { get; init; }
    public bool Identity { get; init; }
    public DatabaseIdentityKind IdentityKind { get; init; } = DatabaseIdentityKind.ByDefault;
    public long? IdentityMinimum { get; init; }
    public long? IdentityMaximum { get; init; }
    public long? IdentityIncrement { get; init; } = 1;
    [Range(1, long.MaxValue)] public long? IdentityCache { get; init; } = 1;
    public bool IdentityCycle { get; init; }
}

/// <summary>PostgreSQL identity generation mode.</summary>
public enum DatabaseIdentityKind { ByDefault, Always }

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
    Spgist,
    Brin
}

/// <summary>Sort direction applied to one indexed column.</summary>
public enum DatabaseIndexSortOrder { None, Ascending, Descending }

/// <summary>Supported referential action for a foreign key.</summary>
public enum DatabaseReferentialAction
{
    NoAction,
    Restrict,
    Cascade,
    SetNull,
    SetDefault
}

/// <summary>Physical persistence of a new PostgreSQL table.</summary>
public enum DatabaseTablePersistence { Persistent, Unlogged }

/// <summary>Supported declarative partition strategies.</summary>
public enum DatabasePartitionStrategy { None, Range, List, Hash }

/// <summary>One role grant applied atomically after table creation.</summary>
public sealed record CreateTableGrantRequest
{
    [Required, MaxLength(63)] public required string Role { get; init; }
    [MinLength(1), MaxLength(7)] public required IReadOnlyList<string> Privileges { get; init; }
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
    [MaxLength(4000)] public string? Comment { get; init; }
    [MinLength(1), MaxLength(64)] public required IReadOnlyList<string> Columns { get; init; }
    [Required, MaxLength(63)] public required string ReferencedSchema { get; init; }
    [Required, MaxLength(63)] public required string ReferencedTable { get; init; }
    [MinLength(1), MaxLength(64)] public required IReadOnlyList<string> ReferencedColumns { get; init; }
    public DatabaseReferentialAction OnUpdate { get; init; } = DatabaseReferentialAction.NoAction;
    public DatabaseReferentialAction OnDelete { get; init; } = DatabaseReferentialAction.NoAction;
    public bool Deferrable { get; init; }
    public bool InitiallyDeferred { get; init; }
}

/// <summary>Index definition created atomically with a new table.</summary>
public sealed record CreateTableIndexRequest
{
    [Required, MaxLength(63)] public required string Name { get; init; }
    [MaxLength(4000)] public string? Comment { get; init; }
    public bool Unique { get; init; }
    public bool NullsNotDistinct { get; init; }
    public DatabaseIndexMethod Method { get; init; } = DatabaseIndexMethod.Btree;
    [MinLength(1), MaxLength(64)] public required IReadOnlyList<CreateTableIndexColumnRequest> Columns { get; init; }
    [MaxLength(64)] public IReadOnlyList<string> IncludeColumns { get; init; } = [];
    [MaxLength(4000)] public string? Condition { get; init; }
    [MaxLength(63)] public string? Tablespace { get; init; }
}

/// <summary>One ordered column in a PostgreSQL index definition.</summary>
public sealed record CreateTableIndexColumnRequest
{
    [Required, MaxLength(63)] public required string Name { get; init; }
    public DatabaseIndexSortOrder Order { get; init; }
    [MaxLength(128)] public string? Collation { get; init; }
    [MaxLength(128)] public string? OperatorClass { get; init; }
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
    [MaxLength(4000)] public string? Comment { get; init; }
    public DatabaseTablePersistence Persistence { get; init; } = DatabaseTablePersistence.Persistent;
    public bool WithOids { get; init; }
    public DatabasePartitionStrategy PartitionStrategy { get; init; }
    [MaxLength(63)] public string? PartitionKey { get; init; }
    [Range(10, 100)] public int? FillFactor { get; init; }
    [MaxLength(63)] public string? AccessMethod { get; init; }
    [MaxLength(63)] public string? Tablespace { get; init; }
    [MaxLength(63)] public string? Owner { get; init; }
    [MaxLength(64)] public IReadOnlyList<CreateTableGrantRequest> Grants { get; init; } = [];
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
    bool IsPrimaryKey,
    string? Comment);

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
    public int? NodeId { get; init; }
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

/// <summary>Scope inherited by a query console from the database tree.</summary>
public sealed record QueryConsoleScope(
    string Kind, string? Schema, string? ObjectName, int? NodeId);

/// <summary>One catalog relation offered by SQL autocomplete.</summary>
public sealed record QueryConsoleRelationResponse(
    string Schema, string Name, string Kind, IReadOnlyList<string> Columns);

/// <summary>Catalog metadata used by the SQL editor autocomplete provider.</summary>
public sealed record QueryConsoleMetadataResponse(
    string Database, string TargetLabel, bool IsReadOnly, QueryConsoleScope Scope,
    IReadOnlyList<string> Schemas, IReadOnlyList<QueryConsoleRelationResponse> Relations,
    IReadOnlyList<string> Functions, IReadOnlyList<string> DataTypes,
    IReadOnlyList<string> JoinSuggestions);

public enum ConsoleRiskLevel
{
    ReadOnly,
    Write,
    Destructive
}

/// <summary>Server-derived statement range and execution risk.</summary>
public sealed record ConsoleStatementDescriptor(
    int Index, int Start, int Length, int StartLine, int EndLine, string Command,
    ConsoleRiskLevel Risk, bool RequiresConfirmation, bool IsResultSet, string SqlHash);

/// <summary>Payload for parsing and classifying SQL without executing it.</summary>
public sealed record AnalyzeConsoleSqlRequest
{
    [Required, MinLength(1), MaxLength(1_000_000)] public required string Sql { get; init; }
    public int? NodeId { get; init; }
}

/// <summary>AST analysis result for the current editor contents.</summary>
public sealed record AnalyzeConsoleSqlResponse(
    string QueryHash, bool IsReadOnlyTarget, IReadOnlyList<ConsoleStatementDescriptor> Statements);

/// <summary>Payload for sequential SQL console execution.</summary>
public sealed record ExecuteConsoleSqlRequest
{
    public Guid ExecutionId { get; init; }
    [Required, MinLength(1), MaxLength(1_000_000)] public required string Sql { get; init; }
    public int? NodeId { get; init; }
    [MaxLength(100)] public IReadOnlyList<int>? StatementIndexes { get; init; }
    [MaxLength(100)] public IReadOnlyList<int>? ConfirmedStatementIndexes { get; init; }
    [MaxLength(100)] public IReadOnlyList<int>? DestructiveConfirmedStatementIndexes { get; init; }
    [MaxLength(64)] public string? AnalysisHash { get; init; }
    public QueryConsoleScope? Scope { get; init; }
}

/// <summary>Skips one statement that is still queued in an active console execution.</summary>
public sealed record SkipConsoleStatementRequest
{
    public Guid ExecutionId { get; init; }
    [Range(0, int.MaxValue)] public int StatementIndex { get; init; }
}

/// <summary>One event in the NDJSON SQL execution stream.</summary>
public sealed record ConsoleExecutionEvent(
    string Type, DateTimeOffset Timestamp, int? StatementIndex = null, string? Command = null,
    string? Message = null, long? DurationMilliseconds = null, int? RecordsAffected = null,
    IReadOnlyList<ResultColumnResponse>? Columns = null,
    IReadOnlyList<IReadOnlyList<CellValueResponse>>? Rows = null,
    bool? IsTruncated = null, string? SqlState = null, string? QueryHash = null,
    long? ExecutionMilliseconds = null, long? FetchingMilliseconds = null);

/// <summary>Stateless page request for one previously executed SELECT.</summary>
public sealed record QueryConsoleResultRequest
{
    [Required, MinLength(1), MaxLength(1_000_000)] public required string Sql { get; init; }
    public int? NodeId { get; init; }
    public QueryConsoleScope? Scope { get; init; }
    [Range(1, 1_000_000)] public int Page { get; init; } = 1;
    [Range(1, 500)] public int PageSize { get; init; } = 20;
    [MaxLength(8000)] public string? Where { get; init; }
    [MaxLength(4000)] public string? OrderBy { get; init; }
}

/// <summary>One replayed query-result page.</summary>
public sealed record QueryConsoleResultResponse(
    IReadOnlyList<ResultColumnResponse> Columns,
    IReadOnlyList<IReadOnlyList<CellValueResponse>> Rows,
    int Page, int PageSize, bool HasPrevious, bool HasNext, TimeSpan Duration,
    ConsoleResultOrigin? Origin = null,
    IReadOnlyList<DatabaseRowIdentity?>? Identities = null);

/// <summary>Single base relation provenance detected for a replayable console result.</summary>
public sealed record ConsoleResultOrigin(
    string Schema, string ObjectName, IReadOnlyList<string>? EditableColumns = null);

/// <summary>Exact count for a replayable SELECT result.</summary>
public sealed record QueryConsoleResultCountResponse(long Count, TimeSpan Duration);

/// <summary>Reads one canonical full cell from a replayable SELECT.</summary>
public sealed record ReadQueryConsoleResultCellRequest
{
    [Required, MinLength(1), MaxLength(1_000_000)] public required string Sql { get; init; }
    public int? NodeId { get; init; }
    public QueryConsoleScope? Scope { get; init; }
    [Range(0, long.MaxValue)] public long RowOffset { get; init; }
    [Range(0, 10_000)] public int ColumnIndex { get; init; }
    [MaxLength(8000)] public string? Where { get; init; }
    [MaxLength(4000)] public string? OrderBy { get; init; }
}

/// <summary>Catalog metadata used by one data workspace.</summary>
public sealed record DatabaseWorkspaceMetadataResponse(
    string Schema, string ObjectName, DatabaseObjectKind ObjectKind, DatabaseTableMode TableMode,
    bool IsCoordinator, bool CanEdit, string? ReadOnlyReason, string? DistributionColumn,
    long? EstimatedRows, IReadOnlyList<WorkspaceColumnResponse> Columns, IReadOnlyList<string> PrimaryKey);

/// <summary>Column behavior and PostgreSQL type information for the workspace grid.</summary>
public sealed record WorkspaceColumnResponse(
    string Name, string DataType, bool IsNullable, bool IsPrimaryKey, bool IsDistributionColumn,
    bool IsGenerated, bool IsIdentity, bool CanEdit, bool IsNumeric, bool IsIndexed, bool IsUnique,
    string? Comment);

/// <summary>One displayed workspace cell.</summary>
public sealed record DatabaseCellResponse(string? Value, bool IsNull, bool IsTruncated);

/// <summary>Stable identity and optimistic concurrency token for one row.</summary>
public sealed record DatabaseRowIdentity(IReadOnlyDictionary<string, string?> Keys, string Fingerprint);

/// <summary>Lazy request for operational details about one workspace row.</summary>
public sealed record InspectWorkspaceRowRequest
{
    [Required, MaxLength(63)] public required string Schema { get; init; }
    [Required, MaxLength(255)] public required string ObjectName { get; init; }
    public int? NodeId { get; init; }
    public DatabaseRowIdentity? Identity { get; init; }
}

/// <summary>One full row value returned by the inspector.</summary>
public sealed record DatabaseInspectedValueResponse(
    string Name, string DataType, string? Value, bool IsNull, bool IsTruncated);

/// <summary>One relation in the PostgreSQL partition lineage.</summary>
public sealed record DatabasePartitionInspectionResponse(
    string Schema, string Name, int Depth, string? Strategy, string? KeyDefinition,
    string? Bound, bool IsLeaf, bool IsDefault, string? AccessMethod, long? TotalBytes);

/// <summary>One Citus shard placement and its topology node.</summary>
public sealed record DatabasePlacementInspectionResponse(
    long? ShardId, long? PlacementId, string? PlacementState, long? ShardBytes,
    int? NodeId, int? GroupId, string Host, int Port, string Role, bool IsActive,
    bool HasMetadata, bool MetadataSynced, bool ShouldHaveShards,
    string? Rack, string? NodeCluster, string? PhysicalRelation);

/// <summary>Resolved or candidate Citus shard information.</summary>
public sealed record DatabaseShardInspectionResponse(
    bool IsExact, string Status, long? ShardId, string? MinimumValue, string? MaximumValue,
    IReadOnlyList<long> CandidateShardIds, IReadOnlyList<DatabasePlacementInspectionResponse> Placements);

/// <summary>Optional PostgreSQL tuple details for an exactly resolved row.</summary>
public sealed record DatabaseRowInternalsResponse(
    long? TableOid, string? PhysicalTable, string? Ctid, string? Xmin, string? Xmax,
    int? RowBytes, string? Fingerprint);

/// <summary>Read-only row, partition, shard and server diagnostics.</summary>
public sealed record DatabaseRowInspectionResponse(
    string Database, string Schema, string ObjectName, DatabaseObjectKind ObjectKind,
    DatabaseTableMode TableMode, string TargetLabel, bool RowResolved, string? ResolutionReason,
    string Persistence, string? AccessMethod, string Owner, string? Tablespace,
    long EstimatedRows, long? TotalBytes, string ReplicaIdentity,
    string? DistributionMethod, string? DistributionColumn, string? DistributionValue,
    long? ColocationId, string? ReplicationModel,
    IReadOnlyList<DatabaseInspectedValueResponse> Values,
    IReadOnlyList<DatabasePartitionInspectionResponse> Partitions,
    DatabaseShardInspectionResponse? Shard,
    DatabaseRowInternalsResponse? Internals,
    IReadOnlyList<string> Warnings);

/// <summary>Batch request used by the grid to resolve current-page row servers.</summary>
public sealed record LocateWorkspaceRowsRequest
{
    [Required, MaxLength(63)] public required string Schema { get; init; }
    [Required, MaxLength(255)] public required string ObjectName { get; init; }
    public int? NodeId { get; init; }
    [Required, MinLength(1), MaxLength(500)]
    public required IReadOnlyList<DatabaseRowIdentity?> Identities { get; init; }
}

/// <summary>Server resolution for one row at the matching request index.</summary>
public sealed record DatabaseWorkspaceRowLocationResponse(
    int RowIndex, bool Resolved, bool IsExact, string Status, long? ShardId,
    IReadOnlyList<DatabasePlacementInspectionResponse> Placements);

public sealed record LocateWorkspaceRowsResponse(
    IReadOnlyList<DatabaseWorkspaceRowLocationResponse> Locations);

/// <summary>One row returned to the workspace grid.</summary>
public sealed record DatabaseRowResponse(DatabaseRowIdentity? Identity, IReadOnlyList<DatabaseCellResponse> Cells);

/// <summary>Query request for a data workspace.</summary>
public sealed record QueryWorkspaceRowsRequest
{
    [Required, MaxLength(63)] public required string Schema { get; init; }
    [Required, MaxLength(255)] public required string ObjectName { get; init; }
    public int? NodeId { get; init; }
    [Range(1, 1_000_000)] public int Page { get; init; } = 1;
    [Range(1, 500)] public int PageSize { get; init; } = 50;
    [MaxLength(8000)] public string? Where { get; init; }
    [MaxLength(4000)] public string? OrderBy { get; init; }
}

/// <summary>One page returned to a data workspace.</summary>
public sealed record QueryWorkspaceRowsResponse(
    IReadOnlyList<WorkspaceColumnResponse> Columns, IReadOnlyList<DatabaseRowResponse> Rows,
    int Page, int PageSize, bool HasPrevious, bool HasNext, bool HasStableOrder,
    long? EstimatedRows, TimeSpan Duration);

/// <summary>Exact-count request for the active workspace filter.</summary>
public sealed record CountWorkspaceRowsRequest
{
    [Required, MaxLength(63)] public required string Schema { get; init; }
    [Required, MaxLength(255)] public required string ObjectName { get; init; }
    public int? NodeId { get; init; }
    [MaxLength(8000)] public string? Where { get; init; }
}

/// <summary>Exact count returned for a workspace filter.</summary>
public sealed record CountWorkspaceRowsResponse(long Count, TimeSpan Duration);

public sealed record ReadWorkspaceCellRequest
{
    [Required, MaxLength(63)] public required string Schema { get; init; }
    [Required, MaxLength(255)] public required string ObjectName { get; init; }
    [Required, MaxLength(63)] public required string Column { get; init; }
    public required DatabaseRowIdentity Identity { get; init; }
}

/// <summary>One staged cell update.</summary>
public sealed record DatabaseCellChangeRequest
{
    [Required, MaxLength(63)] public required string Column { get; init; }
    public string? Value { get; init; }
    public bool IsNull { get; init; }
    public bool UseDefault { get; init; }
}

/// <summary>One staged row update.</summary>
public sealed record UpdateWorkspaceRowRequest
{
    public required IReadOnlyDictionary<string, string?> Keys { get; init; }
    [Required] public required string Fingerprint { get; init; }
    [MinLength(1)] public required IReadOnlyList<DatabaseCellChangeRequest> Changes { get; init; }
}

/// <summary>One staged inserted row.</summary>
public sealed record InsertWorkspaceRowRequest
{
    [MinLength(1)] public required IReadOnlyList<DatabaseCellChangeRequest> Values { get; init; }
}

/// <summary>One staged deleted row.</summary>
public sealed record DeleteWorkspaceRowRequest
{
    public required IReadOnlyDictionary<string, string?> Keys { get; init; }
    [Required] public required string Fingerprint { get; init; }
}

/// <summary>Atomic set of pending grid changes.</summary>
public sealed record ApplyTableChangesRequest
{
    [Required, MaxLength(63)] public required string Schema { get; init; }
    [Required, MaxLength(255)] public required string ObjectName { get; init; }
    public IReadOnlyList<InsertWorkspaceRowRequest> Inserts { get; init; } = [];
    public IReadOnlyList<UpdateWorkspaceRowRequest> Updates { get; init; } = [];
    public IReadOnlyList<DeleteWorkspaceRowRequest> Deletes { get; init; } = [];
}

/// <summary>Result of one atomic grid save.</summary>
public sealed record ApplyTableChangesResponse(int Inserted, int Updated, int Deleted, string Message);

/// <summary>Read-only DDL representation for one database object.</summary>
public sealed record DatabaseDdlResponse(string Schema, string Name, string Sql);

public sealed record ExportWorkspaceCsvRequest
{
    [Required, MaxLength(63)] public required string Schema { get; init; }
    [Required, MaxLength(255)] public required string ObjectName { get; init; }
    public int? NodeId { get; init; }
    [Range(1, 1_000_000)] public int Page { get; init; } = 1;
    [Range(1, 500)] public int PageSize { get; init; } = 50;
    [MaxLength(8000)] public string? Where { get; init; }
    [MaxLength(4000)] public string? OrderBy { get; init; }
    public bool CurrentPageOnly { get; init; } = true;
}

public sealed record CsvImportPreviewResponse(
    IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string?>> Rows, bool IsTruncated);

public sealed record CsvImportResponse(int Imported, string Message);
