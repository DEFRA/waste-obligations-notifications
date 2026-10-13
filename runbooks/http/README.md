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
Combine exact Notify ID, opaque notification key, parent key and outcome filters.
All recorded outcomes are included by default; `delivery-pending` is retained
state, not a claim-expiry classification. `pageSize` defaults to 25 and is limited
to 1–100. Copy `nextCursor` into the next request with the same filters. Results
are newest first; concurrent updates do not provide a historical snapshot.

Follow a replacement's `links.sourceRecord` to its parent. An original record
has no parent. Empty replacement results are not evidence of failed publication
or queue absence. Search shows no payload, attempt history or live Notify status;
check Notify separately using retained identifiers. Recorded acceptance does not
confirm recipient delivery. Existing resolving endpoints remain selected redrive
and discard, plus whole-queue redrive. Explicit resend is unavailable, and search
records cannot authorise selected actions. See the
[existing recovery contract](../../docs/service-behaviour.md#command-dlq-inspection).
