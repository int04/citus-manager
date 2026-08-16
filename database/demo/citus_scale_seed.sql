\set ON_ERROR_STOP on
\pset pager off
\timing on

DO $guard$
BEGIN
    IF to_regnamespace('citus_scale') IS NULL THEN
        RAISE EXCEPTION 'Schema citus_scale is missing; run citus_scale_schema.sql first';
    END IF;
END
$guard$;

\echo 'Seed local/reference tables'
INSERT INTO citus_scale.feature_flags
SELECT i,
       'feature.' || lpad(i::text, 4, '0'),
       mod(i, 3) <> 0,
       round((mod(i * 17, 10000) / 100.0)::numeric, 2),
       jsonb_build_object('plans', ARRAY['free','team','enterprise'],
                          'countries', ARRAY['VN','SG','JP'],
                          'beta', mod(i, 5) = 0),
       tstzrange(TIMESTAMPTZ '2025-01-01 00:00:00+00' + mod(i, 365) * INTERVAL '1 day',
                 TIMESTAMPTZ '2027-01-01 00:00:00+00' + mod(i, 365) * INTERVAL '1 day', '[)'),
       ARRAY['team-' || mod(i, 20), 'owner-' || mod(i, 50)],
       TIMESTAMPTZ '2026-01-01 00:00:00+00' + i * INTERVAL '1 hour'
FROM generate_series(1, 500) AS g(i)
ON CONFLICT DO NOTHING;

INSERT INTO citus_scale.geo_zones
SELECT i,
       'ZONE-' || lpad(i::text, 4, '0'),
       (ARRAY['VN','SG','JP','US','DE'])[1 + mod(i, 5)],
       (ARRAY['VND','SGD','JPY','USD','EUR'])[1 + mod(i, 5)],
       (ARRAY['Asia/Ho_Chi_Minh','Asia/Singapore','Asia/Tokyo','America/New_York','Europe/Berlin'])[1 + mod(i, 5)],
       round((mod(i, 2500) / 10000.0)::numeric, 4),
       box(point(100 + mod(i, 50), 5 + mod(i, 30)),
           point(101 + mod(i, 50), 6 + mod(i, 30)))::polygon,
       jsonb_build_object('priority', mod(i, 10), 'remote', mod(i, 4) = 0)
FROM generate_series(1, 500) AS g(i)
ON CONFLICT DO NOTHING;

SELECT format($cmd$
INSERT INTO citus_scale.merchants
SELECT i,
       ('61000000-0000-0000-0000-' || lpad(i::text, 12, '0'))::uuid,
       'MER-' || lpad(i::text, 9, '0'),
       'Merchant ' || i,
       lpad(mod(i, 9999)::text, 4, '0'),
       1 + mod(i - 1, 500),
       mod(i, 10),
       mod(i, 19) <> 0,
       ARRAY[(ARRAY['VND','USD','EUR','JPY'])[1 + mod(i, 4)], 'USD'],
       jsonb_build_object('channel', (ARRAY['online','store','hybrid'])[1 + mod(i, 3)],
                          'monthlyVolume', mod(i * 7919, 10000000)),
       TIMESTAMPTZ '2020-01-01 00:00:00+00' + mod(i, 2000) * INTERVAL '1 day'
FROM generate_series(%s::bigint, %s::bigint) AS g(i)
ON CONFLICT DO NOTHING
$cmd$, batch_start, LEAST(batch_start + 99999, 500000))
FROM generate_series(1, 500000, 100000) AS b(batch_start)
\gexec

