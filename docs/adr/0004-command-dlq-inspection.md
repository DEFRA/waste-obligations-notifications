# 0004: Authenticated command-DLQ inspection and redrive

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

Redrive replays the signed receive attempt, verifies the selected message and
immutable evidence, then publishes its exact body/encoding to the canonical
recipient lane. Use `v1:command-dlq-redrive:` plus a JSON array of source DLQ URL
and SQS message ID as the HMAC input with the evidence secret, returning a
versioned hexadecimal transport ID. This domain differs from normal command-key
publication and is stable across hosts and retries, permitting recovery while
SQS remembers a consumed original copy. Confirm publication before deleting only
the selected receipt. Bound the entire operation by dependency timeout and
selection expiry, rejecting late confirmations before further effects. Redrive
never changes delivery evidence; normal consumption retains terminal, conflict
and active-claim guards.

## Consequences

Inspection temporarily changes visibility and receive count. Disabled
administration exposes no management routes and ignores unvalidated ACL entries.
Deployments must supply admin credentials and operator access separately.
Recovery joins current lane order. An indeterminate publication can leave both
source and destination; failed deletion preserves publication. Repeated recovery
is deduplicated inside SQS's window and checked against durable command identity
on consumption. A prior indeterminate Notify request retains its accepted
duplicate-email risk. Discard and abandonment writes remain unavailable.

Floci does not implement receive-attempt replay. A labelled test-only API adapter
supplies that boundary while FIFO effects and Mongo remain real; deployment
validation must verify native AWS replay.

## References

- [Waste Obligations authentication at e2bacd5](https://github.com/DEFRA/waste-obligations/tree/e2bacd53cc3158df41ee22eebf1fa9b3eee30476/src/Api/Authentication)
- [AWS ReceiveMessage](https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_ReceiveMessage.html)
- [Service behaviour](../service-behaviour.md#command-dlq-inspection)
