# ADR 0005: Submitted-email production from analytics

**Status:** accepted

**Date:** 2026-10-13

## Context

The legacy sender uses submission-time organisation fields and the original
Submitted audit. Analytics may be delayed until the declaration is Accepted or
Cancelled. Event snapshots cannot replace a failed authorised declaration read.

## Decision

For `submission.created` compliance-declaration events, read the latest declaration
through the existing organisation/declaration API with a Basic service client and
read scope. Require matching identities and equal-or-newer entity version; use the
first stored Submitted audit and persisted organisation values. Keep legacy
direct/scheme and country-based English/Welsh policy in the producer. Four UUID
template mappings belong to `SubmissionEmail` options.

The intended-email key is `submission:{organisationGuid:D}:{declarationObjectId}`.
Validated organisation and declaration identities determine it independently of
transport IDs, entity version, current status, templates and processing time.
Replays publish the same intention; existing terminal evidence suppresses delivery.
Changed mapped immutable fields conflict with that evidence rather than creating a
new email. No producer replay ledger or historical-version endpoint is introduced.

Acknowledge analytics only after confirmed command publication. Delivery continues
through the existing publisher, recipient lane, HMAC references, claims and cutover.
Fixed safe diagnostics correlate publication and delivery with the same Notify
reference; publication and acceptance never claim recipient delivery.

Each consumer receives one message. Analytics owns a separate validated 65-second
receive/read/publish/delete/headroom budget within 120-second receive visibility;
command delivery retains its existing budget. Cancellation and monotonic checks
reject late confirmation before the next side effect. Future batching requires a
reviewed budget. Success and empty responses immediately receive again; failures
back off without acknowledging. Dependency cancellation is distinct from shutdown.

## Consequences

Read credentials, deployed MO-562 version responses, template account access and
queue redrive policies require deployment verification. Local examples and tests
do not establish deployed readiness. Null cutover enables terminal-suppression dry
runs; live shared cutover still waits for MO-550 cancellation production.
Indeterminate publication can replay, and indeterminate Notify acceptance retains
the accepted duplicate-send risk. Recipient lanes preserve publication order, not
lifecycle order or inbox order; cancellation sequencing remains follow-on work.