\echo 'Seed platform/SaaS domain'
SELECT format($cmd$
INSERT INTO citus_scale.organizations
SELECT i,
       ('62000000-0000-0000-0000-' || lpad(i::text, 12, '0'))::uuid,
       'org-' || lpad(i::text, 9, '0'),
       'Synthetic Organization ' || i,
       1 + mod(i - 1, 500),
       int4range(mod(i, 10000)::integer,
                 (mod(i, 10000) + 10 + mod(i, 5000))::integer,
                 '[)'),
       ARRAY[(ARRAY['ap-southeast','eu-west','us-east'])[1 + mod(i, 3)]],
       jsonb_build_object('sso', mod(i, 2) = 0, 'retentionDays', 30 + mod(i, 336),
                          'theme', (ARRAY['light','dark','system'])[1 + mod(i, 3)]),
       mod(i, 23) <> 0,
       TIMESTAMPTZ '2019-01-01 00:00:00+00' + mod(i, 2500) * INTERVAL '1 day'
FROM generate_series(%s::bigint, %s::bigint) AS g(i)
ON CONFLICT DO NOTHING
$cmd$, batch_start, LEAST(batch_start + 99999, 500000))
FROM generate_series(1, 500000, 100000) AS b(batch_start)
\gexec

SELECT format($cmd$
INSERT INTO citus_scale.app_users
SELECT 1 + mod(i - 1, 500000), i,
       ('63000000-0000-0000-0000-' || lpad(i::text, 12, '0'))::uuid,
       'user' || i || '@scale.example.test',
       'Scale User ' || i,
       ARRAY[(ARRAY['owner','admin','member','viewer'])[1 + mod(i, 4)],
             CASE WHEN mod(i, 10) = 0 THEN 'billing' ELSE 'standard' END],
       ('10.' || mod(i, 255) || '.' || mod(i / 255, 255) || '.' || (1 + mod(i, 253)))::inet,
       jsonb_build_object('locale', (ARRAY['vi','en','ja','de'])[1 + mod(i, 4)],
                          'mfa', mod(i, 3) = 0, 'loginCount', mod(i * 13, 5000)),
       TIMESTAMPTZ '2026-08-16 00:00:00+00' - mod(i, 100000) * INTERVAL '1 minute',
       TIMESTAMPTZ '2021-01-01 00:00:00+00' + mod(i, 2000) * INTERVAL '1 day'
FROM generate_series(%s::bigint, %s::bigint) AS g(i)
ON CONFLICT DO NOTHING
$cmd$, batch_start, LEAST(batch_start + 99999, 1000000))
FROM generate_series(1, 1000000, 100000) AS b(batch_start)
\gexec

SELECT format($cmd$
INSERT INTO citus_scale.subscriptions
SELECT 1 + mod(i - 1, 500000), i,
       (ARRAY['trial','active','past_due','paused','cancelled']::citus_scale.subscription_state[])[1 + mod(i, 5)],
       (ARRAY['FREE','TEAM','BUSINESS','ENTERPRISE'])[1 + mod(i, 4)],
       1 + mod(i, 2000),
       round((5 + mod(i, 100000) / 73.0)::numeric, 2),
       daterange(DATE '2025-01-01' + mod(i, 365)::integer,
                 DATE '2026-01-01' + mod(i, 730)::integer, '[)'),
       (7 + mod(i, 60)) * INTERVAL '1 day',
       jsonb_build_object('autoRenew', mod(i, 5) <> 0, 'coupon', CASE WHEN mod(i, 11) = 0 THEN 'SCALE25' END),
       TIMESTAMPTZ '2024-01-01 00:00:00+00' + mod(i, 900) * INTERVAL '1 day'
FROM generate_series(%s::bigint, %s::bigint) AS g(i)
ON CONFLICT DO NOTHING
$cmd$, batch_start, LEAST(batch_start + 99999, 750000))
FROM generate_series(1, 750000, 100000) AS b(batch_start)
\gexec

