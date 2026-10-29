# Notification-command producer contract

The version-1 wire contract is defined by
[`NotificationCommand`](../src/Consumer/Commands/NotificationCommand.cs).
Producers implemented in this service should use
[`NotificationCommandPublisher`](../src/Consumer/Commands/NotificationCommandPublisher.cs).
There is no separately published contract package. An external publisher must
conform to the same wire and FIFO rules; packaging and producer policy belong
to its implementation ticket.

## Wire and identity

Publish UTF-8 JSON with these seven camel-case fields:

```json
{
  "schemaVersion": 1,
  "idempotencyKey": "example-submission-1",
  "actionOccurredAtUtc": "2027-01-01T00:00:00.123Z",
  "notificationType": "example-submitted",
  "emailAddress": "recipient@example.invalid",
  "templateId": "00000000-0000-0000-0000-000000000000",
  "personalisation": {}
}
```

This is synthetic shape data, not declaration recipient/template policy.
Strings must be nonblank and personalisation must be a JSON object. The key
must be 1–128 printable ASCII characters (`!` through `~`); do not trim or
rewrite it. Reuse the key and immutable field values for retries of the same
intended email. Deliberately new mail requires a new key. Preserve notification
type and template spelling; conflicting reuse is refused.

Action time must be explicit UTC (`Z`, `+00:00` or `-00:00`) and truncated to
whole milliseconds before serialization. Reject a value that truncates to the
default timestamp. For declarations, use the immutable `Submitted` or
`Cancelled` audit-entry timestamp as in Waste Obligations
[`EmailService`](https://github.com/DEFRA/waste-obligations/blob/3a40f0c0538aab906202edf14218766078545ad6/src/Api/Services/EmailService.cs).
Do not use processing time or mutable declaration timestamps. Recipient and
template/version selection remain MO-549/MO-550/MO-562 policy.

The publisher emits raw JSON. The reader also supports base64-encoded gzip
JSON when the SQS message attribute `Content-Encoding` has the exact string
value `gzip+base64`; other declared encodings are rejected.

## FIFO transport

Use the configured command FIFO queue. Set `MessageDeduplicationId` to the
unchanged idempotency key. Normalize the recipient with .NET `Trim()` followed
by `ToLowerInvariant()`, and serialize that normalized address.

Set `MessageGroupId` to `v1:` followed by lowercase hexadecimal HMAC-SHA256.
Use UTF-8 bytes of `RecipientLaneSecret` as the HMAC key and UTF-8 bytes of
`v1:recipient-lane:<normalized address>` as the input. This is the implementation
in [`NotificationCommandDigest`](../src/Consumer/Delivery/NotificationCommandDigest.cs).
Every publisher and recovery host must use the same lane secret; do not send
using a raw address or independently chosen group.

For the public example key `example-lane-key` and normalized recipient
`recipient@example.invalid`, the expected group is:

```text
v1:35b98f95f342a4648182f2df3b554c88913a5df0121920da7b1e93ce143499be
```

Use that vector when verifying an external implementation. It is not a deployed
secret. SQS's short deduplication window does not replace durable delivery
identity. See [digest-key lifetime](../README.md#digest-key-lifetime) and the
[cutover decision](adr/0002-email-delivery-cutover-boundary.md).

## Trace correlation

Publishers may supply the optional JSON field `correlationId`
with the originating trace ID. Consumers accept the same GUID or 32-character
hexadecimal format as analytics events and ignore missing or invalid correlation
metadata. Accepted IDs are lowercased for logging, storage and exact search. It supplies ECS `trace.id` for command outcome and failure logs and is
not part of delivery identity. A valid correlation ID is retained when a record
is created or a pending record is reclaimed or abandoned. Existing IDs are
preserved; missing or invalid IDs are not stored. Terminal duplicates do not
update the record, so the retained trace need not represent every replay.
Existing messages need no change. The analytics consumer sets this field from the originating event,
replacing any correlation supplied by its command builder, so all commands from
that event share its trace. See [validation](../src/Consumer/Utils/Logging/TraceCorrelation.cs),
[JSON parsing](../src/Consumer/Utils/Logging/TraceCorrelationJsonConverter.cs)
and [consumer correlation tests](../tests/Consumer.Tests/Delivery/NotificationCommandConsumerTests.cs).

## Explicit replacement metadata

Original commands omit `resendOfNotificationKey`. Explicit accepted-command
resend adds this optional field using the immediate source's opaque evidence key:
`v1:` followed by exactly 64 lowercase hexadecimal characters. Consumers validate
and retain it across pending, retry and terminal writes; conflicting parent
metadata is refused separately from the unchanged original immutable digest.

The administration producer reserves `resend:<source opaque notification key>`
as the raw replacement idempotency key. Other producers must not use this
namespace. Validation requires this exact key whenever a parent is supplied and
rejects a reserved key without its matching parent. It creates one stable intention per immediate source, independent of
transport copies or operator requests. It preserves recipient, template,
personalisation, type and business-action time, and uses the normal recipient
lane and command-key transport deduplication. Consumer and administration support
ship in the same development changeset; mixed-version operation is not supported.

## Source reference

Commands may include `source` with `type` and `id` strings identifying the originating business entity. Both values are required when supplied; type is at most 64 characters and ID at most 128, using ASCII letters, digits, hyphen, underscore or full stop. Producers must use non-sensitive identifiers, never email addresses or names. A valid source is retained with delivery evidence as diagnostic metadata and is not part of the immutable command fields. Publishers must validate it; consumers ignore malformed source metadata. The first retained source is preserved on retries and abandonment. Resends retain the same source. Existing commands without a source remain valid.

Declaration producers use type `compliance-declaration` and the declaration ID. Submission and cancellation keys share the `compliance-declaration` prefix; each cancellation key still distinguishes action and recipient.
