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

- [Health](health.http)
- [Command DLQ](command-dlq.http): run readiness, then inspect before redrive or discard.

Retained evidence can be searched independently of DLQ inspection through
`GET /admin/notification-commands/delivery-records`. Use explicit UTC recording-time
bounds: `recordedFromUtc` includes its boundary and `recordedToUtc` excludes it.
Combine exact Notify ID, opaque notification key and outcome filters.
All recorded outcomes are included by default; `delivery-pending` is retained
state, not a claim-expiry classification. `pageSize` defaults to 25 and is limited
to 1–100. Copy `nextCursor` into the next request with the same filters. Results
are newest first; concurrent updates do not provide a historical snapshot.

Parent readback is deferred until a replacement-command producer exists.
Search shows no payload, attempt history or live Notify status;
check Notify separately using retained identifiers. Recorded acceptance does not
confirm recipient delivery. Existing resolving endpoints remain selected redrive
and discard, plus whole-queue redrive. Explicit resend is unavailable, and search
records cannot authorise selected actions. See the
[existing recovery contract](../../docs/service-behaviour.md#command-dlq-inspection).

Inspection now provides `operatorGuidance`, `availableActions` with HTTP method,
endpoint and purpose, matching `deliveryRecord` evidence and available Notify
correlation. Follow encoded `links.deliveryRecord` to retained lookup. Actions are
classification-specific recommendations. Redrive validates the selection and
command content; discard additionally checks current delivery evidence.

- Accepted: check Notify progress. Wait while processing. Recorded
  acceptance is not recipient delivery; even a later delivery failure leaves the
  record accepted. Selected redrive only cleans up the terminal queue copy.
  Explicit resend is unavailable.
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
before choosing recovery. No new resend or removal endpoint is implemented.
