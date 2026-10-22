# Service behaviour

This guide identifies service boundaries and the executable sources of current
behaviour. Code and tests own implemented decisions, mappings, validation and
response shapes; ADRs retain why those decisions were made. Use
[CONTEXT.md](../CONTEXT.md) for terminology and the
[README](../README.md#configuration) for deployment settings.

## Current scope

Analytics produces commands for compliance-declaration submission and cancellation;
other valid analytics events retain their existing acknowledgement behaviour.
Analytics does not send email or persist event data. Source-independent command
delivery owns cutover, durable evidence and Notify requests. Authenticated
administration supports inspection and recovery without changing business data.

## Consumers and message handling

| Concern | Implementation and executable examples |
| --- | --- |
| Analytics parsing and scope | [AnalyticsEvent](../src/Consumer/Consumers/AnalyticsEvent.cs), [consumer](../src/Consumer/Consumers/AnalyticsEventConsumer.cs), [scope tests](../tests/Consumer.Tests/Cancellation/AnalyticsEventCancellationScopeTests.cs) |
| Submitted intention and original audit | [SubmittedEmailProducer](../src/Consumer/Submission/SubmittedEmailProducer.cs), [hosted scenarios](../tests/Consumer.IntegrationTests/Scenarios/SubmittedEmailSendTests.cs) |
| Notification definitions and routing | [definition options](../src/Consumer/Notifications/NotificationDefinitionsOptions.cs), [registration](../src/Consumer/Notifications/ServiceCollectionExtensions.cs) |
| Declaration reads and deleted-record fallback | [Waste Obligations client](../src/Consumer/Services/WasteObligations), [shared reader and validation](../src/Consumer/Submission/SubmittedEmailProducer.cs), [submission regressions](../tests/Consumer.Tests/Consumers/SubmittedEmailConsumerTests.cs), [cancellation and replay scenarios](../tests/Consumer.IntegrationTests/Scenarios/CancellationEmailSendTests.cs) |
| Cancellation organisation and Account enrichment | [organisation client](../src/Consumer/Services/WasteOrganisations), [Account client](../src/Consumer/Services/AccountBackend), [cancellation producer](../src/Consumer/Cancellation/CancellationEmailProducer.cs) |
| Analytics receive/enrichment/publication budget | [options](../src/Consumer/Consumers/AnalyticsEventConsumerOptions.cs), [consumer](../src/Consumer/Consumers/AnalyticsEventConsumer.cs) |

Required publication must be confirmed before acknowledging its source. A valid
no-email outcome can acknowledge without publication; validation/dependency or
unconfirmed-publication failures remain retryable. Uncertain acknowledgement can
replay confirmed work. Neither publication nor Notify acceptance proves recipient
delivery. Consumer budgets are separate; a delivery lease does not cover analytics
enrichment. Larger receive batches require a reviewed visibility budget.

## Cancellation production

The [cancellation producer](../src/Consumer/Cancellation/CancellationEmailProducer.cs)
owns transition eligibility, audit selection, Account resolution, recipient
filtering and immutable identities. [Presentation](../src/Consumer/Cancellation/CancellationPresentation.cs)
owns bilingual wording and personalisation. Review
[producer tests](../tests/Consumer.Tests/Cancellation/CancellationEmailProducerTests.cs),
[presentation tests](../tests/Consumer.Tests/Cancellation/CancellationPresentationTests.cs)
and [hosted scenarios](../tests/Consumer.IntegrationTests/Scenarios/CancellationEmailSendTests.cs)
for exact cases rather than maintaining a second mapping table here.

Actual cancellation intentions are preceded by confirmed publication of the
original Submitted intention on every attempt. Empty results publish neither.
Unrecognised cancellation reasons leave the analytics message for retry and dead-lettering.
No eligible recipient or unresolved compliance scheme remains an acknowledged no-send
outcome, logged with a fixed reason code and no recipient data.
Retries re-evaluate live contacts; terminal evidence and immutable conflicts remain
the replay boundary. No recipient snapshot or producer ledger is introduced.
Publication precedence does not establish inbox order: recipient lanes, recovery
and resend can change later delivery order.

## Notification commands and data protection

External publishers must follow the [producer contract](notification-command-producer-contract.md).
For internal behaviour follow:

- [Command publisher and parser](../src/Consumer/Commands).
- [Delivery consumer](../src/Consumer/Delivery/NotificationCommandConsumer.cs), [store](../src/Consumer/Delivery/MongoNotificationDeliveryRecordStore.cs) and [digest](../src/Consumer/Delivery/NotificationCommandDigest.cs).
- [Notify boundary](../src/Consumer/Delivery/NotifyEmailClient.cs) and [SDK transport](../src/Consumer/Delivery/NotifySdkHttpClient.cs).
- [Delivery regression scenarios](../tests/Consumer.IntegrationTests/Scenarios/NotificationCommandDeliveryTests.cs), [send scenarios](../tests/Consumer.IntegrationTests/Scenarios/NotificationCommandSendTests.cs) and [null cutover scenarios](../tests/Consumer.IntegrationTests/Scenarios/NullCutoverDeliveryTests.cs).

Retain minimal evidence indefinitely, not recipients, personalisation, rendered
content or full provider responses. Protect identity secrets for that lifetime.
Terminal outcomes cannot be reset to force a send. A failed or indeterminate
Notify request can be retried after claim expiry and produce another email;
there is no exactly-once guarantee or Notify-reference reconciliation. See
[architecture rationale](adr/0001-notification-command-delivery-architecture.md),
[cutover rationale](adr/0002-email-delivery-cutover-boundary.md) and
[coding privacy requirements](../CODING_STANDARDS.md#validation-configuration-and-security).

## Command-DLQ verification command

The [verification-command creator](../src/Consumer/Administration/CommandDlqVerificationCommandCreator.cs)
provides a synthetic permanently suppressed probe for operator recovery checks.
It confirms queuing, not processing, and cannot prove successful discard.
Use the [native runbook](command-dlq-native-sqs-verification.md); it does not
authorize agents to touch shared queues or provision cloud resources.

## Command-DLQ status

[Queue operations](../src/Consumer/Administration/CommandDlqQueueOperations.cs)
provide approximate counts and native redrive-task status without receiving
messages. Counts are eventually consistent. Inspecting messages changes visibility
and receive count, so it can affect the observed counts.

## Whole command-DLQ redrive

[Queue operations](../src/Consumer/Administration/CommandDlqQueueOperations.cs)
start native AWS movement to the configured command queue. Task creation does
not prove completion or delivery. Let inspection visibility expire first and
check status before retrying uncertain task creation. Recovery retains ordinary
cutover, conflict, claim and terminal suppression rules.

## Command-DLQ inspection

Use [HTTP requests and operator guidance](../runbooks/http/README.md) for endpoint
methods, authentication, selection and recovery. Current responses/actions are
specified by [administration endpoints](../src/Consumer/Administration),
[recovery guidance](../src/Consumer/Administration/CommandDlqRecoveryGuidance.cs)
and [response snapshots](../tests/Consumer.Tests/Administration).

Administration is always registered and uses the Admin ACL plus startup readiness.
The accepted [gateway-only Bearer trust contract](adr/0004-command-dlq-inspection.md)
requires gateway validation and restricted direct backend access. Empty ACLs deny
access. Email inputs are allowed only in authenticated inspection responses,
never logs, metrics, selection tokens or Mongo evidence.

Selections expire; replay can keep messages invisible beyond selection expiry.
Wait for visibility and reinspect when necessary. Selected redrive preserves
command content and confirms publication before removal; discard confirms
abandonment before removal. Neither can undo terminal suppression or acceptance.
Malformed commands/conflicts require engineering escalation and configured queue
retention or controlled removal; no verified removal tool is supplied here.

Local tests label their receive-attempt replay adapter because Floci lacks native
replay. Real isolated FIFO/Mongo tests validate wiring and durable guards, not
native AWS replay. Follow the [native verification runbook](command-dlq-native-sqs-verification.md)
for that separate evidence requirement.

## Email diagnostics

[Analytics diagnostics](../src/Consumer/Consumers/AnalyticsEventConsumer.cs),
[delivery metrics](../src/Consumer/Delivery/NotificationCommandMetrics.cs) and
[metric exporter](../src/Consumer/Utils/Metrics/MetricsExporter.cs) own current
fixed reason/category names and instrument behaviour. Configure only trusted,
non-PII category labels. An acceptance metric is not delivery evidence; an
unconfirmed attempt is not proof of rejection. Do not reconstruct historical
failure causes from current evidence. [Administration request-log redaction](../src/Consumer/Utils/Logging/AdminRequestLogRedactor.cs)
keeps query values out of diagnostic metadata without changing request binding.

Action-to-acceptance latency uses the command's original action timestamp and the
service clock when Notify acceptance is confirmed. It includes upstream delays
and retries, and remains a sample even if storing acceptance subsequently fails.
It measures acceptance, not inbox delivery; clocks across services must be aligned.
See the [consumer](../src/Consumer/Delivery/NotificationCommandConsumer.cs) and
[latency tests](../tests/Consumer.Tests/Delivery/NotificationCommandConsumerTests.cs)
for sampling boundaries.

Action-to-outcome latency also covers recorded suppression, including when cutover
is unset. Its outcome dimension separates suppression from confirmed Notify
acceptance; acceptance uses the same endpoint as the acceptance-only metric.
Suppression remains terminal: enabling sending later does not extend its sample.

[Journey metrics](../src/Consumer/Utils/Metrics/NotificationJourneyMetrics.cs)
connect analytics processing with the shared command delivery path. Analytics
outcomes require confirmed source acknowledgement; publication counts include
confirmed partial publications and replays. Counts describe attempts, not a
conversion funnel: one event can produce zero or many commands.

Use safe EventId/EntityId log scopes and NotifyReference to investigate a case,
then consult retained delivery evidence. Successful polling shows consumer
activity, including empty receives; it does not prove dependency health.
Check queue age and dead-letter messages alongside processing failures. Empty
latency panels cannot establish service health.

## Deployment ownership

CDP owns queues/redrive, Mongo, credentials/ACLs, Notify access, metrics routing,
cutover and identity secrets. Compose owns isolated local resources only. The
[README configuration guide](../README.md#configuration) identifies required
settings and separate authentication, TLS/IAM and collector-routing checks.
Enabled analytics requires declaration, organisation and Account access before
rollout, even on hosts currently receiving only submission events. Otherwise
disable analytics before deployment; missing access can reject whole-host startup.

## Behaviour verification

Use [required local checks](../CONTRIBUTING.md#required-checks).
[Integration scenarios](../tests/Consumer.IntegrationTests/Scenarios) own full-path
expectations; [unit tests](../tests/Consumer.Tests) own detailed policy, validation
and privacy assertions. Provider previews require separate real Notify access
and guarded TRX evidence. Keep integration and preview results distinct, using
reported test identities rather than fixed summary counts. Preview success does
not prove deployed Read ACL, native replay, analytics headroom or cutover readiness.

## Initial deployment and cutover

Follow the [handover procedure](../README.md#nullable-initial-cutover-and-handover)
and [ADR 0002](adr/0002-email-delivery-cutover-boundary.md). Null cutover permanently
suppresses valid commands and cannot preserve a backlog. Finalize mappings before
the dry run and preserve them while those intentions can replay.

Activation requires deployed response/version contracts, read/health privileges,
effective template rendering, measured peak/retry latency within visibility,
redrive policies and stable secrets. Both producers must be ready. Configure
Notifications with future X first, verify all hosts and stop old null consumers,
then complete Waste Obligations rollout with identical X before the boundary.
After X recovery is forward-only; do not change the cutover to force a resend.

Critical migration completion gates `/health`; consumers/admin wait for its first
successful anonymous response. Extended diagnostics do not release that startup
boundary. Long critical migrations must fit actual platform startup budgets or
be applied before rollout; CDP owns replacement of unhealthy hosts. Readiness
does not prove optional search-index completion or future dependency availability.

## Retained delivery-record search

[Search query](../src/Consumer/Administration/DeliveryRecordSearchQuery.cs),
[Mongo reader](../src/Consumer/Administration/MongoDeliveryRecordSearchReader.cs)
and [search scenarios](../tests/Consumer.IntegrationTests/Scenarios/DeliveryRecordSearchTests.cs)
define filters, cursor semantics and summaries. Use the [HTTP runbook](../runbooks/http/README.md)
for requests. Search is authenticated, read-only and independent of queue copies;
it does not authorize recovery or report live Notify delivery. Recording time
is different from business-action time. Concurrent updates do not provide a
snapshot, and empty results cannot prove absence of malformed evidence.

Optional [search migrations](../src/Consumer/Data/Migrations) must complete before
relying on efficient navigation, especially parent-only replacement lookups over
indefinitely retained evidence. Builds consume resources and can outlive a client
timeout. Investigate failures; do not replace incompatible definitions or claim
universal query-performance guarantees from representative plan tests.

## Accepted-command resend

The [resender](../src/Consumer/Administration/CommandDlqResender.cs) and
[resend tests](../tests/Consumer.Tests/Administration/CommandDlqResendTests.cs)
define accepted-source eligibility and stable replacement identity. Resend
requires a current identity-bound DLQ selection, not retained history alone.
Ordinary redrive remains terminal cleanup. Check Notify and existing replacements
before requesting another intended email; uncertain Notify outcomes retain the
duplicate risk. Publication/removal does not prove acceptance or immediate
replacement-record availability.

Deploy the consumer and administration endpoints in the same development
changeset. No staged rollout or resend feature flag is required; mixed-version
operation is not supported. Preserve existing startup/cutover constraints. The
[producer contract](notification-command-producer-contract.md#explicit-replacement-metadata)
and separate [native accepted-source verification](command-dlq-native-sqs-verification.md#accepted-command-resend)
remain applicable. Never reset evidence or override cutover/suppression to resend.
