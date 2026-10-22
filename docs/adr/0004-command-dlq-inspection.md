# 0004: Authenticated command-DLQ inspection, redrive and discard

Status: Accepted

## Context

Operators need to inspect failed notification commands with authenticated access to email inputs, without altering delivery evidence. Every host consumes commands after startup
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
then receives up to ten visible FIFO
DLQ messages in one request. It does not publish, delete or modify delivery evidence. A bounded
dependency timeout rejects cancelled or late results.

Authenticated inspection returns a `messages` array containing valid commands' email
inputs, including recipient, template ID and personalisation, with SQS metadata and
current delivery classification. The user approved this operator-view expansion on
2026-10-08. Command content remains excluded from logs, metrics, tokens and Mongo
evidence. Invalid commands expose no partially extracted command fields or usable
selection. Historical dependency errors remain unavailable.

Use a content-free, expiring selection signed with the evidence secret and a
separate HMAC domain. A selection binds the original receive attempt and immutable
identity, allowing another correctly configured host to recover without retaining
payloads. Replay can reset visibility beyond authorization expiry; expiry must
still prevent further effects. The exact version and compatibility rules belong
in [selection tokens](../../src/Consumer/Administration/CommandDlqSelectionTokens.cs).

Selected redrive republishes unchanged content before removing the selected copy.
A distinct stable transport identity permits recovery while SQS remembers an
original consumed copy; durable command identity still governs subsequent sends.
Batch recovery is intentionally not atomic. Discard records durable abandonment
before removal, refusing protected or conflicting history. It prevents future
attempts without establishing whether an earlier indeterminate Notify call failed.
These choices preserve terminal evidence while permitting safe completion after
uncertain responses; do not introduce a second recovery ledger.

See [redriver](../../src/Consumer/Administration/CommandDlqRedriver.cs),
[discarder](../../src/Consumer/Administration/CommandDlqDiscarder.cs) and
[store](../../src/Consumer/Delivery/MongoNotificationDeliveryRecordStore.cs) for
current guards, response models and persistence. Abandonment retains exact command
type consistent with other outcomes; earlier diagnostic labels remain unchanged
without backfill. Private-bearing type values stay excluded from diagnostics.

On 2026-10-08 the user approved whole-DLQ recovery through AWS's native message-move
task. Resolve ARNs from the existing queue URLs, explicitly target the configured
command queue, and leave transfer rate selection to AWS. Return 202 after confirmed
creation, without reading command contents. The status endpoint combines approximate
SQS counts with the latest task's bounded status and progress projection; it does
not return dependency failure text. No extra application settings or recovery store
are introduced. Existing inspection holds must expire before whole-queue recovery.
Native movement leaves delivery evidence unchanged and normal consumption retains
all terminal suppression and identity checks.

### Retained evidence search

Add read-only authenticated delivery-record search independently of queue
inspection. Use a separate Mongo reader, preserving the delivery write-store
interface. Project only retained identity/state/timestamps and available Notify,
template correlation; exclude payloads, recipient/immutable digests and attempt
ownership. The explicit resend producer now supplies parent correlation. Search
projects that retained opaque relationship, provides exact parent filtering and navigation,
and binds it into cursors. No lineage backfill is introduced.

Recording time, exact filters and stable bounded cursors support operator
investigation without interpreting action time as processing time. Concurrent
updates do not provide a snapshot. The
[query](../../src/Consumer/Administration/DeliveryRecordSearchQuery.cs),
[reader](../../src/Consumer/Administration/MongoDeliveryRecordSearchReader.cs) and
[query-plan scenarios](../../tests/Consumer.IntegrationTests/Scenarios/DeliveryRecordSearchQueryPlanTests.cs)
own current filter, projection and cursor details.

Evidence retention is indefinite. Parent-only lookup, including sources without
replacements, must not walk every retained original. Optional migration 003 uses
a partial parent index so parentless originals need no entries; migration 002
supports ordinary search. Exact definitions and build/rollback checks live in
[migrations](../../src/Consumer/Data/Migrations). Neither migration gates critical
delivery readiness. Confirm their builds before relying on efficient search,
monitor resource use and investigate incompatible definitions. Builds can outlive
client timeout. No backfill is introduced. Representative plan tests are not a
universal performance guarantee for every distribution/filter combination.

### Inspection recovery guidance and correlation

Authenticated inspection exposes current evidence and only implemented recovery
actions. These are recommendations, not authorization for later effects; actions
revalidate selection and state. Pending evidence cannot distinguish rejection,
a lost response or persistence failure. Accepted evidence does not prove delivery.
Invalid/conflicting/unknown records require engineering escalation rather than an
unsafe removal shortcut. See
[recovery guidance](../../src/Consumer/Administration/CommandDlqRecoveryGuidance.cs)
and [HTTP operator instructions](../../runbooks/http/README.md).

### Malformed retained evidence

Do not fabricate missing dates or rewrite malformed history. Search excludes
unreadable mandatory evidence before paging and preserves unknown string outcomes;
inspection cannot offer usable recovery for unknown evidence. Empty search is not
proof that malformed evidence does not exist. Optional malformed values remain
absent. The reader and [search scenarios](../../tests/Consumer.IntegrationTests/Scenarios/DeliveryRecordSearchTests.cs)
provide exact type and cursor contracts; failures expose fixed safe diagnostics.

### Administration request-log privacy

Keep retained identity and Notify correlation confined to authenticated responses.
GET query values can otherwise appear in hosting diagnostics before authorization
and in ECS URL enrichment. Redact administration query strings at the logging
boundary, preserving lifecycle, path, method, status and correlation metadata.
The [request-log redactor](../../src/Consumer/Utils/Logging/AdminRequestLogRedactor.cs)
changes detached event metadata after enrichment, leaving request binding,
authorization, log levels and non-admin request logging unchanged.

## Consequences

Malformed commands and immutable conflicts cannot be discarded through these APIs.
Native whole-queue redrive can move them unchanged; normal consumption still rejects
invalid commands or conflicts.
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

### Stable accepted-command replacement

Ordinary terminal replay remains cleanup. Explicit resend derives B's raw key
from A's validated opaque key in the reserved `resend:` namespace, rather than a
request ID or transport message ID. This prevents fresh selections or other hosts
from creating multiple intentions from A after uncertain queue/HTTP responses.
B can be an immediate source later without rewriting A or bypassing terminal B.

Carry A's opaque key as optional command metadata and retain it through all
pending and terminal transitions. Compare it separately from the existing
immutable digest so original-command identities stay compatible. Contradictory
parent evidence refuses sending and history changes. Publish before deleting
the selected copy; do not authorise resend from retained history alone.

Selection format `v4` additionally signs the source key and parent, preventing a
same-content identity substitution. Old selections retain ordinary cleanup
compatibility but require fresh inspection for resend. All consumers must support
and preserve lineage before operator use; schema compatibility alone cannot
establish mixed-version readiness. Native accepted-source verification remains a
separate controlled-resource operation described in the recovery runbook.