SELECT format($cmd$
INSERT INTO citus_scale.usage_events
SELECT 1 + mod(i - 1, 500000), i,
       1 + mod(i - 1, 1000000),
       TIMESTAMPTZ '2025-09-01 00:00:00+00' + mod(i - 1, 365) * INTERVAL '1 day' + mod(i * 37, 86400) * INTERVAL '1 second',
       (ARRAY['api_call','storage_bytes','active_user','export_row','ai_token'])[1 + mod(i, 5)],
       round((mod(i * 7919, 1000000) / 100.0)::numeric, 4),
       jsonb_build_object('region', (ARRAY['ap','eu','us'])[1 + mod(i, 3)],
                          'endpoint', '/api/v' || (1 + mod(i, 3)) || '/resource/' || mod(i, 50),
                          'cacheHit', mod(i, 4) <> 0),
       ('172.20.' || mod(i, 255) || '.' || (1 + mod(i, 253)))::inet,
       ('64000000-0000-0000-0000-' || lpad(i::text, 12, '0'))::uuid
FROM generate_series(%s::bigint, %s::bigint) AS g(i)
ON CONFLICT DO NOTHING
$cmd$, batch_start, LEAST(batch_start + 99999, 3000000))
FROM generate_series(1, 3000000, 100000) AS b(batch_start)
\gexec

SELECT format($cmd$
INSERT INTO citus_scale.usage_daily_rollups
SELECT 1 + mod(i - 1, 500000), i,
       DATE '2026-01-01' + mod(i, 228)::integer,
       (ARRAY['api_call','storage_bytes','active_user','export_row','ai_token'])[1 + mod(i, 5)],
       100 + mod(i * 97, 100000),
       round((mod(i * 1543, 10000000) / 10.0)::numeric, 4),
       mod(i * 31, 10000) / 100.0,
       jsonb_build_object('region', (ARRAY['ap','eu','us'])[1 + mod(i, 3)], 'generated', true)
FROM generate_series(%s::bigint, %s::bigint) AS g(i)
ON CONFLICT DO NOTHING
$cmd$, batch_start, LEAST(batch_start + 99999, 500000))
FROM generate_series(1, 500000, 100000) AS b(batch_start)
\gexec

\echo 'Seed finance domain'
SELECT format($cmd$
INSERT INTO citus_scale.bank_accounts
SELECT i,
       ('65000000-0000-0000-0000-' || lpad(i::text, 12, '0'))::uuid,
       'customer-' || i,
       (ARRAY['checking','savings','credit','wallet'])[1 + mod(i, 4)],
       (ARRAY['VND','USD','EUR','JPY'])[1 + mod(i, 4)],
       round(((mod(i * 3571, 20000000) - 1000000) / 100.0)::numeric, 4),
       round((mod(i * 101, 5000000) / 100.0)::numeric, 4),
       (ARRAY['active','frozen','dormant','closed'])[1 + mod(i, 4)],
       DATE '2015-01-01' + mod(i, 4200)::integer,
       jsonb_build_object('kycLevel', 1 + mod(i, 3), 'pep', mod(i, 1000) = 0,
                          'riskScore', mod(i * 17, 1000) / 1000.0)
FROM generate_series(%s::bigint, %s::bigint) AS g(i)
ON CONFLICT DO NOTHING
$cmd$, batch_start, LEAST(batch_start + 99999, 500000))
FROM generate_series(1, 500000, 100000) AS b(batch_start)
\gexec

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
       jsonb_build_object('batch', 1 + mod(i, 10000), 'reconciled', mod(i, 17) <> 0)
FROM generate_series(%s::bigint, %s::bigint) AS g(i)
ON CONFLICT DO NOTHING
$cmd$, batch_start, LEAST(batch_start + 99999, 3000000))
FROM generate_series(1, 3000000, 100000) AS b(batch_start)
\gexec

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
                          'threeDS', mod(i, 3) = 0, 'installments', mod(i, 12))
FROM generate_series(%s::bigint, %s::bigint) AS g(i)
ON CONFLICT DO NOTHING
$cmd$, batch_start, LEAST(batch_start + 99999, 2000000))
FROM generate_series(1, 2000000, 100000) AS b(batch_start)
\gexec

