# Native SQS command-DLQ verification

Administration endpoints are always registered and protected by the Basic/OAuth admin ACL. Native FIFO replay remains unverified. Floci/LocalStack checks supply a labelled test adapter and cannot supply native proof. There is no administration enablement switch; an empty ACL permits startup and denies administrator calls.

## Synthetic verification through the deployed service

Use the [HTTP requests](../runbooks/http/command-dlq.http) with the
[private environment setup](../runbooks/http/README.md).

An authorised operator can use the always-registered body-free verification-command endpoint on the configured DLQ. It records permanent suppression before enqueueing; Notify cannot be called for the matching command. This is an operator action after deployment, not permission for an agent to call shared queues or provision cloud resources.

1. Confirm `/health` succeeds and the admin ACL grants access through the approved gateway/Basic contract. POST `/admin/notification-commands/dlq/verification-command` with no payload. Save its generated `idempotencyKey` and `messageId` privately. A 503 does not confirm queuing and may leave safe suppression evidence.
2. POST `/admin/notification-commands/dlq/inspect`. Locate the generated key in the returned messages before any action. Inspection selects up to ten visible messages in its `messages` array. If the probe is absent, leave the selected commands untouched and wait for selection/visibility expiry; this API cannot search for the probe by key.
3. Redrive the probe using its selection token, preferably through another service host within expiry. With differing host selection lifetimes, production still uses the token's original visibility parameter. Confirm a 204 redrive response, then source-queue processing/deletion, unchanged `delivery-suppressed` evidence and no Notify call for its reference. Logs should report terminal-duplicate processing for the recovered SQS message; redrive success alone is not proof that processing completed.
4. Record native region, revision, host/configuration differences, outcomes and safe evidence links. No token, receipt handle, credentials or message body belongs in logs/reports. The probe must be refused by discard because suppressed evidence is protected. Successful native discard and the explicit changed-visibility experiment below remain separate gaps.

Malformed commands and immutable conflicts cannot be cleared through these APIs.
Use configured queue retention or deployment-owned controlled removal for those
messages; this runbook supplies no verified removal tool. Null cutover permanently
suppresses valid commands while every host continues consuming. All hosts require
valid command/DLQ queues, digest secrets, Mongo and Notify settings and sending budgets.

## Full isolated recovery protocol

The following full recovery protocol requires isolated resources that are not currently available. Provisioning them is a separate operator decision; this implementation does not create cloud resources or test shared queues.

An operator must provision dedicated, disposable native AWS FIFO source and
destination queues, an isolated Mongo database and two verification service hosts.
Use synthetic commands and a controlled administrator ACL. Do not use shared
queues, real recipient addresses or deployed delivery evidence. Restrict gateway
and direct backend access before enabling this isolated verification surface.
This runbook does not authorize provisioning or changing deployed resources.

1. On AWS client host A, send a synthetic message and receive it with a fresh
   `ReceiveRequestAttemptId`, one message, zero wait, visibility 120 seconds,
   all message attributes and system attributes `ApproximateReceiveCount` and
   `SentTimestamp`. On client host B, repeat the same request within 120 seconds.
   Check equal message ID, body, attributes and receipt handle without logging
   the receipt. Verify the message remains hidden from a different attempt ID.
2. On a fresh message/attempt, repeat host A's 120-second receive on host B with
   `VisibilityTimeout=60`, changing no other parameter. Record whether native
   SQS returns the same message and receipt or rejects/invalidates the replay.
   Observe the reset visibility duration. Do not claim support from the emulator.
   Production uses stable signed timeout parameters; the changed-timeout result
   still needs recording to close the reviewer's API-contract question.
3. With host A's selection lifetime 120 seconds and host B's 60 seconds, inspect
   through A and redrive through B before expiry. Verify both native receives
   request visibility 120, use the same attempt ID and return the same receipt.
   Confirm exact body/encoding publication before selected deletion, unchanged
   unrelated source messages, canonical recipient lane and repeat deduplication.
4. Repeat inspection/discard across the hosts on fresh eligible evidence.
   Confirm durable abandonment before deletion, future duplicate suppression,
   and refusal to replace active, accepted, suppressed or conflicting history.
   Use only the isolated database; never test permanent discard on real evidence.
5. Repeat after signed expiry and after a selected message is deleted or its
   visibility is changed externally. Confirm no publication, abandonment or
   deletion of another message. Exercise deletion failure after a successful
   redrive and discard, then verify safe completion from the other host.

Store the date, native AWS region, service revision, host/configuration differences,
request parameters, pass/fail outcomes and safe evidence links. Do not retain
credentials, tokens, receipt handles or command content in reports. If native replay/recovery checks fail or cannot run, record the unresolved evidence gap.
Record the changed-timeout experiment separately from the stable production check.

Inspection returns up to ten next-visible messages. It cannot search by key or enumerate
the DLQ, and other invisible messages are unavailable until visibility expires.
Each replay resets visibility to the original signed timeout, potentially beyond
selection expiry. Wait for visibility and reinspect when a selection expires;
`v1` selections also require fresh inspection after deploying the current selection format.

AWS documents five-minute receive-attempt deduplication, equal messages/receipt
handles during visibility and reset visibility on replay, provided messages have
not been deleted or had visibility changed. It does not explicitly settle changes
to request timeout parameters; that is why the native experiment is required.
See [ReceiveMessage API](https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_ReceiveMessage.html)
and [receive-attempt guidance](https://docs.aws.amazon.com/AWSSimpleQueueService/latest/SQSDeveloperGuide/using-receiverequestattemptid-request-parameter.html).
