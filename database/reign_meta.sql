CREATE SCHEMA IF NOT EXISTS reign_meta;

CREATE TABLE IF NOT EXISTS reign_meta.storage_version (
    component text PRIMARY KEY,
    version integer NOT NULL,
    applied_utc timestamptz NOT NULL DEFAULT now()
);

INSERT INTO reign_meta.storage_version(component, version)
VALUES ('reign_postgresql', 1)
ON CONFLICT(component) DO NOTHING;

CREATE TABLE IF NOT EXISTS reign_meta.campaign_registry (
    campaign_id text PRIMARY KEY,
    schema_name text NOT NULL UNIQUE,
    label text NOT NULL DEFAULT '',
    created_utc timestamptz NOT NULL DEFAULT now(),
    updated_utc timestamptz NOT NULL DEFAULT now(),
    migrated_from_sqlite_utc timestamptz NULL,
    migration_state text NOT NULL DEFAULT 'postgresql_native',
    migration_report_json jsonb NOT NULL DEFAULT '{}'::jsonb
);

CREATE TABLE IF NOT EXISTS reign_meta.save_sync_snapshots (
    campaign_id text NOT NULL,
    save_point_id text NOT NULL,
    schema_name text NOT NULL UNIQUE,
    created_utc timestamptz NOT NULL DEFAULT now(),
    source_schema_size_bytes bigint NOT NULL DEFAULT 0,
    PRIMARY KEY(campaign_id, save_point_id),
    FOREIGN KEY(campaign_id)
        REFERENCES reign_meta.campaign_registry(campaign_id)
        ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS reign_meta.migration_runs (
    migration_id text PRIMARY KEY,
    campaign_id text NOT NULL,
    source_path text NOT NULL DEFAULT '',
    source_bytes bigint NOT NULL DEFAULT 0,
    status text NOT NULL,
    started_utc timestamptz NOT NULL DEFAULT now(),
    completed_utc timestamptz NULL,
    source_table_count integer NOT NULL DEFAULT 0,
    destination_table_count integer NOT NULL DEFAULT 0,
    source_row_count bigint NOT NULL DEFAULT 0,
    destination_row_count bigint NOT NULL DEFAULT 0,
    validation_json jsonb NOT NULL DEFAULT '{}'::jsonb,
    error_text text NOT NULL DEFAULT ''
);

-- Final conversation qualification is controller state, not campaign state.
-- It deliberately lives outside reign_campaign_* so Save Sync rollback can
-- rewind dialogue/memory evidence without erasing leases, attempts, budgets,
-- or the audit trail that observes that rollback.
CREATE TABLE IF NOT EXISTS reign_meta.final_gauntlet_runs (
    run_id text PRIMARY KEY,
    campaign_id text NOT NULL,
    stage text NOT NULL,
    build_version text NOT NULL,
    state text NOT NULL,
    created_ts bigint NOT NULL,
    updated_ts bigint NOT NULL
);

CREATE TABLE IF NOT EXISTS reign_meta.final_gauntlet_cases (
    run_id text NOT NULL,
    case_instance_id text NOT NULL,
    family text NOT NULL,
    evaluation_kind text NOT NULL,
    execution_kind text NOT NULL,
    mode text NOT NULL,
    requires_provider bigint NOT NULL,
    requires_game bigint NOT NULL,
    requirement_ids text NOT NULL,
    tags text NOT NULL,
    behavioral_requirement text NOT NULL,
    hard_prohibitions text NOT NULL,
    prerequisite_capabilities text NOT NULL,
    evidence_needs text NOT NULL,
    state text NOT NULL,
    created_ts bigint NOT NULL,
    updated_ts bigint NOT NULL,
    PRIMARY KEY(run_id, case_instance_id),
    FOREIGN KEY(run_id)
        REFERENCES reign_meta.final_gauntlet_runs(run_id)
        ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS reign_meta.final_gauntlet_attempts (
    run_id text NOT NULL,
    case_instance_id text NOT NULL,
    attempt_number bigint NOT NULL,
    correlation_id text NOT NULL,
    status text NOT NULL,
    error text NOT NULL DEFAULT '',
    created_ts bigint NOT NULL,
    PRIMARY KEY(run_id, case_instance_id, attempt_number),
    UNIQUE(run_id, correlation_id),
    FOREIGN KEY(run_id, case_instance_id)
        REFERENCES reign_meta.final_gauntlet_cases(
            run_id, case_instance_id)
        ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS reign_meta.final_gauntlet_assertions (
    run_id text NOT NULL,
    case_instance_id text NOT NULL,
    assertion_id text NOT NULL,
    passed bigint NOT NULL,
    payload_json text NOT NULL,
    created_ts bigint NOT NULL,
    PRIMARY KEY(run_id, case_instance_id, assertion_id),
    FOREIGN KEY(run_id, case_instance_id)
        REFERENCES reign_meta.final_gauntlet_cases(
            run_id, case_instance_id)
        ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS reign_meta.final_gauntlet_evidence (
    run_id text NOT NULL,
    case_instance_id text NOT NULL,
    evidence_key text NOT NULL,
    schema_version bigint NOT NULL,
    payload_json text NOT NULL,
    created_ts bigint NOT NULL,
    PRIMARY KEY(run_id, case_instance_id, evidence_key),
    FOREIGN KEY(run_id, case_instance_id)
        REFERENCES reign_meta.final_gauntlet_cases(
            run_id, case_instance_id)
        ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS reign_meta.final_gauntlet_review_items (
    run_id text NOT NULL,
    review_id text NOT NULL,
    case_instance_id text NOT NULL,
    blinded_payload_json text NOT NULL,
    answer_key_json text NOT NULL,
    created_ts bigint NOT NULL,
    PRIMARY KEY(run_id, review_id),
    FOREIGN KEY(run_id, case_instance_id)
        REFERENCES reign_meta.final_gauntlet_cases(
            run_id, case_instance_id)
        ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS reign_meta.final_gauntlet_provider_correlations (
    run_id text NOT NULL,
    case_instance_id text NOT NULL,
    correlation_id text NOT NULL,
    created_ts bigint NOT NULL,
    PRIMARY KEY(run_id, correlation_id),
    FOREIGN KEY(run_id, case_instance_id)
        REFERENCES reign_meta.final_gauntlet_cases(
            run_id, case_instance_id)
        ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS reign_meta.final_gauntlet_provider_calls (
    run_id text NOT NULL,
    provider_call_id text NOT NULL,
    case_instance_id text NOT NULL,
    correlation_id text NOT NULL,
    request_type text NOT NULL,
    model text NOT NULL,
    physical_attempt bigint NOT NULL,
    ordinal bigint NOT NULL,
    status text NOT NULL,
    started_ts bigint NOT NULL,
    completed_ts bigint NOT NULL DEFAULT 0,
    error text NOT NULL DEFAULT '',
    PRIMARY KEY(run_id, provider_call_id),
    UNIQUE(run_id, ordinal),
    FOREIGN KEY(run_id, case_instance_id)
        REFERENCES reign_meta.final_gauntlet_cases(
            run_id, case_instance_id)
        ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS reign_meta.final_gauntlet_schedule (
    run_id text PRIMARY KEY,
    campaign_id text NOT NULL,
    state text NOT NULL,
    stage_b_scheduled bigint NOT NULL DEFAULT 0,
    baseline_save_name text NOT NULL DEFAULT '',
    manifest_fingerprint text NOT NULL,
    catalog_fingerprint text NOT NULL,
    settings_fingerprint text NOT NULL,
    state_fingerprint text NOT NULL,
    pause_requested bigint NOT NULL DEFAULT 0,
    created_ts bigint NOT NULL,
    updated_ts bigint NOT NULL,
    FOREIGN KEY(run_id)
        REFERENCES reign_meta.final_gauntlet_runs(run_id)
        ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_final_gauntlet_schedule_campaign
ON reign_meta.final_gauntlet_schedule(campaign_id, state);

CREATE TABLE IF NOT EXISTS reign_meta.final_gauntlet_leases (
    run_id text NOT NULL,
    case_instance_id text NOT NULL,
    controller_id text NOT NULL,
    leased_ts bigint NOT NULL,
    expires_ts bigint NOT NULL,
    correlation_ledger_json text NOT NULL,
    PRIMARY KEY(run_id, case_instance_id),
    FOREIGN KEY(run_id, case_instance_id)
        REFERENCES reign_meta.final_gauntlet_cases(
            run_id, case_instance_id)
        ON DELETE CASCADE
);

INSERT INTO reign_meta.storage_version(component, version)
VALUES ('final_gauntlet_control', 1)
ON CONFLICT(component) DO UPDATE
SET version=excluded.version, applied_utc=excluded.applied_utc;

CREATE OR REPLACE FUNCTION reign_meta.assert_managed_schema(schema_name text)
RETURNS void AS $$
BEGIN
    IF schema_name IS NULL
       OR (schema_name NOT LIKE 'reign_campaign_%'
           AND schema_name NOT LIKE 'reign_save_%') THEN
        RAISE EXCEPTION 'Schema is not managed by Reign: %', schema_name;
    END IF;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION reign_meta.drop_schema_safe(schema_name text)
RETURNS boolean AS $$
BEGIN
    PERFORM reign_meta.assert_managed_schema(schema_name);
    EXECUTE format('DROP SCHEMA IF EXISTS %I CASCADE', schema_name);
    RETURN true;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION reign_meta.deduplicate_schema_indexes(schema_name text)
RETURNS integer AS $$
DECLARE
    obj record;
    removed integer := 0;
BEGIN
    PERFORM reign_meta.assert_managed_schema(schema_name);
    FOR obj IN
        WITH ranked AS (
            SELECT
                index_class.relname AS index_name,
                constraint_row.oid AS constraint_oid,
                row_number() OVER (
                    PARTITION BY index_row.indrelid,
                        index_row.indisunique,
                        index_row.indisprimary,
                        index_row.indisexclusion,
                        index_row.indkey::text,
                        index_row.indcollation::text,
                        index_row.indclass::text,
                        index_row.indoption::text,
                        COALESCE(pg_get_expr(index_row.indexprs,
                            index_row.indrelid), ''),
                        COALESCE(pg_get_expr(index_row.indpred,
                            index_row.indrelid), '')
                    ORDER BY
                        CASE WHEN constraint_row.oid IS NOT NULL THEN 0
                             WHEN left(index_class.relname,4)='idx_' THEN 1
                             ELSE 2 END,
                        index_class.relname
                ) AS duplicate_rank
            FROM pg_index index_row
            JOIN pg_class table_class
              ON table_class.oid=index_row.indrelid
            JOIN pg_namespace table_namespace
              ON table_namespace.oid=table_class.relnamespace
            JOIN pg_class index_class
              ON index_class.oid=index_row.indexrelid
            LEFT JOIN pg_constraint constraint_row
              ON constraint_row.conindid=index_row.indexrelid
            WHERE table_namespace.nspname=schema_name
        )
        SELECT index_name
        FROM ranked
        WHERE duplicate_rank>1 AND constraint_oid IS NULL
        ORDER BY index_name
    LOOP
        EXECUTE format('DROP INDEX %I.%I', schema_name, obj.index_name);
        removed := removed + 1;
    END LOOP;
    RETURN removed;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION reign_meta.clone_schema(source_schema text, destination_schema text)
RETURNS void AS $$
DECLARE
    obj record;
    source_sequence_value bigint;
    sequence_name text;
    source_default text;
    destination_default text;
BEGIN
    PERFORM reign_meta.assert_managed_schema(source_schema);
    PERFORM reign_meta.assert_managed_schema(destination_schema);

    IF source_schema = destination_schema THEN
        RAISE EXCEPTION 'Source and destination schemas must differ.';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.schemata WHERE schema_name=source_schema) THEN
        RAISE EXCEPTION 'Source schema does not exist: %', source_schema;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.schemata WHERE schema_name=destination_schema) THEN
        RAISE EXCEPTION 'Destination schema already exists: %', destination_schema;
    END IF;

    EXECUTE format('CREATE SCHEMA %I', destination_schema);

    FOR obj IN
        SELECT tablename
        FROM pg_tables
        WHERE schemaname=source_schema
        ORDER BY tablename
    LOOP
        EXECUTE format(
            'CREATE TABLE %I.%I (LIKE %I.%I INCLUDING ALL)',
            destination_schema, obj.tablename, source_schema, obj.tablename);
    END LOOP;

    -- LIKE INCLUDING ALL faithfully copies every source index, including
    -- redundant generations left by an older Save Sync clone. Remove
    -- structural duplicates before loading any rows so snapshots do not
    -- amplify storage and write costs on every save/load cycle.
    PERFORM reign_meta.deduplicate_schema_indexes(destination_schema);

    FOR obj IN
        SELECT tablename
        FROM pg_tables
        WHERE schemaname=source_schema
        ORDER BY tablename
    LOOP
        EXECUTE format(
            'INSERT INTO %I.%I SELECT * FROM %I.%I',
            destination_schema, obj.tablename, source_schema, obj.tablename);
    END LOOP;

    FOR obj IN
        SELECT sequencename
        FROM pg_sequences
        WHERE schemaname=source_schema
        ORDER BY sequencename
    LOOP
        EXECUTE format('SELECT last_value FROM %I.%I', source_schema, obj.sequencename)
            INTO source_sequence_value;
        EXECUTE format('CREATE SEQUENCE %I.%I', destination_schema, obj.sequencename);
        EXECUTE format(
            'SELECT setval(%L, %s, true)',
            destination_schema || '.' || obj.sequencename,
            source_sequence_value);
    END LOOP;

    FOR obj IN
        SELECT table_name, column_name, column_default
        FROM information_schema.columns
        WHERE table_schema=destination_schema
          AND column_default LIKE 'nextval(%'
        ORDER BY table_name, ordinal_position
    LOOP
        source_default := obj.column_default;
        sequence_name := substring(source_default from '''([^'']+)''');
        IF position('.' in sequence_name) > 0 THEN
            sequence_name := substring(sequence_name from '[^.]+$');
        END IF;
        IF sequence_name IS NULL OR sequence_name = '' THEN
            RAISE EXCEPTION 'Unable to resolve sequence for %.%', obj.table_name, obj.column_name;
        END IF;
        destination_default := format(
            'nextval(%L::regclass)',
            destination_schema || '.' || sequence_name);
        EXECUTE format(
            'ALTER TABLE %I.%I ALTER COLUMN %I SET DEFAULT %s',
            destination_schema, obj.table_name, obj.column_name, destination_default);
        EXECUTE format(
            'ALTER SEQUENCE %I.%I OWNED BY %I.%I.%I',
            destination_schema, sequence_name,
            destination_schema, obj.table_name, obj.column_name);
    END LOOP;

    FOR obj IN
        SELECT table_name, view_definition
        FROM information_schema.views
        WHERE table_schema=source_schema
        ORDER BY table_name
    LOOP
        EXECUTE format(
            'CREATE VIEW %I.%I AS %s',
            destination_schema,
            obj.table_name,
            replace(obj.view_definition, source_schema || '.', destination_schema || '.'));
    END LOOP;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION reign_meta.replace_schema(source_schema text, destination_schema text)
RETURNS void AS $$
BEGIN
    PERFORM reign_meta.assert_managed_schema(source_schema);
    PERFORM reign_meta.assert_managed_schema(destination_schema);
    IF source_schema = destination_schema THEN
        RAISE EXCEPTION 'Source and destination schemas must differ.';
    END IF;
    PERFORM reign_meta.drop_schema_safe(destination_schema);
    PERFORM reign_meta.clone_schema(source_schema, destination_schema);
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION reign_meta.get_schema_size(schema_name text)
RETURNS bigint AS $$
DECLARE
    total_size bigint;
BEGIN
    PERFORM reign_meta.assert_managed_schema(schema_name);
    SELECT COALESCE(sum(pg_total_relation_size(format('%I.%I', schemaname, tablename))), 0)
    INTO total_size
    FROM pg_tables
    WHERE schemaname=schema_name;
    RETURN total_size;
END;
$$ LANGUAGE plpgsql STABLE;

DO $$
DECLARE
    registered_schema record;
    current_version integer;
BEGIN
    SELECT version INTO current_version
    FROM reign_meta.storage_version
    WHERE component='reign_postgresql';
    IF COALESCE(current_version, 0) < 2 THEN
        FOR registered_schema IN
            SELECT schema_name FROM reign_meta.campaign_registry
            UNION
            SELECT schema_name FROM reign_meta.save_sync_snapshots
        LOOP
            IF EXISTS (SELECT 1 FROM pg_namespace
                       WHERE nspname=registered_schema.schema_name) THEN
                PERFORM reign_meta.deduplicate_schema_indexes(
                    registered_schema.schema_name);
            END IF;
        END LOOP;
        UPDATE reign_meta.storage_version
        SET version=2,applied_utc=now()
        WHERE component='reign_postgresql';
    END IF;
END;
$$;
