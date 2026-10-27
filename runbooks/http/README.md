# HTTP requests

Use Rider's HTTP Client or the
[httpYac extension for VS Code](https://marketplace.visualstudio.com/items?itemName=anweber.vscode-httpyac)
on Windows, macOS or Linux. These files stay outside the .NET solution.

For local Compose, run `docker compose up --build -d --wait` and select `docker`
with [local health](health.docker.http) or [local command DLQ](command-dlq.docker.http).
It uses `http://localhost:8085` and the local Basic-auth client; no private settings
or OAuth token endpoint are needed.

For deployed environments:

1. Copy `http-client.private.env.json.example` to `http-client.private.env.json`
   beside the requests. Git ignores the copy.
2. Fill in the host, token URL, client ID and client secret for your environment.
3. Open `health.http` or `command-dlq.http`, select the environment and run an individual request.
   Requests acquire an OAuth token automatically. The token expression belongs directly
   in each Authorization header; Rider does not evaluate it inside an environment value.
   All deployed health requests carry the token to pass the private gateway’s Cognito
   authentication, including endpoints that are anonymous within the service.

- [Health](health.http): readiness and extended checks through the gateway. The
  `/health/authorized` example is diagnostic: Bearer passes Cognito but the service
  requires Basic and returns 401. Use `/health` for deployed readiness and
  [local health](health.docker.http) for the Basic-auth readiness request.
- [Command DLQ](command-dlq.http): run readiness, then inspect before redrive, discard or accepted-command resend.
- [Not submitted reminder](not-submitted-reminder.http): request a background reminder scan
  within the send window. Repeating it is safe because commands are idempotent.

Executable response examples are recorded in the Verify `*.verified.json` snapshots in
[`tests/Consumer.Tests/Administration`](../../tests/Consumer.Tests/Administration).

## Investigation and recovery

Use the requests in [command-dlq.http](command-dlq.http) for current endpoint
parameters and authentication. The
[query model](../../src/Consumer/Administration/DeliveryRecordSearchQuery.cs) and
[response snapshots](../../tests/Consumer.Tests/Administration) define filters,
cursors and response shapes. Use explicit UTC recording-time bounds, retain
filters when following `nextCursor`, and check Notify separately: retained
acceptance is not recipient delivery and concurrent searches are not snapshots.
Confirm optional migration 003 before relying on efficient replacement navigation.

1. Run readiness, then inspect the DLQ. Inspection changes visibility/receive
   count, and the first returned message may not be the one you intend to recover.
2. Check the chosen message's `operatorGuidance`, `availableActions`, matching
   evidence and Notify reference/ID. Follow its record/replacement links to
   investigate prior outcomes. The
   [guidance implementation](../../src/Consumer/Administration/CommandDlqRecoveryGuidance.cs)
   owns classification-specific choices; the inspection snapshot is not authority
   for later effects, which revalidate current state.
3. Investigate failures before selecting recovery. Pending evidence cannot tell
   whether Notify rejected a request, its response was lost, the process crashed
   or acceptance persistence failed. Notify absence is not proof of nonacceptance.
4. Use the chosen message's current selection token. If it expires, wait for
   visibility and reinspect; receive replay can keep messages hidden beyond token
   expiry. Let inspection visibility expire before whole-queue recovery.
5. After uncertain responses, inspect current evidence and Notify before retrying.
   Selected redrive confirms publication before removal and ordinary terminal
   replay cleans up without a new send. Discard prevents future attempts but
   cannot prove an earlier uncertain request failed. Never override suppression,
   abandonment or cutover to force delivery.

Explicit accepted-command resend requires a current identity-bound selection on
the DLQ; retained history alone cannot authorize it. Check Notify and existing
replacements first. Retries derive the same replacement, but its record may not
exist immediately after publication. Source removal and publication do not prove
Notify acceptance or recipient delivery. See the [resend implementation](../../src/Consumer/Administration/CommandDlqResender.cs)
and [native accepted-source verification](../../docs/command-dlq-native-sqs-verification.md#accepted-command-resend).

Deploy the consumer and administration endpoints in the same development
changeset. No staged activation or resend feature flag is required; mixed-version
operation is not supported. History-only resend and batches are unsupported.

Invalid/unsupported, conflicting or unknown states require engineering escalation;
no selected removal operation is offered. Do not act on partially trusted fields.
Use configured retention or separately controlled removal for unmanageable items.
Native AWS proof remains separate from isolated emulator/adaptor checks; follow
[the native runbook](../../docs/command-dlq-native-sqs-verification.md) without
changing shared queues, deployed settings or real evidence from tests.
