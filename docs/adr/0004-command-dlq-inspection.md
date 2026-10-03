# 0004: Authenticated command-DLQ inspection, redrive and discard

Status: Accepted

## Context

Operators need to inspect failed notification commands without exposing message
content or altering delivery evidence. Every host consumes commands after startup
readiness; null cutover permanently suppresses them. The approved administration contract uses Waste Obligations' Basic/OAuth ACL
and administrator endpoint structure.

## Decision

Use Waste Obligations' Basic and Bearer providers, `Acl.Clients` configuration
and authenticated Admin policy requiring an ACL `admin` scope. Basic maps ApiKey
clients; Bearer maps exactly one `client_id` to an OAuth client. Build privileges
only from ACL scopes, excluding incoming token scope/role claims. Administration
is always registered, independently of sending. An empty ACL or one without an
admin scope permits startup but denies administrator calls; configured entries
remain validated. OAuth needs no Basic secret. Preserve generic malformed-header denial and strict Basic UTF-8 decoding.

The user amended the original Basic-only decision on 2026-10-01 and explicitly
approved Waste Obligations' gateway-only validation. The private CDP gateway
owns JWT signature, issuer and audience validation; this service parses tokens
and retains framework lifetime checks with the default five-minute clock skew.
Direct backend callers can assert an ACL identity, so backend access is a
trusted deployment boundary requiring separate network controls. Another CDP
service could forge an unexpired token for a known OAuth admin client ID and
permanently abandon delivery; client IDs are public identifiers. This is the
consequence of this service's explicitly approved gateway-only contract. Gateway
Cognito authentication must cover every administrator route. This decision
does not provision deployed access or claim in-service signature validation.

Start/check Mongo migrations on every host, including with null cutover. Require
valid command/DLQ queues, digest secrets, Mongo and Notify settings and complete
sending budgets at startup. Critical migrations gate `/health`. The admin HTTP boundary
returns 503 until its first successful anonymous response completes. Inspection
then receives one visible FIFO
DLQ message. It does not publish, delete or modify delivery evidence. A bounded
dependency timeout rejects cancelled or late results.

Return only minimal metadata. The user explicitly approved exact raw idempotency
key and notification type in authenticated responses, including private-bearing
spellings. This exception does not extend to logs, metrics, tokens or other
message fields. Invalid commands expose neither partial identity nor a usable
selection. Historical dependency errors remain unavailable, so the response
contains current classification without a constant failure-details placeholder.

Sign a content-free selection with the existing evidence secret and a distinct
HMAC domain. Format `v2` contains receive-attempt ID, opaque SQS message ID, HMAC
queue binding, absolute UTC expiry, immutable-field digest and original receive
visibility timeout. Replay uses the signed original timeout across hosts rather
than deriving a different value from remaining lifetime. Reject old `v1` tokens
and require fresh inspection. Replay can reset visibility beyond token expiry,
but the signed expiry still prevents further effects. Anchor expiry
before receive and keep it below AWS's five-minute receive-attempt window.
Correctly configured hosts validate the same selection without host-local keys
or storing payloads.

Redrive replays the signed receive attempt, verifies the selected message and
immutable evidence, then publishes its exact body/encoding to the canonical
recipient lane. Use `v1:command-dlq-redrive:` plus a JSON array of source DLQ URL
and SQS message ID as the HMAC input with the evidence secret, returning a
versioned hexadecimal transport ID. This domain differs from normal command-key
publication and is stable across hosts and retries, permitting recovery while
SQS remembers a consumed original copy. Confirm publication before deleting only
the selected receipt. Bound the entire operation by dependency timeout and
selection expiry, rejecting late confirmations before further effects. Redrive
never changes delivery evidence; normal consumption retains terminal, conflict
and active-claim guards.

Discard repeats selection validation under one bounded deadline and records
`delivery-abandoned` before deleting only the selected receipt. Use the unique
notification-key index and a server-`$$NOW` pipeline: create minimal abandonment
or replace only matching expired pending evidence, retaining its record ID and
clearing the obsolete owner/lease. A matching abandoned record supports
idempotent deletion. Refuse active owners, immutable conflicts, accepted,
suppressed and unknown outcomes without rewriting their history. The claim and
acceptance predicates cannot both win against the abandonment transition.

New abandonment retains the original eight-field shape. Store the exact command
notification type, consistent with accepted/suppressed records, while the immutable
digest preserves the original command type and fields. This supersedes the
earlier abandonment-only diagnostic projection following the PR review.
Existing abandoned records retain their stored diagnostic label; their original
type may be unrecoverable and no backfill or terminal-history rewrite is performed.
Private-bearing type values remain excluded from logs and metrics. Do not persist raw key, recipient, body
or template. Failed or late writes preserve the source; failed deletion retains
abandonment for later completion. Future duplicates are suppressed without
Notify.

## Consequences

Malformed commands and immutable conflicts cannot be cleared through these APIs.
They require configured queue retention or deployment-owned controlled removal;
this decision supplies no verified removal tool. Preserve the evidence secret for
durable identity, selection signing, Notify references and redrive deduplication.
These uses have distinct HMAC domains; key rotation is unsupported.

Inspection temporarily changes visibility and receive count. All management
routes are registered and authenticate through the configured admin ACL.
Deployments supply credentials and operator access separately.
Recovery joins current lane order. An indeterminate publication can leave both
source and destination; failed deletion preserves publication. Repeated recovery
is deduplicated inside SQS's window and checked against durable command identity
on consumption. A prior indeterminate Notify request retains its accepted
duplicate-email risk. Abandonment prevents future attempts but does not prove an
earlier indeterminate request failed.

Floci does not implement receive-attempt replay. A labelled test-only API adapter
supplies that boundary while FIFO effects and Mongo remain real; deployment
validation still needs native AWS replay evidence, following the native SQS
runbook. Local results cannot supply that evidence, including replay with changed
visibility parameters. The user accepted always-registered authenticated routes
while that evidence is unavailable.

## References

- [Waste Obligations authentication at e2bacd5](https://github.com/DEFRA/waste-obligations/tree/e2bacd53cc3158df41ee22eebf1fa9b3eee30476/src/Api/Authentication)
- [AWS ReceiveMessage](https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_ReceiveMessage.html)
- [Service behaviour](../service-behaviour.md#command-dlq-inspection)

The user removed the administration enablement switch on 2026-10-02. Native FIFO
replay remains unverified; this is an evidence gap rather than an endpoint-registration
gate. Always-registered Basic/OAuth admin protection and the approved gateway trust
contract apply to every route.

The user approved an authenticated body-free verification-command route on
2026-10-02. It confirms fresh permanent suppression before enqueueing a fixed
synthetic command on the configured DLQ. Normal
redrive and consumption delete the terminal duplicate without Notify or evidence
changes. This supplies an operator probe for inspect/replay/redrive while native
AWS proof is unavailable. It does not prove successful discard or authorise
agent testing of shared queues or cloud provisioning.