\echo 'Seed social/chat domain'
SELECT format($cmd$
INSERT INTO citus_scale.conversations
SELECT i,
       ('66000000-0000-0000-0000-' || lpad(i::text, 12, '0'))::uuid,
       (ARRAY['direct','group','channel','support'])[1 + mod(i, 4)],
       CASE WHEN mod(i, 4) = 0 THEN 'Channel ' || i END,
       ARRAY[1 + mod(i * 13, 1000000), 1 + mod(i * 17, 1000000), 1 + mod(i * 19, 1000000)],
       jsonb_build_object('encrypted', mod(i, 3) = 0, 'retentionDays', 7 + mod(i, 358)),
       TIMESTAMPTZ '2023-01-01 00:00:00+00' + mod(i, 1200) * INTERVAL '1 day'
FROM generate_series(%s::bigint, %s::bigint) AS g(i)
ON CONFLICT DO NOTHING
$cmd$, batch_start, LEAST(batch_start + 99999, 500000))
FROM generate_series(1, 500000, 100000) AS b(batch_start)
\gexec

SELECT format($cmd$
INSERT INTO citus_scale.messages
SELECT 1 + mod(i - 1, 500000), i,
       1 + mod(i * 17, 1000000),
       'Synthetic message ' || i || ' about distributed systems, Citus shards, support, orders, and analytics.',
       to_tsvector('simple', 'Synthetic message ' || i || ' distributed systems Citus shards support orders analytics'),
       CASE WHEN mod(i, 10) = 0 THEN ARRAY['attachment-' || i || '.json'] ELSE ARRAY[]::text[] END,
       jsonb_build_object('toxicity', mod(i * 7, 1000) / 1000.0,
                          'flagged', mod(i, 101) = 0, 'language', (ARRAY['vi','en','ja'])[1 + mod(i, 3)]),
       TIMESTAMPTZ '2025-01-01 00:00:00+00' + mod(i, 525600) * INTERVAL '1 minute',
       CASE WHEN mod(i, 20) = 0 THEN TIMESTAMPTZ '2025-01-01 00:05:00+00' + mod(i, 525600) * INTERVAL '1 minute' END
FROM generate_series(%s::bigint, %s::bigint) AS g(i)
ON CONFLICT DO NOTHING
$cmd$, batch_start, LEAST(batch_start + 99999, 3000000))
FROM generate_series(1, 3000000, 100000) AS b(batch_start)
\gexec

SELECT format($cmd$
INSERT INTO citus_scale.message_reactions
SELECT 1 + mod((1 + mod(i - 1, 3000000)) - 1, 500000), i,
       1 + mod(i - 1, 3000000),
       1 + mod(i * 29, 1000000),
       (ARRAY['like','love','laugh','wow','sad','celebrate'])[1 + mod(i, 6)],
       TIMESTAMPTZ '2025-01-01 00:01:00+00' + mod(i, 525600) * INTERVAL '1 minute'
FROM generate_series(%s::bigint, %s::bigint) AS g(i)
ON CONFLICT DO NOTHING
$cmd$, batch_start, LEAST(batch_start + 99999, 1500000))
FROM generate_series(1, 1500000, 100000) AS b(batch_start)
\gexec

\echo 'Seed logistics domain'
SELECT format($cmd$
INSERT INTO citus_scale.shipments
SELECT i,
       'TRK-' || lpad(i::text, 12, '0'),
       (ARRAY['created','picked_up','in_transit','customs','delivered','returned','lost']::citus_scale.shipment_state[])[1 + mod(i, 7)],
       1 + mod(i - 1, 500),
       1 + mod(i * 7 - 1, 500),
       (ARRAY['economy','standard','express','same_day'])[1 + mod(i, 4)],
       round((0.1 + mod(i * 17, 50000) / 1000.0)::numeric, 3),
       ARRAY[10 + mod(i, 100), 10 + mod(i * 3, 100), 5 + mod(i * 7, 100)],
       round((mod(i * 3571, 10000000) / 100.0)::numeric, 2),
       jsonb_build_object('fragile', mod(i, 11) = 0, 'temperatureControlled', mod(i, 97) = 0,
                          'pieces', 1 + mod(i, 10)),
       TIMESTAMPTZ '2025-12-01 00:00:00+00' + mod(i, 274) * INTERVAL '1 day'
FROM generate_series(%s::bigint, %s::bigint) AS g(i)
ON CONFLICT DO NOTHING
$cmd$, batch_start, LEAST(batch_start + 99999, 750000))
FROM generate_series(1, 750000, 100000) AS b(batch_start)
\gexec

