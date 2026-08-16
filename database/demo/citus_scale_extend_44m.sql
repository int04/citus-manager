\set ON_ERROR_STOP on
\pset pager off
\timing on

DO $guard$
BEGIN
    IF to_regnamespace('citus_scale') IS NULL THEN
        RAISE EXCEPTION 'Schema citus_scale is missing; build base scale pack first';
    END IF;
END
$guard$;

\echo 'Extend usage_events: 3M -> 6M'
SELECT format($cmd$
INSERT INTO citus_scale.usage_events
SELECT 1 + mod(i - 1, 500000), i,
       1 + mod(i - 1, 1000000),
       TIMESTAMPTZ '2025-09-01 00:00:00+00' + mod(i - 1, 365) * INTERVAL '1 day' + mod(i * 37, 86400) * INTERVAL '1 second',
       (ARRAY['api_call','storage_bytes','active_user','export_row','ai_token'])[1 + mod(i, 5)],
       round((mod(i * 7919, 1000000) / 100.0)::numeric, 4),
       jsonb_build_object('region', (ARRAY['ap','eu','us'])[1 + mod(i, 3)],
                          'endpoint', '/api/v' || (1 + mod(i, 3)) || '/resource/' || mod(i, 50),
                          'cacheHit', mod(i, 4) <> 0, 'extensionBatch', true),
       ('172.21.' || mod(i, 255) || '.' || (1 + mod(i, 253)))::inet,
       ('74000000-0000-0000-0000-' || lpad(i::text, 12, '0'))::uuid
FROM generate_series(%s::bigint, %s::bigint) AS g(i)
ON CONFLICT DO NOTHING
$cmd$, batch_start, LEAST(batch_start + 99999, 6000000))
FROM generate_series(3000001, 6000000, 100000) AS b(batch_start)
\gexec

\echo 'Extend ledger_entries: 3M -> 6M'
SELECT format($cmd$
INSERT INTO citus_scale.ledger_entries
SELECT 1 + mod(i - 1, 500000), i,
       (ARRAY['debit','credit']::citus_scale.ledger_direction[])[1 + mod(i, 2)],
       round((1 + mod(i * 3571, 5000000) / 100.0)::numeric, 4),
       (ARRAY['VND','USD','EUR','JPY'])[1 + mod(i, 4)],
       round(((mod(i * 1237, 20000000) - 1000000) / 100.0)::numeric, 4),
       TIMESTAMPTZ '2025-01-01 00:00:00+00' + mod(i, 525600) * INTERVAL '1 minute',
       DATE '2025-01-01' + mod(i, 608)::integer,
       'LEDGER-' || lpad(i::text, 12, '0'),
       ARRAY[(ARRAY['card','transfer','fee','interest'])[1 + mod(i, 4)],
             CASE WHEN mod(i, 10) = 0 THEN 'review' ELSE 'normal' END],
       jsonb_build_object('batch', 1 + mod(i, 10000), 'reconciled', mod(i, 17) <> 0,
                          'extensionBatch', true)
FROM generate_series(%s::bigint, %s::bigint) AS g(i)
ON CONFLICT DO NOTHING
$cmd$, batch_start, LEAST(batch_start + 99999, 6000000))
FROM generate_series(3000001, 6000000, 100000) AS b(batch_start)
\gexec

\echo 'Extend payment_transactions: 2M -> 4M'
SELECT format($cmd$
INSERT INTO citus_scale.payment_transactions
SELECT 1 + mod(i - 1, 500000), i,
       1 + mod(i - 1, 500000),
       round((1 + mod(i * 7919, 3000000) / 100.0)::numeric, 4),
       (ARRAY['VND','USD','EUR','JPY'])[1 + mod(i, 4)],
       (ARRAY['authorized','captured','settled','declined','reversed'])[1 + mod(i, 5)],
       TIMESTAMPTZ '2025-09-01 00:00:00+00' + mod(i, 525600) * INTERVAL '1 minute',
       CASE WHEN mod(i, 5) = 3 THEN NULL ELSE TIMESTAMPTZ '2025-09-01 00:05:00+00' + mod(i, 525600) * INTERVAL '1 minute' END,
       decode(md5('card-' || i), 'hex'),
       point(100 + mod(i, 5000) / 100.0, 5 + mod(i, 3000) / 100.0),
       mod(i * 43, 10000) / 10000.0,
       jsonb_build_object('channel', (ARRAY['pos','ecommerce','atm','mobile'])[1 + mod(i, 4)],
                          'threeDS', mod(i, 3) = 0, 'installments', mod(i, 12),
                          'extensionBatch', true)
FROM generate_series(%s::bigint, %s::bigint) AS g(i)
ON CONFLICT DO NOTHING
$cmd$, batch_start, LEAST(batch_start + 99999, 4000000))
FROM generate_series(2000001, 4000000, 100000) AS b(batch_start)
\gexec

\echo 'Extend messages: 3M -> 6M'
SELECT format($cmd$
INSERT INTO citus_scale.messages
SELECT 1 + mod(i - 1, 500000), i,
       1 + mod(i * 17, 1000000),
       'Synthetic high-scale message ' || i || ' about distributed systems, Citus routing, analytics, payments, and support.',
       to_tsvector('simple', 'Synthetic high scale message ' || i || ' distributed systems Citus routing analytics payments support'),
       CASE WHEN mod(i, 10) = 0 THEN ARRAY['attachment-' || i || '.json'] ELSE ARRAY[]::text[] END,
       jsonb_build_object('toxicity', mod(i * 7, 1000) / 1000.0,
                          'flagged', mod(i, 101) = 0,
                          'language', (ARRAY['vi','en','ja'])[1 + mod(i, 3)],
                          'extensionBatch', true),
       TIMESTAMPTZ '2025-01-01 00:00:00+00' + mod(i, 525600) * INTERVAL '1 minute',
       CASE WHEN mod(i, 20) = 0 THEN TIMESTAMPTZ '2025-01-01 00:05:00+00' + mod(i, 525600) * INTERVAL '1 minute' END
FROM generate_series(%s::bigint, %s::bigint) AS g(i)
ON CONFLICT DO NOTHING
$cmd$, batch_start, LEAST(batch_start + 99999, 6000000))
FROM generate_series(3000001, 6000000, 100000) AS b(batch_start)
\gexec

