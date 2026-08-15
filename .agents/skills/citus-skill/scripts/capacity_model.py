#!/usr/bin/env python3
"""Estimate Citus shard, partition, relation, and connection budgets.

This calculator intentionally produces planning estimates, not deployment
recommendations. Validate every assumption with the target Citus release,
query plans, workload tests, and live database limits.
"""

from __future__ import annotations

import argparse
import json
from dataclasses import asdict, dataclass


@dataclass(frozen=True)
class CapacityInputs:
    workers: int
    cores_per_worker: float
    max_connections_per_worker: int
    reserved_connections_per_worker: int
    shard_count: int
    active_leaf_partitions: int
    indexes_per_leaf_partition: int
    placement_factor: int
    concurrent_multi_shard_queries: int
    shards_touched_per_query: int
    total_logical_data_gb: float | None = None


@dataclass(frozen=True)
class CapacityResult:
    total_worker_cores: float
    shards_per_worker: float
    shards_per_worker_core: float
    usable_connections_per_worker: int
    total_usable_worker_connections: int
    peak_internal_connections_upper_bound: int
    connection_budget_ratio: float | None
    physical_leaf_table_placements: int
    physical_leaf_index_placements: int
    approximate_table_plus_index_relations: int
    average_logical_shard_gb: float | None
    average_shard_partition_gb: float | None
    observations: tuple[str, ...]


def _positive_int(name: str, value: int, *, allow_zero: bool = False) -> None:
    minimum = 0 if allow_zero else 1
    if value < minimum:
        comparator = "non-negative" if allow_zero else "positive"
        raise ValueError(f"{name} must be {comparator}; got {value}")


def validate_inputs(inputs: CapacityInputs) -> None:
    _positive_int("workers", inputs.workers)
    if inputs.cores_per_worker <= 0:
        raise ValueError("cores_per_worker must be positive")
    _positive_int("max_connections_per_worker", inputs.max_connections_per_worker)
    _positive_int(
        "reserved_connections_per_worker",
        inputs.reserved_connections_per_worker,
        allow_zero=True,
    )
    if inputs.reserved_connections_per_worker >= inputs.max_connections_per_worker:
        raise ValueError(
            "reserved_connections_per_worker must be smaller than "
            "max_connections_per_worker"
        )
    _positive_int("shard_count", inputs.shard_count)
    _positive_int("active_leaf_partitions", inputs.active_leaf_partitions)
    _positive_int(
        "indexes_per_leaf_partition",
        inputs.indexes_per_leaf_partition,
        allow_zero=True,
    )
    _positive_int("placement_factor", inputs.placement_factor)
    _positive_int(
        "concurrent_multi_shard_queries",
        inputs.concurrent_multi_shard_queries,
        allow_zero=True,
    )
    _positive_int(
        "shards_touched_per_query",
        inputs.shards_touched_per_query,
        allow_zero=True,
    )
    if inputs.shards_touched_per_query > inputs.shard_count:
        raise ValueError("shards_touched_per_query cannot exceed shard_count")
    if inputs.total_logical_data_gb is not None and inputs.total_logical_data_gb < 0:
        raise ValueError("total_logical_data_gb must be non-negative")