SELECT format($cmd$
INSERT INTO citus_scale.tracking_events
SELECT 1 + mod(i - 1, 750000), i,
       TIMESTAMPTZ '2026-01-01 00:00:00+00' + mod(i - 1, 243) * INTERVAL '1 day' + mod(i * 41, 86400) * INTERVAL '1 second',
       (ARRAY['CREATED','PICKUP','DEPARTED','ARRIVED','CUSTOMS','OUT_FOR_DELIVERY','DELIVERED'])[1 + mod(i, 7)],
       'FAC-' || lpad(mod(i, 5000)::text, 5, '0'),
       point(100 + mod(i, 5000) / 100.0, 5 + mod(i, 3000) / 100.0),
       ('192.168.' || mod(i, 255) || '.' || (1 + mod(i, 253)))::inet,
       jsonb_build_object('scanner', 'SCN-' || mod(i, 10000), 'battery', mod(i, 101),
                          'manual', mod(i, 17) = 0)
FROM generate_series(%s::bigint, %s::bigint) AS g(i)
ON CONFLICT DO NOTHING
$cmd$, batch_start, LEAST(batch_start + 99999, 2000000))
FROM generate_series(1, 2000000, 100000) AS b(batch_start)
\gexec

\echo 'Seed healthcare domain'
SELECT format($cmd$
INSERT INTO citus_scale.patients
SELECT 1 + mod(i - 1, 50000), i,
       ('67000000-0000-0000-0000-' || lpad(i::text, 12, '0'))::uuid,
       'Synthetic Patient ' || i,
       DATE '1940-01-01' + mod(i * 17, 30000)::integer,
       (ARRAY['F','M','X'])[1 + mod(i, 3)],
       (ARRAY['O+','O-','A+','A-','B+','B-','AB+','AB-'])[1 + mod(i, 8)],
       CASE WHEN mod(i, 5) = 0 THEN ARRAY['penicillin','pollen'] ELSE ARRAY[]::text[] END,
       jsonb_build_object('phone', '+84' || lpad(mod(i, 1000000000)::text, 9, '0'),
                          'city', (ARRAY['HCM','Ha Noi','Da Nang'])[1 + mod(i, 3)]),
       daterange(DATE '2024-01-01' + mod(i, 365)::integer,
                 DATE '2028-01-01' + mod(i, 365)::integer, '[)'),
       TIMESTAMPTZ '2024-01-01 00:00:00+00' + mod(i, 900) * INTERVAL '1 day'
FROM generate_series(%s::bigint, %s::bigint) AS g(i)
ON CONFLICT DO NOTHING
$cmd$, batch_start, LEAST(batch_start + 99999, 500000))
FROM generate_series(1, 500000, 100000) AS b(batch_start)
\gexec

SELECT format($cmd$
INSERT INTO citus_scale.encounters
SELECT 1 + mod((1 + mod(i - 1, 500000)) - 1, 50000), i,
       1 + mod(i - 1, 500000),
       (ARRAY['outpatient','inpatient','emergency','telehealth'])[1 + mod(i, 4)],
       (ARRAY['planned','in_progress','completed','cancelled'])[1 + mod(i, 4)],
       TIMESTAMPTZ '2025-01-01 00:00:00+00' + mod(i, 525600) * INTERVAL '1 minute',
       CASE WHEN mod(i, 4) = 1 THEN NULL ELSE TIMESTAMPTZ '2025-01-01 01:00:00+00' + mod(i, 525600) * INTERVAL '1 minute' END,
       ARRAY['DX-' || lpad(mod(i, 10000)::text, 4, '0'), 'DX-' || lpad(mod(i * 7, 10000)::text, 4, '0')],
       xmlparse(document '<note><summary>Synthetic encounter ' || i || '</summary></note>'),
       jsonb_build_object('department', (ARRAY['general','cardiology','orthopedics','pediatrics'])[1 + mod(i, 4)],
                          'room', 1 + mod(i, 500))
FROM generate_series(%s::bigint, %s::bigint) AS g(i)
ON CONFLICT DO NOTHING
$cmd$, batch_start, LEAST(batch_start + 99999, 1000000))
FROM generate_series(1, 1000000, 100000) AS b(batch_start)
\gexec

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
       jsonb_build_object('device', 'MED-' || mod(i, 10000), 'verified', mod(i, 7) <> 0)
