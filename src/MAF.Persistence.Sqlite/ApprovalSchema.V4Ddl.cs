namespace MAF.Persistence.Sqlite;

public static partial class ApprovalSchema
{
    internal const string V4Delta = """
        ALTER TABLE executions ADD COLUMN session_receipt_required INTEGER NOT NULL DEFAULT 0
            CHECK(session_receipt_required IN (0,1));

        CREATE TRIGGER executions_v4_new_profile
        BEFORE INSERT ON executions
        WHEN NEW.session_receipt_required <> 1
        BEGIN
            SELECT RAISE(ABORT, 'ReceiptProfileRequired');
        END;

        CREATE TRIGGER executions_v4_profile_immutable
        BEFORE UPDATE OF session_receipt_required ON executions
        WHEN NEW.session_receipt_required IS NOT OLD.session_receipt_required
        BEGIN
            SELECT RAISE(ABORT, 'ReceiptProfileImmutable');
        END;

        CREATE TABLE execution_session_receipts (
            receipt_id TEXT PRIMARY KEY NOT NULL
                CHECK(length(CAST(receipt_id AS BLOB)) BETWEEN 1 AND 160),
            task_id TEXT NOT NULL UNIQUE
                REFERENCES executions(task_id) ON DELETE RESTRICT,
            execution_id TEXT NOT NULL UNIQUE
                CHECK(length(CAST(execution_id AS BLOB)) BETWEEN 1 AND 160),
            operation_id TEXT NOT NULL UNIQUE
                REFERENCES authorization_operations(operation_id) ON DELETE RESTRICT,
            approval_id TEXT REFERENCES approval_requests(approval_id) ON DELETE RESTRICT,
            provider_id TEXT NOT NULL
                CHECK(length(CAST(provider_id AS BLOB)) BETWEEN 1 AND 160),
            provider_scope TEXT NOT NULL CHECK(length(provider_scope)=64
                AND provider_scope NOT GLOB '*[^0-9A-F]*'),
            intent_execution_version INTEGER NOT NULL
                CHECK(intent_execution_version BETWEEN 1 AND 9223372036854775806),
            intent_control_version INTEGER NOT NULL CHECK(intent_control_version>=0),
            intent_audit_sequence INTEGER NOT NULL UNIQUE
                REFERENCES control_audit(sequence) ON DELETE RESTRICT,
            intent_at_ticks INTEGER NOT NULL
                CHECK(intent_at_ticks BETWEEN 0 AND 3155378975999999999),
            format_version INTEGER NOT NULL CHECK(format_version=1),
            binding_version INTEGER NOT NULL CHECK(binding_version=1),
            intent_digest TEXT NOT NULL CHECK(length(intent_digest)=64
                AND intent_digest NOT GLOB '*[^0-9A-F]*'),
            intent_json TEXT NOT NULL
                CHECK(length(CAST(intent_json AS BLOB)) BETWEEN 1 AND 16384),
            version INTEGER NOT NULL CHECK(version IN (0,1)),
            status INTEGER NOT NULL CHECK(status IN (0,1,2)),
            provider_session_id TEXT CHECK(provider_session_id IS NULL OR
                length(CAST(provider_session_id AS BLOB)) BETWEEN 1 AND 160),
            response_digest TEXT CHECK(response_digest IS NULL OR
                (length(response_digest)=64 AND response_digest NOT GLOB '*[^0-9A-F]*')),
            response_json TEXT CHECK(response_json IS NULL OR
                length(CAST(response_json AS BLOB)) BETWEEN 1 AND 2048),
            finalized_at_ticks INTEGER CHECK(finalized_at_ticks IS NULL OR
                finalized_at_ticks BETWEEN 0 AND 3155378975999999999),
            response_audit_sequence INTEGER UNIQUE
                REFERENCES control_audit(sequence) ON DELETE RESTRICT,
            CHECK((status=0 AND version=0 AND provider_session_id IS NULL
                AND response_digest IS NULL AND response_json IS NULL
                AND finalized_at_ticks IS NULL AND response_audit_sequence IS NULL)
                OR (status IN (1,2) AND version=1 AND provider_session_id IS NOT NULL
                AND response_digest IS NOT NULL AND response_json IS NOT NULL
                AND finalized_at_ticks IS NOT NULL AND finalized_at_ticks>=intent_at_ticks
                AND response_audit_sequence IS NOT NULL
                AND response_audit_sequence>intent_audit_sequence))
        ) STRICT;

        CREATE UNIQUE INDEX one_original_provider_session
            ON execution_session_receipts(provider_id,provider_scope,provider_session_id)
            WHERE provider_session_id IS NOT NULL;

        CREATE TRIGGER session_receipt_profile_required
        BEFORE INSERT ON execution_session_receipts
        WHEN NEW.status<>0 OR NEW.version<>0 OR NOT EXISTS(SELECT 1 FROM executions
            WHERE task_id=NEW.task_id AND session_receipt_required=1)
        BEGIN
            SELECT RAISE(ABORT, 'ReceiptInitialStateOrProfile');
        END;

        CREATE TRIGGER session_receipt_finalize_once
        BEFORE UPDATE ON execution_session_receipts
        WHEN NOT (OLD.status=0 AND OLD.version=0 AND NEW.status IN (1,2)
            AND NEW.version=1
            AND (NEW.receipt_id,NEW.task_id,NEW.execution_id,NEW.operation_id,
                 NEW.approval_id,NEW.provider_id,NEW.provider_scope,
                 NEW.intent_execution_version,NEW.intent_control_version,
                 NEW.intent_audit_sequence,NEW.intent_at_ticks,NEW.format_version,
                 NEW.binding_version,NEW.intent_digest,NEW.intent_json)
            IS (OLD.receipt_id,OLD.task_id,OLD.execution_id,OLD.operation_id,
                OLD.approval_id,OLD.provider_id,OLD.provider_scope,
                OLD.intent_execution_version,OLD.intent_control_version,
                OLD.intent_audit_sequence,OLD.intent_at_ticks,OLD.format_version,
                OLD.binding_version,OLD.intent_digest,OLD.intent_json))
        BEGIN
            SELECT RAISE(ABORT, 'SessionReceiptImmutable');
        END;

        CREATE TRIGGER session_receipt_no_delete
        BEFORE DELETE ON execution_session_receipts
        BEGIN
            SELECT RAISE(ABORT, 'SessionReceiptImmutable');
        END;

        CREATE TABLE cancellation_deliveries (
            delivery_id TEXT PRIMARY KEY NOT NULL
                CHECK(length(CAST(delivery_id AS BLOB)) BETWEEN 1 AND 160),
            cancel_request_id TEXT NOT NULL UNIQUE
                REFERENCES cancellation_requests(cancel_request_id) ON DELETE RESTRICT,
            receipt_id TEXT NOT NULL UNIQUE
                REFERENCES execution_session_receipts(receipt_id) ON DELETE RESTRICT,
            task_id TEXT NOT NULL UNIQUE
                REFERENCES task_controls(task_id) ON DELETE RESTRICT,
            execution_id TEXT NOT NULL
                CHECK(length(CAST(execution_id AS BLOB)) BETWEEN 1 AND 160),
            abort_operation_id TEXT NOT NULL UNIQUE
                CHECK(length(CAST(abort_operation_id AS BLOB)) BETWEEN 1 AND 160),
            captured_control_version INTEGER NOT NULL CHECK(captured_control_version>=1),
            receipt_digest TEXT NOT NULL CHECK(length(receipt_digest)=64
                AND receipt_digest NOT GLOB '*[^0-9A-F]*'),
            intent_audit_sequence INTEGER NOT NULL UNIQUE
                REFERENCES control_audit(sequence) ON DELETE RESTRICT,
            intent_at_ticks INTEGER NOT NULL
                CHECK(intent_at_ticks BETWEEN 0 AND 3155378975999999999),
            budget_ticks INTEGER NOT NULL CHECK(budget_ticks BETWEEN 1 AND 50000000),
            format_version INTEGER NOT NULL CHECK(format_version=1),
            binding_version INTEGER NOT NULL CHECK(binding_version=1),
            intent_digest TEXT NOT NULL CHECK(length(intent_digest)=64
                AND intent_digest NOT GLOB '*[^0-9A-F]*'),
            intent_json TEXT NOT NULL
                CHECK(length(CAST(intent_json AS BLOB)) BETWEEN 1 AND 16384),
            version INTEGER NOT NULL CHECK(version BETWEEN 0 AND 9223372036854775806),
            delivery_stage INTEGER NOT NULL CHECK(delivery_stage IN (0,1,2,3)),
            acknowledgement_at_ticks INTEGER CHECK(acknowledgement_at_ticks IS NULL OR
                acknowledgement_at_ticks BETWEEN 0 AND 3155378975999999999),
            remote_outcome INTEGER NOT NULL CHECK(remote_outcome IN (0,1,2,3,4)),
            last_observed_at_ticks INTEGER CHECK(last_observed_at_ticks IS NULL OR
                last_observed_at_ticks BETWEEN 0 AND 3155378975999999999),
            observation_digest TEXT CHECK(observation_digest IS NULL OR
                (length(observation_digest)=64 AND observation_digest NOT GLOB '*[^0-9A-F]*')),
            observation_json TEXT CHECK(observation_json IS NULL OR
                length(CAST(observation_json AS BLOB)) BETWEEN 1 AND 8192),
            last_audit_sequence INTEGER NOT NULL
                REFERENCES control_audit(sequence) ON DELETE RESTRICT,
            CHECK((delivery_stage=2 AND acknowledgement_at_ticks IS NOT NULL
                AND acknowledgement_at_ticks>=intent_at_ticks)
                OR (delivery_stage<>2 AND acknowledgement_at_ticks IS NULL)),
            CHECK((version=0 AND delivery_stage=0 AND remote_outcome=0
                AND last_observed_at_ticks IS NULL AND observation_digest IS NULL
                AND observation_json IS NULL AND last_audit_sequence=intent_audit_sequence)
                OR (version>0 AND last_observed_at_ticks IS NOT NULL
                AND last_observed_at_ticks>=intent_at_ticks AND observation_digest IS NOT NULL
                AND observation_json IS NOT NULL AND last_audit_sequence>intent_audit_sequence))
        ) STRICT;

        CREATE TRIGGER delivery_requires_verified_receipt
        BEFORE INSERT ON cancellation_deliveries
        WHEN NEW.version<>0 OR NEW.delivery_stage<>0 OR NEW.remote_outcome<>0
            OR NOT EXISTS(SELECT 1 FROM execution_session_receipts r
            JOIN cancellation_requests c ON c.cancel_request_id=NEW.cancel_request_id
            WHERE r.receipt_id=NEW.receipt_id AND r.status=1 AND r.version=1
              AND r.task_id=NEW.task_id AND r.execution_id=NEW.execution_id
              AND c.task_id=NEW.task_id AND c.execution_id=NEW.execution_id
              AND c.committed_control_version=NEW.captured_control_version)
        BEGIN
            SELECT RAISE(ABORT, 'DeliveryReceiptMismatch');
        END;

        CREATE TRIGGER delivery_cas_and_immutable_binding
        BEFORE UPDATE ON cancellation_deliveries
        WHEN NOT (NEW.version=OLD.version+1
            AND NEW.last_audit_sequence>OLD.last_audit_sequence
            AND NEW.last_observed_at_ticks>=coalesce(OLD.last_observed_at_ticks,OLD.intent_at_ticks)
            AND (NEW.delivery_id,NEW.cancel_request_id,NEW.receipt_id,NEW.task_id,
                 NEW.execution_id,NEW.abort_operation_id,NEW.captured_control_version,
                 NEW.receipt_digest,NEW.intent_audit_sequence,NEW.intent_at_ticks,
                 NEW.budget_ticks,NEW.format_version,NEW.binding_version,NEW.intent_digest,NEW.intent_json)
            IS (OLD.delivery_id,OLD.cancel_request_id,OLD.receipt_id,OLD.task_id,
                OLD.execution_id,OLD.abort_operation_id,OLD.captured_control_version,
                OLD.receipt_digest,OLD.intent_audit_sequence,OLD.intent_at_ticks,
                OLD.budget_ticks,OLD.format_version,OLD.binding_version,OLD.intent_digest,OLD.intent_json)
            AND (NEW.delivery_stage=OLD.delivery_stage
                OR (OLD.delivery_stage=0 AND NEW.delivery_stage IN (1,2,3)))
            AND (OLD.delivery_stage<>2 OR NEW.acknowledgement_at_ticks IS OLD.acknowledgement_at_ticks)
            AND (OLD.remote_outcome=0 OR NEW.remote_outcome=OLD.remote_outcome))
        BEGIN
            SELECT RAISE(ABORT, 'DeliveryVersionOrBindingConflict');
        END;

        CREATE TRIGGER delivery_no_delete
        BEFORE DELETE ON cancellation_deliveries
        BEGIN
            SELECT RAISE(ABORT, 'DeliveryEvidenceImmutable');
        END;
        """;
}