def calculate_capacity(inputs: CapacityInputs) -> CapacityResult:
    validate_inputs(inputs)

    total_worker_cores = inputs.workers * inputs.cores_per_worker
    shards_per_worker = inputs.shard_count / inputs.workers
    shards_per_worker_core = inputs.shard_count / total_worker_cores

    usable_per_worker = (
        inputs.max_connections_per_worker - inputs.reserved_connections_per_worker
    )
    total_usable_connections = inputs.workers * usable_per_worker

    # This is deliberately an upper-bound planning model. The adaptive executor,
    # pool reuse, task scheduling, router queries, and installed GUCs can reduce
    # or cap actual simultaneous connections.
    peak_connections = (
        inputs.concurrent_multi_shard_queries * inputs.shards_touched_per_query
    )
    connection_ratio = (
        peak_connections / total_usable_connections
        if total_usable_connections > 0
        else None
    )

    leaf_table_placements = (
        inputs.shard_count
        * inputs.active_leaf_partitions
        * inputs.placement_factor
    )
    leaf_index_placements = (
        leaf_table_placements * inputs.indexes_per_leaf_partition
    )
    approximate_relations = leaf_table_placements + leaf_index_placements

    average_shard_gb: float | None = None
    average_shard_partition_gb: float | None = None
    if inputs.total_logical_data_gb is not None:
        average_shard_gb = inputs.total_logical_data_gb / inputs.shard_count
        average_shard_partition_gb = (
            inputs.total_logical_data_gb
            / inputs.shard_count
            / inputs.active_leaf_partitions
        )

    observations: list[str] = []
    if inputs.shard_count < inputs.workers:
        observations.append(
            "The table has fewer primary shards than workers, so one primary "
            "placement per worker cannot be achieved for this table."
        )
    if shards_per_worker_core < 1:
        observations.append(
            "There is less than one shard per worker core. This may limit "
            "parallel analytical execution; benchmark the actual query path."
        )
    if connection_ratio is not None:
        if connection_ratio >= 1:
            observations.append(
                "The upper-bound internal connection demand meets or exceeds "
                "the modeled usable worker connection budget."
            )
        elif connection_ratio >= 0.7:
            observations.append(
                "The upper-bound internal connection demand uses at least 70% "
                "of the modeled usable worker connection budget; preserve more "
                "headroom or validate executor/pool limits under concurrency."
            )
    if inputs.active_leaf_partitions > 1:
        observations.append(
            "Relation estimates multiply shards by leaf partitions and active "
            "placements. Add TOAST, partitioned-index metadata, sequences, and "
            "other dependent objects separately."
        )
    if not observations:
        observations.append(
            "No immediate arithmetic warning was triggered. This is not proof "
            "that the design is safe or optimal; benchmark representative work."
        )

    return CapacityResult(
        total_worker_cores=total_worker_cores,
        shards_per_worker=shards_per_worker,
        shards_per_worker_core=shards_per_worker_core,
        usable_connections_per_worker=usable_per_worker,
        total_usable_worker_connections=total_usable_connections,
        peak_internal_connections_upper_bound=peak_connections,
        connection_budget_ratio=connection_ratio,
        physical_leaf_table_placements=leaf_table_placements,
        physical_leaf_index_placements=leaf_index_placements,
        approximate_table_plus_index_relations=approximate_relations,
        average_logical_shard_gb=average_shard_gb,
        average_shard_partition_gb=average_shard_partition_gb,
        observations=tuple(observations),
    )


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description=(
            "Estimate Citus shard/partition relation fan-out and an upper-bound "
            "coordinator-to-worker connection budget."
        )
    )
    parser.add_argument("--workers", type=int, required=True)
    parser.add_argument("--cores-per-worker", type=float, required=True)
    parser.add_argument("--max-connections-per-worker", type=int, required=True)
    parser.add_argument("--reserved-connections-per-worker", type=int, default=30)
    parser.add_argument("--shard-count", type=int, required=True)
    parser.add_argument("--active-leaf-partitions", type=int, default=1)
    parser.add_argument("--indexes-per-leaf-partition", type=int, default=0)
    parser.add_argument("--placement-factor", type=int, default=1)
    parser.add_argument("--concurrent-multi-shard-queries", type=int, default=0)
    parser.add_argument("--shards-touched-per-query", type=int, default=0)
    parser.add_argument("--total-logical-data-gb", type=float)
    parser.add_argument("--json", action="store_true", dest="as_json")
    return parser


def _format_optional(value: float | None, suffix: str = "") -> str:
    return "not supplied" if value is None else f"{value:,.3f}{suffix}"


def render_text(inputs: CapacityInputs, result: CapacityResult) -> str:
    ratio = (
        "not applicable"
        if result.connection_budget_ratio is None
        else f"{result.connection_budget_ratio:.1%}"
    )
    lines = [
        "Citus capacity planning estimate",
        "================================",
        f"Workers: {inputs.workers}",
        f"Total worker cores: {result.total_worker_cores:,.2f}",
        f"Shard count: {inputs.shard_count}",
        f"Shards per worker: {result.shards_per_worker:,.3f}",
        f"Shards per worker core: {result.shards_per_worker_core:,.3f}",
        f"Usable connections per worker: {result.usable_connections_per_worker}",
        f"Total usable worker connections: {result.total_usable_worker_connections}",
        (
            "Peak internal connections (upper bound): "
            f"{result.peak_internal_connections_upper_bound}"
        ),
        f"Connection budget ratio: {ratio}",
        (
            "Physical leaf table placements: "
            f"{result.physical_leaf_table_placements:,}"
        ),
        (
            "Physical leaf index placements: "
            f"{result.physical_leaf_index_placements:,}"
        ),
        (
            "Approximate table + index relations: "
            f"{result.approximate_table_plus_index_relations:,}"
        ),
        (
            "Average logical shard size: "
            f"{_format_optional(result.average_logical_shard_gb, ' GB')}"
        ),
        (
            "Average shard-partition size: "
            f"{_format_optional(result.average_shard_partition_gb, ' GB')}"
        ),
        "",
        "Observations:",
    ]
    lines.extend(f"- {item}" for item in result.observations)
    lines.extend(
        [
            "",
            "Model limits:",
            "- Connection demand is an upper-bound arithmetic estimate, not a measurement.",
            "- Relation count excludes TOAST and other dependent catalog objects.",
            "- Validate with runtime Citus capabilities, EXPLAIN, and representative load.",
        ]
    )
    return "\n".join(lines)


def main() -> int:
    args = build_parser().parse_args()
    inputs = CapacityInputs(
        workers=args.workers,
        cores_per_worker=args.cores_per_worker,
        max_connections_per_worker=args.max_connections_per_worker,
        reserved_connections_per_worker=args.reserved_connections_per_worker,
        shard_count=args.shard_count,
        active_leaf_partitions=args.active_leaf_partitions,
        indexes_per_leaf_partition=args.indexes_per_leaf_partition,
        placement_factor=args.placement_factor,
        concurrent_multi_shard_queries=args.concurrent_multi_shard_queries,
        shards_touched_per_query=args.shards_touched_per_query,
        total_logical_data_gb=args.total_logical_data_gb,
    )
    try:
        result = calculate_capacity(inputs)
    except ValueError as exc:
        raise SystemExit(f"Input error: {exc}") from exc

    if args.as_json:
        print(json.dumps({"inputs": asdict(inputs), "result": asdict(result)}, indent=2))
    else:
        print(render_text(inputs, result))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
