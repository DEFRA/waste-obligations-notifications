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
