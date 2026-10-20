# Waste Obligations Notifications

A CDP service that consumes Waste Obligations analytics events and delivers email notification commands.

## Scope

Analytics produces notification commands for compliance-declaration submissions and
cancellations. Command delivery is a separate, source-independent consumer. It
uses Mongo evidence to suppress terminal duplicates and GOV.UK Notify to send
eligible commands. Notify acceptance is not proof of recipient delivery, and an
indeterminate request can be sent again after claim expiry.

Start with [service behaviour](docs/service-behaviour.md) for the implementation
entry points and operational boundaries. External publishers must follow the
[notification-command producer contract](docs/notification-command-producer-contract.md).

## Run locally

Prerequisites: .NET 10 SDK and Docker or a compatible container runtime.

```bash
docker compose up --build
```

The health endpoint is `http://localhost:8085/health`. Compose owns isolated
SNS/SQS queues, MongoDB and a controlled Notify HTTP fixture on port `8086`.
It does not contact GOV.UK Notify. The fixture generates synthetic credentials
in an ephemeral volume; teardown removes them. Analytics is disabled because
Compose has no business declaration API. Integration scenarios start controlled
enrichment endpoints and isolated hosts instead.

Use [Contributing](CONTRIBUTING.md#required-checks) for formatting, build, tests and
mandatory Compose teardown. Use the [HTTP requests](runbooks/http/README.md) for
health and authenticated command-DLQ operations.

## Configuration

Use double underscores for environment-variable nesting. Checked-in
[production settings](src/Consumer/appsettings.json),
[development settings](src/Consumer/appsettings.Development.json) and the linked
options/registrations define supported settings, validation and defaults. Local
Compose values do not configure deployed CDP environments.

### Analytics and enrichment

`AnalyticsEventConsumer__ProcessingEnabled` is disabled by default. Supply its
`QueueUrl` using a service-owned subscription to `waste_obligations_analytics_events`,
separate from the producer queue, with its own CDP redrive policy.

Before deploying this image to any environment where analytics is enabled, provide:

| Boundary | Deployment settings |
| --- | --- |
| Waste Obligations Basic Read | `WasteObligations__BaseAddress`, `Username`, `Password` |
| Waste Organisations Basic | `WasteOrganisations__BaseAddress`, `ClientId`, `ClientSecret` |
| Account OAuth client credentials | `AccountBackend__BaseAddress`, `TokenEndpoint`, `ClientId`, `ClientSecret`, optional `Scope` |

Each suffix in the table uses its boundary's prefix. Alternatively, disable
analytics before deployment. Missing enabled-analytics access configuration
rejects startup for the whole host, including delivery and administration.
Addresses require HTTPS except loopback development endpoints. Background
analytics has no inbound headers to propagate. Account/OAuth follow the companion
service's `HTTP_PROXY` convention with local bypass.

Resilience settings are bound under `WasteObligations`, `WasteOrganisations`,
`AccountBackend` and `AccountOAuth`. See their
[clients](src/Consumer/Services) and
[shared timeout/retry options](src/Consumer/Services/Http/DownstreamOptions.cs).
The outer [analytics budget](src/Consumer/Consumers/AnalyticsEventConsumerOptions.cs)
remains authoritative. Measure peak/retry enrichment and publication latency and
check actual queue visibility/redrive settings before activation; defaults are
initial estimates, not a deployed latency guarantee. Larger receive batches
require a reviewed processing/visibility budget.

Notification template configuration lives under `Notifications` with
named `DeclarationSubmitted` and `DeclarationCancelled` definitions. See
[definition options](src/Consumer/Notifications/NotificationDefinitionsOptions.cs)
and checked-in settings for variant names and template defaults. Move existing
`Submission__<variant>` and `Cancellation__<variant>` overrides to
`Notifications__Definitions__DeclarationSubmitted__Templates__<variant>` and
`Notifications__Definitions__DeclarationCancelled__Templates__<variant>` before
rollout; default template IDs are unchanged. Finalize effective
overrides and validate Notify account/template access before the dry run. A later
template change for a replayable intention can cause an immutable-command conflict.

Extended health authenticates downstream endpoints but does not prove deployed
Read privileges or the declaration version/identity contract. Validate those
separately before activation; see [health registrations](src/Consumer/Utils/Health/ServiceCollectionExtensions.cs).

### Command delivery and administration

Every host consumes commands after startup readiness; there is no command-only
pause or administration enablement switch. Provide the command FIFO URL,
distinct evidence/lane secrets, optional UTC cutover, Mongo access and Notify
credentials even with a null cutover. Use
[delivery options](src/Consumer/Delivery/NotificationCommandDeliveryOptions.cs) and
[registration](src/Consumer/Delivery/ServiceCollectionExtensions.cs) for exact
`NotificationCommandDelivery__` setting names and budget validation.
`Notify__ApiKey` supplies the service key and `Notify__BaseAddress` selects the API.
Verify effective deployed settings and access before merging when
[CDP auto-deploy](https://github.com/DEFRA/cdp-documentation/blob/main/how-to/auto-deployment.md)
is enabled.

Administration needs a distinct FIFO DLQ `CommandDlqAdministration__QueueUrl` and
its [selection/timeout settings](src/Consumer/Administration/CommandDlqAdministrationOptions.cs).
Configure Basic clients with `Acl__Clients__<clientId>__Type=ApiKey`, `Secret` and
`Scopes__0=admin`; OAuth clients use `Type=OAuth` and `Scopes__0=admin` without a
Basic secret. Empty ACLs deny administrator access. Supply secrets privately.

Bearer authentication uses the approved gateway-only trust contract: the CDP
gateway must validate signature, issuer and audience for every administrator
route. The service checks lifetime and configured client identity, not signatures.
Direct backend callers can assert an OAuth admin identity; client IDs are not
secrets. Deployment owners must restrict direct backend access. Private DNS alone
does not establish gateway authentication. See [ADR 0004](docs/adr/0004-command-dlq-inspection.md).

Whole-queue recovery requires `sqs:StartMessageMoveTask`, `sqs:ReceiveMessage`,
`sqs:DeleteMessage` and `sqs:GetQueueAttributes` on the DLQ, plus `sqs:SendMessage`
and `sqs:GetQueueAttributes` on the command queue. Status also requires
`sqs:ListMessageMoveTasks`. SSE-KMS needs applicable `kms:Decrypt` and
`kms:GenerateDataKey` permissions. Let inspection visibility expire before
whole-queue redrive. Native replay/recovery proof remains a separate
[operator verification](docs/command-dlq-native-sqs-verification.md); emulator tests
cannot supply it or authorize testing shared resources.

### Mongo, metrics and health

Configure `Mongo__DatabaseUri` and `Mongo__DatabaseName`; the notifications database
must remain separate from Waste Obligations. These replace the earlier
`NotificationCommandDelivery__MongoConnectionString`/`MongoDatabaseName` settings.
For CDP, use `authSource=$external`, `authMechanism=MONGODB-AWS` and platform TLS/
topology options, with task-role permissions for this database, migrations, leases
and index creation. Do not embed credentials in the URI. `TRUSTSTORE_` certificate
variables load before clients are created. Local standalone Mongo does not verify
CDP IAM/TLS: check those, permissions and migration completion before rollout.

[Migration options](src/Consumer/Data/MongoMigrationOptions.cs) define
`MongoMigrations__` timeouts and attempts. Critical completion gates `/health`;
optional search indexes do not. Confirm migrations 002/003 and their builds before
relying on efficient search/replacement navigation. Index construction consumes
resources and can continue after client timeout. Long critical migrations must
fit the platform startup budget or be applied before rollout. CDP owns replacement
of hosts that remain unhealthy. See [ADR 0003](docs/adr/0003-mongo-migrations.md).

Deploy the consumer and administration endpoints in the same development
changeset. No staged rollout or resend feature flag is required; mixed-version
operation is not supported.

Configure trusted, non-PII diagnostic categories through
`NotificationCommandDelivery__DiagnosticNotificationTypes` (array suffixes `__0`,
`__1`, etc.). Preserve configured declaration categories when overriding the list.
Unknown types use `other`; logs and metrics never substitute for delivery evidence.

EMF uses `AWS_EMF_ENABLED`, `AWS_EMF_NAMESPACE`, `AWS_EMF_ENVIRONMENT`,
`AWS_EMF_AGENT_ENDPOINT`, `AWS_EMF_AGENT_BUFFER_SIZE`, `AWS_EMF_SERVICE_NAME`,
`AWS_EMF_SERVICE_TYPE`, `AWS_EMF_LOG_GROUP_NAME` and `AWS_EMF_LOG_STREAM_NAME`.
See [options](src/Consumer/Utils/Metrics/EmfOptions.cs) and
[exporter](src/Consumer/Utils/Metrics/MetricsExporter.cs) for validation and SDK
integration. Set actual CDP collector routing explicitly: the pinned SDK's
[ECS endpoint derivation](https://github.com/awslabs/aws-embedded-metrics-dotnet/blob/v2.2.0/src/Amazon.CloudWatch.EMF/Environment/ECSEnvironment.cs)
is malformed. `AWS_EMF_ENVIRONMENT=Agent` avoids metadata discovery when routing
is known. Export is best effort; pending metrics may be lost at process exit.
Any CDP override of `Microsoft.AspNetCore.Hosting.Diagnostics` must allow
Information level for the retained health-request completion event.

## Digest-key lifetime

`EvidenceDigestSecret` is durable identity material. Preserve it across hosts and
deployments while retained evidence, signed selections or replayable commands
exist. Replacing it can bypass duplicate detection and invalidate selections,
Notify references and recovery identities. The digest version is not a key ID;
rotation requires a separately designed identity-preserving migration.

Every publisher/consumer must share `RecipientLaneSecret`. Changing it while
commands remain queued or dead-lettered can break recipient ordering. Keep both
values in deployment-owned secret storage; no lane-key rotation is supplied.

## Nullable initial cutover and handover

Null `NotificationCommandDelivery__EmailDeliveryCutoverUtc` means permanent
suppression, not a paused backlog. Suppressed evidence remains terminal after
configuration changes. Run the producer dry run with null while Waste Obligations
retains direct sending. Before activation validate deployed DTO/Read ACL, all
organisation/Account access, effective template rendering, measured analytics
headroom, queue policies and identity-secret provisioning. Both producers must
be deployed and verified.

Configure Notifications with a future X first. Verify every active host uses X
and the post-cutover implementation; stop old null-cutover consumers. Then
configure Waste Obligations with identical X and complete its rollout before X.
Otherwise both paths can permanently suppress affected actions. After X, use
Notifications recovery; never clear/change the boundary to force sending. Compare
`EmailDeliveryCutover` in `/health/all` on every active host, not saved settings
alone. Follow [ADR 0002](docs/adr/0002-email-delivery-cutover-boundary.md).

## Email verification and local Notify credentials

[Submission](tests/Consumer.IntegrationTests/Scenarios/SubmittedEmailSendTests.cs),
[cancellation](tests/Consumer.IntegrationTests/Scenarios/CancellationEmailSendTests.cs)
and [container wiring](tests/Consumer.IntegrationTests/Scenarios/DockerSubmittedEmailTests.cs)
scenarios use isolated SQS/Mongo and controlled HTTP boundaries. Their assertions
are the source of truth for full-path coverage. They do not prove deployed access,
provider rendering or native AWS receive-attempt replay.

Provider previews render synthetic inputs without sending mail. CI requires a
Notify **test-type** `GOVUKNOTIFY_APIKEY` in both Actions and Dependabot secret
stores, never a live sending key: PR-built code/dependencies can read that value.
Missing keys or provider errors fail the required gate; there is no bot exemption.
Image/security checks run before previews. The
[preview guard](scripts/require-notify-previews.py) defines required cases and the
[workflow](.github/workflows/check-pull-request.yml) defines report handling.
Keep isolated integration and provider-preview results separate; counts come
from TRX evidence and repeated preview identities are counted once.

For local previews, privately replace the placeholder in the ignored root `.env`
(also excluded from Docker inputs):

```dotenv
GovukNotify_ApiKey=replace-with-your-private-notify-key
```

```bash
dotnet test --test-modules tests/Consumer.IntegrationTests/bin/Debug/net10.0/Consumer.IntegrationTests.dll --no-build -- --filter-class '*PreviewTests'
```

Direct preview execution falls back to `.env` when `GOVUKNOTIFY_APIKEY` is absent.
Without a key local previews explicitly skip; `NOTIFY_PREVIEWS_REQUIRED=1` requires
execution. Credentials, rendered content and provider exception text must not
appear in reports.

For a local consumer using real Notify, the wrapper provides the same private
fallback independently to `GOVUKNOTIFY_APIKEY` and `Notify__ApiKey`; explicit
process values, including empty values and `NOTIFY__APIKEY`, take precedence.
It never executes `.env` content or prints the key:

```bash
Notify__BaseAddress=https://api.notifications.service.gov.uk \
  python3 scripts/with-notify-env.py run
```

Set the base address explicitly because Development otherwise selects the isolated
fixture. This is real Notify runtime configuration, separate from normal Compose.
All Read access, template and cutover prerequisites still apply.

## Documentation

- [Coding standards](CODING_STANDARDS.md), [Contributing](CONTRIBUTING.md) and [Agent guidelines](AGENTS.md).
- [Terminology](CONTEXT.md) and [service behaviour](docs/service-behaviour.md).
- Accepted [command architecture](docs/adr/0001-notification-command-delivery-architecture.md), [cutover](docs/adr/0002-email-delivery-cutover-boundary.md), [migrations](docs/adr/0003-mongo-migrations.md) and [administrator recovery](docs/adr/0004-command-dlq-inspection.md) decisions.
