from __future__ import annotations

import importlib.util
import sys
import unittest
from pathlib import Path


MODULE_PATH = Path(__file__).resolve().parents[1] / "scripts" / "capacity_model.py"
SPEC = importlib.util.spec_from_file_location("capacity_model", MODULE_PATH)
assert SPEC is not None and SPEC.loader is not None
capacity_model = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = capacity_model
SPEC.loader.exec_module(capacity_model)


class CapacityModelTests(unittest.TestCase):
    def test_core_and_relation_math(self) -> None:
        inputs = capacity_model.CapacityInputs(
            workers=4,
            cores_per_worker=8,
            max_connections_per_worker=200,
            reserved_connections_per_worker=40,
            shard_count=64,
            active_leaf_partitions=12,
            indexes_per_leaf_partition=3,
            placement_factor=1,
            concurrent_multi_shard_queries=5,
            shards_touched_per_query=16,
            total_logical_data_gb=1024,
        )
        result = capacity_model.calculate_capacity(inputs)
        self.assertEqual(result.total_worker_cores, 32)
        self.assertEqual(result.total_usable_worker_connections, 640)
        self.assertEqual(result.peak_internal_connections_upper_bound, 80)
        self.assertEqual(result.physical_leaf_table_placements, 768)
        self.assertEqual(result.physical_leaf_index_placements, 2304)
        self.assertEqual(result.approximate_table_plus_index_relations, 3072)
        self.assertEqual(result.average_logical_shard_gb, 16)
        self.assertAlmostEqual(result.average_shard_partition_gb, 4 / 3)

    def test_fewer_shards_than_workers_warning(self) -> None:
        inputs = capacity_model.CapacityInputs(
            workers=8,
            cores_per_worker=4,
            max_connections_per_worker=100,
            reserved_connections_per_worker=20,
            shard_count=4,
            active_leaf_partitions=1,
            indexes_per_leaf_partition=0,
            placement_factor=1,
            concurrent_multi_shard_queries=0,
            shards_touched_per_query=0,
        )
        result = capacity_model.calculate_capacity(inputs)
        self.assertTrue(
            any("fewer primary shards than workers" in x for x in result.observations)
        )

    def test_connection_budget_warning(self) -> None:
        inputs = capacity_model.CapacityInputs(
            workers=2,
            cores_per_worker=4,
            max_connections_per_worker=100,
            reserved_connections_per_worker=25,
            shard_count=64,
            active_leaf_partitions=1,
            indexes_per_leaf_partition=0,
            placement_factor=1,
            concurrent_multi_shard_queries=10,
            shards_touched_per_query=32,
        )
        result = capacity_model.calculate_capacity(inputs)
        self.assertGreater(result.connection_budget_ratio, 1)
        self.assertTrue(
            any("meets or exceeds" in x for x in result.observations)
        )

    def test_invalid_reserved_connections(self) -> None:
        inputs = capacity_model.CapacityInputs(
            workers=2,
            cores_per_worker=4,
            max_connections_per_worker=100,
            reserved_connections_per_worker=100,
            shard_count=32,
            active_leaf_partitions=1,
            indexes_per_leaf_partition=0,
            placement_factor=1,
            concurrent_multi_shard_queries=0,
            shards_touched_per_query=0,
        )
        with self.assertRaises(ValueError):
            capacity_model.calculate_capacity(inputs)


if __name__ == "__main__":
    unittest.main()
