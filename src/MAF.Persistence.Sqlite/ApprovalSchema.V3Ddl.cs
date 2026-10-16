namespace MAF.Persistence.Sqlite;
public static partial class ApprovalSchema
{
    internal const string V3Delta = """
        CREATE TABLE cancellation_requests (
            cancel_request_id TEXT PRIMARY KEY NOT NULL
                CHECK(length(cancel_request_id) BETWEEN 1 AND 160),
            operation_id TEXT NOT NULL UNIQUE
                REFERENCES authorization_operations(operation_id) ON DELETE RESTRICT,
            task_id TEXT NOT NULL UNIQUE
                REFERENCES task_controls(task_id) ON DELETE RESTRICT,
            execution_id TEXT NOT NULL CHECK(length(execution_id) BETWEEN 1 AND 160),
            target_operation_id TEXT
                REFERENCES authorization_operations(operation_id) ON DELETE RESTRICT,
            caller_subject TEXT NOT NULL CHECK(length(caller_subject) BETWEEN 1 AND 160),
            authentication_context_id TEXT NOT NULL
                CHECK(length(authentication_context_id) BETWEEN 1 AND 160),
            authority_id TEXT NOT NULL CHECK(length(authority_id) BETWEEN 1 AND 160),
            policy_epoch INTEGER NOT NULL CHECK(policy_epoch > 0),
            policy_version INTEGER NOT NULL CHECK(policy_version > 0),
            policy_digest TEXT NOT NULL CHECK(length(policy_digest) = 64
                AND policy_digest NOT GLOB '*[^0-9A-F]*'),
            evidence_format INTEGER NOT NULL CHECK(evidence_format = 1),
            binding_version INTEGER NOT NULL CHECK(binding_version = 1),
            binding_digest TEXT NOT NULL CHECK(length(binding_digest) = 64
                AND binding_digest NOT GLOB '*[^0-9A-F]*'),
            expected_control_version INTEGER NOT NULL CHECK(expected_control_version >= 0
                AND expected_control_version < 9223372036854775807),
            committed_control_version INTEGER NOT NULL
                CHECK(committed_control_version = expected_control_version + 1),
            evaluated_at_ticks INTEGER NOT NULL CHECK(evaluated_at_ticks BETWEEN 0 AND 3155378975999999999),
            expires_at_ticks INTEGER NOT NULL CHECK(expires_at_ticks BETWEEN 0 AND 3155378975999999999),
            requested_at_ticks INTEGER NOT NULL CHECK(requested_at_ticks BETWEEN 0 AND 3155378975999999999),
            evidence_json TEXT NOT NULL
                CHECK(length(CAST(evidence_json AS BLOB)) BETWEEN 1 AND 16384),
            CHECK(evaluated_at_ticks <= requested_at_ticks
                AND requested_at_ticks < expires_at_ticks)
        );

        ALTER TABLE control_audit ADD COLUMN cancel_request_id TEXT
            REFERENCES cancellation_requests(cancel_request_id) ON DELETE RESTRICT;

        CREATE UNIQUE INDEX one_cancellation_requested_audit
            ON control_audit(cancel_request_id)
            WHERE cancel_request_id IS NOT NULL AND code = 6;

        CREATE TRIGGER cancellation_requests_no_update
        BEFORE UPDATE ON cancellation_requests
        BEGIN
            SELECT RAISE(ABORT, 'CancellationEvidenceImmutable');
        END;

        CREATE TRIGGER cancellation_requests_no_delete
        BEFORE DELETE ON cancellation_requests
        BEGIN
            SELECT RAISE(ABORT, 'CancellationEvidenceImmutable');
        END;
        """;
}
