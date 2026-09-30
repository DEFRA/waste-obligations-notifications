# ADR 0002: Email-delivery cutover boundary

**Status:** proposed

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

Notifications and Waste Obligations will use the same deployment-owned future
UTC value, `EmailDeliveryCutoverUtc`, with inverse decisions. Notifications
sends an email command when its `actionOccurredAtUtc` is at or after the
boundary; before the boundary it records `delivery-suppressed` and deletes the
command. Waste Obligations sends only for actions before the same boundary and
suppresses actions at or after it.

For compliance declarations, the action time is the `Submitted` or `Cancelled`
audit-entry timestamp. It is never a mutable `Created` or `Updated` timestamp.

The configured cutover and serialized command action timestamp must include an
explicit UTC timezone: `Z` or a numeric zero offset (`+00:00` or `-00:00`).
Reject offset-free and nonzero-offset input so host timezone cannot change the
delivery boundary. Retain timestamp precision when comparing the two values.
Validate the cutover at startup when command processing is enabled.

## Consequences

Both services must be deployed and configured with the identical future value
before the boundary. The handover then occurs without another deployment,
regardless of later declaration-state changes or when Notifications consumes an
analytics event.

`delivery-suppressed` is a terminal, duplicate-suppressing outcome distinct
from Notify acceptance and operator abandonment. A later replay of the same
command must not be sent by Notifications.
