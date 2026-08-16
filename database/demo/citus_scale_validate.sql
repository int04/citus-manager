\set ON_ERROR_STOP on
\pset pager off
\timing on

SELECT *
FROM (VALUES
    ('feature_flags', (SELECT count(*) FROM citus_scale.feature_flags), 500::bigint),
    ('geo_zones', (SELECT count(*) FROM citus_scale.geo_zones), 500::bigint),
    ('merchants', (SELECT count(*) FROM citus_scale.merchants), 500000::bigint),
    ('organizations', (SELECT count(*) FROM citus_scale.organizations), 500000::bigint),
    ('app_users', (SELECT count(*) FROM citus_scale.app_users), 1000000::bigint),
    ('subscriptions', (SELECT count(*) FROM citus_scale.subscriptions), 750000::bigint),
    ('usage_events', (SELECT count(*) FROM citus_scale.usage_events), 3000000::bigint),
    ('usage_daily_rollups', (SELECT count(*) FROM citus_scale.usage_daily_rollups), 500000::bigint),
    ('bank_accounts', (SELECT count(*) FROM citus_scale.bank_accounts), 500000::bigint),
    ('ledger_entries', (SELECT count(*) FROM citus_scale.ledger_entries), 3000000::bigint),
    ('payment_transactions', (SELECT count(*) FROM citus_scale.payment_transactions), 2000000::bigint),
    ('conversations', (SELECT count(*) FROM citus_scale.conversations), 500000::bigint),
    ('messages', (SELECT count(*) FROM citus_scale.messages), 3000000::bigint),
    ('message_reactions', (SELECT count(*) FROM citus_scale.message_reactions), 1500000::bigint),
    ('shipments', (SELECT count(*) FROM citus_scale.shipments), 750000::bigint),
    ('tracking_events', (SELECT count(*) FROM citus_scale.tracking_events), 2000000::bigint),
    ('patients', (SELECT count(*) FROM citus_scale.patients), 500000::bigint),
    ('encounters', (SELECT count(*) FROM citus_scale.encounters), 1000000::bigint),
    ('clinical_observations', (SELECT count(*) FROM citus_scale.clinical_observations), 2000000::bigint),
    ('support_tickets', (SELECT count(*) FROM citus_scale.support_tickets), 750000::bigint),
    ('ticket_comments', (SELECT count(*) FROM citus_scale.ticket_comments), 1500000::bigint)
) AS counts(table_name, actual_rows, expected_rows)
ORDER BY table_name;

SELECT count(*) AS mismatched_tables
FROM (VALUES
    ((SELECT count(*) FROM citus_scale.feature_flags), 500::bigint),
    ((SELECT count(*) FROM citus_scale.geo_zones), 500::bigint),
    ((SELECT count(*) FROM citus_scale.merchants), 500000::bigint),
    ((SELECT count(*) FROM citus_scale.organizations), 500000::bigint),
    ((SELECT count(*) FROM citus_scale.app_users), 1000000::bigint),
    ((SELECT count(*) FROM citus_scale.subscriptions), 750000::bigint),
    ((SELECT count(*) FROM citus_scale.usage_events), 3000000::bigint),
    ((SELECT count(*) FROM citus_scale.usage_daily_rollups), 500000::bigint),
    ((SELECT count(*) FROM citus_scale.bank_accounts), 500000::bigint),
    ((SELECT count(*) FROM citus_scale.ledger_entries), 3000000::bigint),
    ((SELECT count(*) FROM citus_scale.payment_transactions), 2000000::bigint),
    ((SELECT count(*) FROM citus_scale.conversations), 500000::bigint),
    ((SELECT count(*) FROM citus_scale.messages), 3000000::bigint),
    ((SELECT count(*) FROM citus_scale.message_reactions), 1500000::bigint),
    ((SELECT count(*) FROM citus_scale.shipments), 750000::bigint),
    ((SELECT count(*) FROM citus_scale.tracking_events), 2000000::bigint),
    ((SELECT count(*) FROM citus_scale.patients), 500000::bigint),
    ((SELECT count(*) FROM citus_scale.encounters), 1000000::bigint),
    ((SELECT count(*) FROM citus_scale.clinical_observations), 2000000::bigint),
    ((SELECT count(*) FROM citus_scale.support_tickets), 750000::bigint),
    ((SELECT count(*) FROM citus_scale.ticket_comments), 1500000::bigint)
) AS checks(actual_rows, expected_rows)
WHERE actual_rows <> expected_rows;

WITH per_shard AS (
    SELECT table_name, shardid, max(shard_size)::numeric AS shard_bytes
    FROM citus_shards
    WHERE table_name::text LIKE 'citus_scale.%'
    GROUP BY table_name, shardid
)
SELECT table_name,
       count(*) AS shard_count,
       pg_size_pretty(sum(shard_bytes)::bigint) AS logical_size,
       pg_size_pretty(avg(shard_bytes)::bigint) AS avg_shard,
       pg_size_pretty(max(shard_bytes)::bigint) AS max_shard,
       round(max(shard_bytes) / NULLIF(avg(shard_bytes), 0), 2) AS max_to_avg
FROM per_shard
GROUP BY table_name
ORDER BY sum(shard_bytes) DESC;

SELECT nodename, nodeport,
       count(*) AS placements,
       pg_size_pretty(sum(shard_size)::bigint) AS bytes
FROM citus_shards
WHERE table_name::text LIKE 'citus_scale.%'
GROUP BY nodename, nodeport
ORDER BY nodeport;

EXPLAIN (COSTS OFF)
SELECT a.account_id, a.currency, l.entry_id, l.amount, l.booked_at
FROM citus_scale.bank_accounts a
JOIN citus_scale.ledger_entries l USING (account_id)
WHERE a.account_id = 42
ORDER BY l.booked_at DESC
LIMIT 20;

EXPLAIN (COSTS OFF)
SELECT metric_name, count(*), sum(metric_value)
FROM citus_scale.usage_events
WHERE org_id = 42
  AND event_time >= TIMESTAMPTZ '2026-08-01 00:00:00+00'
  AND event_time < TIMESTAMPTZ '2026-09-01 00:00:00+00'
GROUP BY metric_name;

EXPLAIN (COSTS OFF)
SELECT m.message_id, m.sent_at, count(r.reaction_id)
FROM citus_scale.messages m
LEFT JOIN citus_scale.message_reactions r
  ON r.conversation_id = m.conversation_id
 AND r.message_id = m.message_id
WHERE m.conversation_id = 42
GROUP BY m.message_id, m.sent_at
ORDER BY m.sent_at DESC
LIMIT 20;