\echo 'Extend message_reactions: 1.5M -> 3.5M'
SELECT format($cmd$
INSERT INTO citus_scale.message_reactions
SELECT 1 + mod((1 + mod(i - 1, 6000000)) - 1, 500000), i,
       1 + mod(i - 1, 6000000),
       1 + mod(i * 29, 1000000),
       (ARRAY['like','love','laugh','wow','sad','celebrate'])[1 + mod(i, 6)],
       TIMESTAMPTZ '2025-01-01 00:01:00+00' + mod(i, 525600) * INTERVAL '1 minute'
FROM generate_series(%s::bigint, %s::bigint) AS g(i)
ON CONFLICT DO NOTHING
$cmd$, batch_start, LEAST(batch_start + 99999, 3500000))
FROM generate_series(1500001, 3500000, 100000) AS b(batch_start)
\gexec

\echo 'Extend tracking_events: 2M -> 4M'
SELECT format($cmd$
INSERT INTO citus_scale.tracking_events
SELECT 1 + mod(i - 1, 750000), i,
       TIMESTAMPTZ '2026-01-01 00:00:00+00' + mod(i - 1, 243) * INTERVAL '1 day' + mod(i * 41, 86400) * INTERVAL '1 second',
       (ARRAY['CREATED','PICKUP','DEPARTED','ARRIVED','CUSTOMS','OUT_FOR_DELIVERY','DELIVERED'])[1 + mod(i, 7)],
       'FAC-' || lpad(mod(i, 5000)::text, 5, '0'),
       point(100 + mod(i, 5000) / 100.0, 5 + mod(i, 3000) / 100.0),
       ('192.168.' || mod(i, 255) || '.' || (1 + mod(i, 253)))::inet,
       jsonb_build_object('scanner', 'SCN-' || mod(i, 10000), 'battery', mod(i, 101),
                          'manual', mod(i, 17) = 0, 'extensionBatch', true)
FROM generate_series(%s::bigint, %s::bigint) AS g(i)
ON CONFLICT DO NOTHING
$cmd$, batch_start, LEAST(batch_start + 99999, 4000000))
FROM generate_series(2000001, 4000000, 100000) AS b(batch_start)
\gexec

\echo 'Extend clinical_observations: 2M -> 4M'
SELECT format($cmd$
INSERT INTO citus_scale.clinical_observations
SELECT 1 + mod((1 + mod((1 + mod(i - 1, 1000000)) - 1, 500000)) - 1, 50000), i,
       1 + mod(i - 1, 1000000),
       (ARRAY['HR','BP_SYS','TEMP','SPO2','GLUCOSE'])[1 + mod(i, 5)],
       TIMESTAMPTZ '2025-01-01 00:05:00+00' + mod(i, 525600) * INTERVAL '1 minute',
       round((35 + mod(i * 37, 20000) / 100.0)::numeric, 4),
       CASE WHEN mod(i, 17) = 0 THEN 'requires review' END,
       (ARRAY['bpm','mmHg','C','percent','mg/dL'])[1 + mod(i, 5)],
       mod(i, 13) = 0,
       numrange(35::numeric, (200 + mod(i, 100))::numeric, '[]'),
       jsonb_build_object('device', 'MED-' || mod(i, 10000), 'verified', mod(i, 7) <> 0,
                          'extensionBatch', true)
FROM generate_series(%s::bigint, %s::bigint) AS g(i)
ON CONFLICT DO NOTHING
$cmd$, batch_start, LEAST(batch_start + 99999, 4000000))
FROM generate_series(2000001, 4000000, 100000) AS b(batch_start)
\gexec

\echo 'Extend ticket_comments: 1.5M -> 3.5M'
SELECT format($cmd$
INSERT INTO citus_scale.ticket_comments
SELECT 1 + mod((1 + mod(i - 1, 750000)) - 1, 500000), i,
       1 + mod(i - 1, 750000),
       1 + mod(i * 31, 1000000),
       'Synthetic high-scale comment ' || i || ': investigation result, workaround, logs, and next action.',
       to_tsvector('simple', 'Synthetic high scale comment investigation result workaround logs next action ' || i),
       mod(i, 7) = 0,
       CASE WHEN mod(i, 12) = 0
            THEN jsonb_build_array(jsonb_build_object('name', 'log-' || i || '.txt', 'bytes', mod(i * 97, 1000000)))
            ELSE '[]'::jsonb END,
       TIMESTAMPTZ '2026-01-01 00:10:00+00' + mod(i, 328320) * INTERVAL '1 minute'
FROM generate_series(%s::bigint, %s::bigint) AS g(i)
ON CONFLICT DO NOTHING
$cmd$, batch_start, LEAST(batch_start + 99999, 3500000))
FROM generate_series(1500001, 3500000, 100000) AS b(batch_start)
\gexec

ANALYZE citus_scale.usage_events;
ANALYZE citus_scale.ledger_entries;
ANALYZE citus_scale.payment_transactions;
ANALYZE citus_scale.messages;
ANALYZE citus_scale.message_reactions;
ANALYZE citus_scale.tracking_events;
ANALYZE citus_scale.clinical_observations;
ANALYZE citus_scale.ticket_comments;

\echo '44M extension complete.'
