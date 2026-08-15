using System.ComponentModel.DataAnnotations;

namespace CitusManager.Contracts;

/// <summary>One database object shown in the cluster data browser.</summary>
public sealed record DatabaseObjectResponse(
    string Schema,
    string Name,
    string Kind,
    string TableType,
    long EstimatedRows,
    long Bytes,
    int LocalShardCount);

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
