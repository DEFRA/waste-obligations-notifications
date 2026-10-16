# HTTP requests

Use Rider's HTTP Client or the
[httpYac extension for VS Code](https://marketplace.visualstudio.com/items?itemName=anweber.vscode-httpyac)
on Windows, macOS or Linux. These files stay outside the .NET solution.

For local Compose, run `docker compose up --build -d --wait` and select `docker`.
It uses `http://localhost:8085` and the local Basic-auth client; no private settings
or OAuth token endpoint are needed.

For deployed environments:

1. Copy `http-client.private.env.json.example` to `http-client.private.env.json`
   beside the requests. Git ignores the copy.
2. Fill in the host, token URL, client ID and client secret for your environment.
3. Open a `.http` file, select the environment and run an individual request.
   Requests acquire an OAuth token automatically.

- [Health](health.http): anonymous readiness and extended checks, plus authenticated readiness.
- [Command DLQ](command-dlq.http): run readiness, then inspect before redrive, discard or accepted-command resend.

Retained evidence can be searched independently of DLQ inspection through
`GET /admin/notification-commands/delivery-records`. Use explicit UTC recording-time
bounds: `recordedFromUtc` includes its boundary and `recordedToUtc` excludes it.
Combine exact Notify ID, opaque notification key and outcome filters.
All recorded outcomes are included by default; `delivery-pending` is retained
state, not a claim-expiry classification. `pageSize` defaults to 25 and is limited
to 1–100. Copy `nextCursor` into the next request with the same filters. Results
are newest first; concurrent updates do not provide a historical snapshot.

Use exact `resendOfNotificationKey` filtering to find replacements of an opaque source
key, or follow inspection's `links.resends`. Confirm optional migration 003 has
completed before relying on indexed replacement navigation.
Search shows no payload, attempt history or live Notify status;
check Notify separately using retained identifiers. Recorded acceptance does not
confirm recipient delivery. Recovery endpoints include selected redrive, discard
and accepted-command resend,
plus whole-queue redrive. Search records cannot authorise selected actions; resend
requires one current identity-bound selection from DLQ inspection. See the
[existing recovery contract](../../docs/service-behaviour.md#command-dlq-inspection).

Inspection now provides `operatorGuidance`, `availableActions` with HTTP method,
endpoint and purpose, matching `deliveryRecord` evidence and available Notify
correlation. Follow encoded `links.deliveryRecord` to retained lookup. Actions are
classification-specific recommendations. Redrive validates the selection and
command content; discard additionally checks current delivery evidence.

- Accepted: check Notify progress. Wait while processing. Recorded
  acceptance is not recipient delivery; even a later delivery failure leaves the
  record accepted. Selected redrive only cleans up the terminal queue copy.
  Inspect existing replacements and investigate before requesting resend. Use the
  accepted message's current `selectionToken` with the resend request; the first
  inspected message is not necessarily eligible.
- Active claim: wait and inspect again. A lease does not prove its process is
  running. Selected redrive is not offered while active; discard refuses active
  claims.
- Expired claim: investigate with the original Notify reference. For confirmed
  rejection, fix the cause (including credentials if applicable) before redrive.
  For a lost response, check Notify; discard if delivered to prevent further
  attempts, or wait if processing. Inconclusive lookup retains duplicate risk;
  Notify absence is not proof that no request was accepted.
- Unrecorded: fix the processing failure before redrive, or discard if further
  attempts are no longer required.
- Suppressed: confirm handover policy; the legacy sender may have sent. Redrive
  only cleans up the terminal copy and cannot undo suppression.
- Abandoned: confirm the previous decision; discard performs idempotent cleanup.
  Abandonment does not prove an earlier uncertain request failed.
- Invalid/unsupported, conflicting or unknown state: escalate to engineering.
  No selected recovery or removal operation is offered. Invalid entries expose
  no partially trusted identity, navigation or selected action.

These are current-state instructions. Pending evidence alone cannot establish
whether Notify rejected a request, its response was lost, the process crashed
before sending, or acceptance persistence failed. Check diagnostics and Notify
before choosing recovery.

Accepted-command resend publishes one stable replacement and confirms publication
before deleting the selected source copy. Retries derive the same replacement;
ordinary redrive continues to suppress duplicate delivery. Check `availableActions`
before selecting resend. The response includes source and replacement opaque keys
and record links; a replacement record may not exist until consumption. Confirmed
publication and source removal do not prove Notify acceptance or recipient delivery.
Deploy consumers that preserve parent metadata on every host before using resend.
History-only resend, batches and suppression overrides are unsupported. Follow the
[accepted-command resend contract](../../docs/service-behaviour.md#accepted-command-resend)
and [native AWS verification steps](../../docs/command-dlq-native-sqs-verification.md#accepted-command-resend) before
using this in an environment.