FROM generate_series(%s::bigint, %s::bigint) AS g(i)
ON CONFLICT DO NOTHING
$cmd$, batch_start, LEAST(batch_start + 99999, 2000000))
FROM generate_series(1, 2000000, 100000) AS b(batch_start)
\gexec

\echo 'Seed customer support domain'
SELECT format($cmd$
INSERT INTO citus_scale.support_tickets
SELECT 1 + mod(i - 1, 500000), i,
       1 + mod(i - 1, 1000000),
       (ARRAY['new','open','waiting_customer','waiting_internal','resolved','closed']::citus_scale.ticket_state[])[1 + mod(i, 6)],
       1 + mod(i, 5),
       'Ticket ' || i || ': help with billing, API, data export, or account access',
       'Synthetic support request ' || i || '. Reproduction steps and expected result stored for UI testing.',
       ARRAY[(ARRAY['billing','api','bug','access','feature'])[1 + mod(i, 5)],
             CASE WHEN mod(i, 10) = 0 THEN 'vip' ELSE 'standard' END],
       tstzrange(TIMESTAMPTZ '2026-01-01 00:00:00+00' + mod(i, 228) * INTERVAL '1 day',
                 TIMESTAMPTZ '2026-01-02 00:00:00+00' + mod(i, 228) * INTERVAL '1 day' + mod(i, 72) * INTERVAL '1 hour', '[)'),
       jsonb_build_object('browser', (ARRAY['Chrome','Edge','Firefox','Safari'])[1 + mod(i, 4)],
                          'appVersion', 'v' || (1 + mod(i, 10)) || '.' || mod(i, 50)),
       TIMESTAMPTZ '2026-01-01 00:00:00+00' + mod(i, 328320) * INTERVAL '1 minute',
       TIMESTAMPTZ '2026-01-01 00:05:00+00' + mod(i, 328320) * INTERVAL '1 minute'
FROM generate_series(%s::bigint, %s::bigint) AS g(i)
ON CONFLICT DO NOTHING
$cmd$, batch_start, LEAST(batch_start + 99999, 750000))
FROM generate_series(1, 750000, 100000) AS b(batch_start)
\gexec

SELECT format($cmd$
INSERT INTO citus_scale.ticket_comments
SELECT 1 + mod((1 + mod(i - 1, 750000)) - 1, 500000), i,
       1 + mod(i - 1, 750000),
       1 + mod(i * 31, 1000000),
       'Synthetic comment ' || i || ': investigation result, workaround, logs, and next action.',
       to_tsvector('simple', 'Synthetic comment investigation result workaround logs next action ' || i),
       mod(i, 7) = 0,
       CASE WHEN mod(i, 12) = 0
            THEN jsonb_build_array(jsonb_build_object('name', 'log-' || i || '.txt', 'bytes', mod(i * 97, 1000000)))
            ELSE '[]'::jsonb END,
       TIMESTAMPTZ '2026-01-01 00:10:00+00' + mod(i, 328320) * INTERVAL '1 minute'
FROM generate_series(%s::bigint, %s::bigint) AS g(i)
ON CONFLICT DO NOTHING
$cmd$, batch_start, LEAST(batch_start + 99999, 1500000))
FROM generate_series(1, 1500000, 100000) AS b(batch_start)
\gexec

\echo 'citus_scale seed complete.'
