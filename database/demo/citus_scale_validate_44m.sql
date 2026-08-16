\set ON_ERROR_STOP on
\pset pager off
\timing on

SELECT * FROM (VALUES
    ('usage_events', (SELECT count(*) FROM citus_scale.usage_events), 6000000::bigint),
    ('ledger_entries', (SELECT count(*) FROM citus_scale.ledger_entries), 6000000::bigint),
    ('payment_transactions', (SELECT count(*) FROM citus_scale.payment_transactions), 4000000::bigint),
    ('messages', (SELECT count(*) FROM citus_scale.messages), 6000000::bigint),
    ('message_reactions', (SELECT count(*) FROM citus_scale.message_reactions), 3500000::bigint),
    ('tracking_events', (SELECT count(*) FROM citus_scale.tracking_events), 4000000::bigint),
    ('clinical_observations', (SELECT count(*) FROM citus_scale.clinical_observations), 4000000::bigint),
    ('ticket_comments', (SELECT count(*) FROM citus_scale.ticket_comments), 3500000::bigint)
) AS counts(table_name, actual_rows, expected_rows)
ORDER BY table_name;

SELECT count(*) AS mismatched_tables
FROM (VALUES
    ((SELECT count(*) FROM citus_scale.usage_events), 6000000::bigint),
    ((SELECT count(*) FROM citus_scale.ledger_entries), 6000000::bigint),
    ((SELECT count(*) FROM citus_scale.payment_transactions), 4000000::bigint),
    ((SELECT count(*) FROM citus_scale.messages), 6000000::bigint),
    ((SELECT count(*) FROM citus_scale.message_reactions), 3500000::bigint),
    ((SELECT count(*) FROM citus_scale.tracking_events), 4000000::bigint),
    ((SELECT count(*) FROM citus_scale.clinical_observations), 4000000::bigint),
    ((SELECT count(*) FROM citus_scale.ticket_comments), 3500000::bigint)
) checks(actual_rows, expected_rows)
WHERE actual_rows <> expected_rows;

WITH per_shard AS (
    SELECT table_name, shardid, max(shard_size)::numeric AS shard_bytes
    FROM citus_shards
    WHERE table_name IN (
        'citus_scale.ledger_entries'::regclass,
        'citus_scale.payment_transactions'::regclass,
        'citus_scale.messages'::regclass,
        'citus_scale.message_reactions'::regclass,
        'citus_scale.clinical_observations'::regclass,
        'citus_scale.ticket_comments'::regclass
    )
    GROUP BY table_name, shardid
)
SELECT table_name, count(*) shard_count,
       pg_size_pretty(sum(shard_bytes)::bigint) logical_size,
       pg_size_pretty(max(shard_bytes)::bigint) max_shard,
       round(max(shard_bytes) / NULLIF(avg(shard_bytes), 0), 2) max_to_avg
FROM per_shard
GROUP BY table_name
ORDER BY sum(shard_bytes) DESC;

SELECT CASE
           WHEN table_name::text LIKE 'citus_scale.usage_events_%' THEN 'citus_scale.usage_events'
           WHEN table_name::text LIKE 'citus_scale.tracking_events_%' THEN 'citus_scale.tracking_events'
       END AS partitioned_table,
       count(DISTINCT table_name) AS leaf_partitions,
       count(DISTINCT shardid) AS leaf_shards,
       pg_size_pretty(sum(shard_size)::bigint) AS logical_size
FROM citus_shards
WHERE table_name::text LIKE 'citus_scale.usage_events_%'
   OR table_name::text LIKE 'citus_scale.tracking_events_%'
GROUP BY 1
ORDER BY 1;

SELECT nodename, nodeport, count(*) placements,
       pg_size_pretty(sum(shard_size)::bigint) bytes
FROM citus_shards
WHERE table_name::text LIKE 'citus_scale.%'
GROUP BY nodename, nodeport
ORDER BY nodeport;
