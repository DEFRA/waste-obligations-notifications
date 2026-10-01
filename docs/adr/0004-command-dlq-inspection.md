# 0004: Authenticated command-DLQ inspection

Status: Accepted

## Context

Operators need to inspect failed notification commands without exposing message
content or altering delivery evidence. Command sending can be paused during
recovery. The approved administration contract uses Waste Obligations' Basic ACL
and administrator endpoint structure.

## Decision

Use Waste Obligations' `Basic` client authentication, `Acl.Clients` configuration,
name/client-ID/scope claims and authenticated `Admin` policy requiring `admin`.
Adapt malformed-header handling to generic denial with strict UTF-8 decoding.
Validate enabled clients and require an ApiKey administrator. OAuth entries may
coexist but cannot authenticate; no Bearer scheme is imported.

Enable inspection separately from sending and start/check Mongo migrations for
either capability. Inspection waits for readiness and receives one visible FIFO
DLQ message. It does not publish, delete or modify delivery evidence. A bounded
dependency timeout rejects cancelled or late results.

Return only minimal metadata. The user explicitly approved exact raw idempotency
key and notification type in authenticated responses, including private-bearing
spellings. This exception does not extend to logs, metrics, tokens or other
message fields. Invalid commands expose neither partial identity nor a usable
selection. Historical dependency errors remain unavailable.

Sign a content-free selection with the existing evidence secret and a distinct
HMAC domain. Its five fields are receive-attempt ID, opaque SQS message ID, HMAC
queue binding, absolute UTC expiry and immutable-field digest. Anchor expiry
before receive and keep it below AWS's five-minute receive-attempt window.
Correctly configured hosts validate the same selection without host-local keys
or storing payloads.

## Consequences

Inspection temporarily changes visibility and receive count. Disabled
administration exposes no management routes and ignores unvalidated ACL entries.
Deployments must supply admin credentials and operator access separately.
Redrive, discard and abandonment writes remain unavailable in this increment.

## References

- [Waste Obligations authentication at e2bacd5](https://github.com/DEFRA/waste-obligations/tree/e2bacd53cc3158df41ee22eebf1fa9b3eee30476/src/Api/Authentication)
- [AWS ReceiveMessage](https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_ReceiveMessage.html)
- [Service behaviour](../service-behaviour.md#command-dlq-inspection)
