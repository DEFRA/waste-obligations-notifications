using System.Text.Json.Serialization;
using Defra.WasteObligations.Consumer.Commands;
using Defra.WasteObligations.Consumer.Delivery;

namespace Defra.WasteObligations.Consumer.Administration;

public sealed record CommandDlqSelection(
    string ReceiveRequestAttemptId,
    string MessageId,
    string QueueBinding,
    DateTimeOffset ExpiresAtUtc,
    string ImmutableFieldsDigest,
    int VisibilityTimeoutSeconds,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? MaxNumberOfMessages = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? NotificationKey = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ResendOfNotificationKey = null
)
{
    public bool Matches(NotificationCommand command, INotificationCommandDigest digest) =>
        digest.CreateImmutableFieldsDigest(command) == ImmutableFieldsDigest
        && (
            NotificationKey is null
            || (
                digest.CreateIdempotencyKeyDigest(command.IdempotencyKey) == NotificationKey
                && command.ResendOfNotificationKey == ResendOfNotificationKey
            )
        );
}
