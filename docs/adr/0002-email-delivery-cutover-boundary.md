# ADR 0002: Email-delivery cutover boundary

**Status:** accepted

**Date:** 2026-09-29

## Context

Waste Obligations has an existing email path while Notifications introduces a
new delivery path. During migration, either service could otherwise send the
same compliance-declaration email, particularly when an analytics event is
delayed or replayed.

The migration needs a boundary based on the business action that triggered the
email, rather than mutable declaration state or when a consumer receives an
event.

## Decision

The initial cutover is null: Waste Obligations sends every action, while
Notifications records `delivery-suppressed` and deletes every valid command.
This permits a producer dry run; suppression remains terminal when a cutover is
later configured.

Notifications and Waste Obligations use the same deployment-owned future
UTC value, `EmailDeliveryCutoverUtc`, with inverse decisions. Notifications
sends an email command when its `actionOccurredAtUtc` is at or after the
boundary; before the boundary it records `delivery-suppressed` and deletes the
command. Waste Obligations sends only for actions before the same boundary and
suppresses actions at or after it.

For compliance declarations, the action time is the `Submitted` or `Cancelled`
audit-entry timestamp. It is never a mutable `Created` or `Updated` timestamp.

A supplied cutover and serialized command action timestamp must include an
explicit UTC timezone: `Z` or a numeric zero offset (`+00:00` or `-00:00`).
Reject offset-free and nonzero-offset input so host timezone cannot change the
delivery boundary. Parse both values with the same ISO timestamp parser and
truncate to whole milliseconds, matching MongoDB storage precision. Ignore
sub-millisecond precision in command serialisation and immutable evidence as well
so a Mongo roundtrip preserves command identity. The producer's inverse cutover
decision must use the same precision. Validate the cutover at startup when command
processing is enabled.

## Consequences

Deploy both services with null first, activate the MO-549/MO-550 producers and
compare suppression evidence with Waste Obligations sends. Then choose the
identical future X, deploy Notifications and Waste Obligations before X, and
verify both deployed values match. Local configuration does not set CDP values.
Once X passes, the handover is forward-only: do not clear or move the cutover
backward to restore direct sending. There is no fallback to Waste Obligations
after X; use Notifications delivery/recovery to resolve failures.

Both services must be deployed and configured with the identical future value
before the boundary. The handover then occurs without another deployment,
regardless of later declaration-state changes or when Notifications consumes an
analytics event.

`delivery-suppressed` is a terminal, duplicate-suppressing outcome distinct
from Notify acceptance and operator abandonment. A later replay of the same
command must not be sent by Notifications.

A command already suppressed while cutover was null remains terminal even when
its action timestamp is at or after the later configured boundary. Matching
replays are deleted; a fresh post-cutover command is not recorded as suppressed.
