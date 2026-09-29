# Notification delivery

This service receives notification commands from upstream producers and delivers
their email intent. It does not decide whether a business event requires a
notification.

## Language

**Notification command**:
One intended email, created by an upstream producer for delivery as soon as it
is consumed. A command is independent of the transport or event that initiated
it.

**Idempotency key**:
The stable identity of one intended notification command. Retries reuse the
same key; an intentionally new email has a new key.
_Avoid_: message ID, event ID, delivery ID

**Action occurred at**:
The immutable UTC timestamp of the business action that made a notification
necessary. It is used to evaluate the email-delivery cutover boundary.
_Avoid_: processing time, updated time

**Recipient lane**:
The per-recipient ordering lane for notification commands. It preserves the
order in which commands enter the delivery queue, not the order in which a
mailbox presents emails.

**Accepted by Notify**:
A terminal outcome meaning GOV.UK Notify accepted a send request. It is not
evidence that the recipient received or read the email.

**Delivery-suppressed**:
A terminal outcome meaning the command was intentionally not sent because its
action occurred before the delivery cutover boundary.

**Delivery-abandoned**:
A terminal outcome meaning an operator chose not to make another attempt for
that exact intended email.
