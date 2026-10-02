# Native SQS command-DLQ verification

Keep `CommandDlqAdministration:Enabled` false on the service's queues until this
verification passes in CDP dev. Native FIFO receive replay has not been verified.
Floci/LocalStack checks supply a labelled test adapter and cannot satisfy this gate.

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
credentials, tokens, receipt handles or command content in reports. If required
production replay/recovery checks fail or cannot run, keep administration disabled.
Record the changed-timeout experiment separately from the stable production check.

Inspection returns only one next-visible message. It cannot search by key or list
the DLQ, and other invisible messages are unavailable until visibility expires.
Each replay resets visibility to the original signed timeout, potentially beyond
selection expiry. Wait for visibility and reinspect when a selection expires;
`v1` selections also require fresh inspection after deploying format `v2`.

AWS documents five-minute receive-attempt deduplication, equal messages/receipt
handles during visibility and reset visibility on replay, provided messages have
not been deleted or had visibility changed. It does not explicitly settle changes
to request timeout parameters; that is why the native experiment is required.
See [ReceiveMessage API](https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_ReceiveMessage.html)
and [receive-attempt guidance](https://docs.aws.amazon.com/AWSSimpleQueueService/latest/SQSDeveloperGuide/using-receiverequestattemptid-request-parameter.html).
