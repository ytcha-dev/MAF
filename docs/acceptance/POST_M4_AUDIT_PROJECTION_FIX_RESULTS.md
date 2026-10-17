# Post-M4 audit projection fix

Date: 2026-10-17. Baseline: `34e48811def5d44d0fadba473b48fe2a050cb4ee`.

## Root cause and bounded correction

`SqliteApprovalStore.ReadAuditAsync()` selected `cancel_request_id` only for
ExplicitV3. ExplicitV4 incorrectly selected SQL NULL although its column exists.
The projection now selects the actual column for exactly ExplicitV3 and ExplicitV4;
ExplicitV2 retains NULL because that schema has no cancellation column.

The only production change is this projection condition. No schema, migration,
stored evidence, authorization, cancellation behavior or provider path changed.
Surrounding audit readers were inspected: receipt/delivery validators already read
the column directly under v4; the cancellation validator filters by the exact
cancel ID and uses its private audit projection only to validate other fields.
Separate v3/v4 schema validators already enforce their respective exact versions.
No directly equivalent version omission was found.

## Regression evidence

New [AuditProjectionTests](../../tests/MAF.Tests/AuditProjectionTests.cs) use only
synthetic disposable SQLite fixtures and existing test trust. Two versioned cases
prove v3/v4 cancellation ID preservation, ordinary audit nulls, exact paging and
ascending ordering. A v2 case proves compatibility without the column. All three
check page sizes 1 and 100, exclusive cursors, end-of-results and rejection of
negative cursors and limits -1, 0 and 101.

Before the fix, these unchanged assertions discovered 3 cases: 2 passed and the
v4 case failed with expected `cancel-one`, actual null. After the fix, the focused
filter covering AuditProjectionTests, ApprovalPersistenceTests and
CancellationAdmissionTests passed 104/104, with zero failures or skips.

Full offline regression (excluding `MAF.Tests.OpenCodeLiveTests`) passed 976/976,
with zero failures or skips, in 2 minutes 6 seconds. The historical M4-C baseline
was 973; all 973 test names remain, with exactly the three new projection cases.
Evidence: `post-m4-audit-offline.trx` in the same local results directory.

Solution build (`dotnet build MAF.slnx --no-restore`) passed with 0 warnings and
0 errors. TRX evidence is retained locally under `TestResults/home-validation/`
as `post-m4-audit-before.trx` and `post-m4-audit-focused.trx`.

No live provider HTTP or external model execution was performed. Existing offline
regression fixtures retain their controlled fake transports and test-owned process
checks. Production trust and activation remain disabled. This maintenance increment
does not authorize Phase 2B-4 or any later milestone.
