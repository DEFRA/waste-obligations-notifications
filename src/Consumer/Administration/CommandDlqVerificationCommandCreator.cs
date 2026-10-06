using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using Defra.WasteObligations.Consumer.Commands;
using Defra.WasteObligations.Consumer.Delivery;
using Microsoft.Extensions.Options;

namespace Defra.WasteObligations.Consumer.Administration;

public sealed class CommandDlqVerificationCommandCreator(
    IAmazonSQS sqs,
    INotificationDeliveryRecordStore store,
    INotificationCommandDigest digest,
    IOptions<CommandDlqAdministrationOptions> administration
)
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<CommandDlqVerificationCommand> Create(CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        using var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(TimeSpan.FromSeconds(administration.Value.DependencyTimeoutSeconds));
        var command = new NotificationCommand(
            NotificationCommand.CurrentSchemaVersion,
            $"verification-{Guid.NewGuid():N}",
            new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero),
            "admin-verification",
            "verification@example.invalid",
            "00000000-0000-0000-0000-000000000000",
            JsonSerializer.SerializeToElement(new { })
        ).NormaliseRecipient();
        command.Validate();
        EnsureTimely(started, source.Token);
        var result = await store.RecordSuppression(command, source.Token);
        EnsureTimely(started, source.Token);
        if (result != SuppressionClaimResult.Recorded)
            throw new InvalidOperationException("Verification command suppression was not recorded.");
        // Permanent matching suppression must exist before the command can reach any queue or consumer.
        var response = await sqs.SendMessageAsync(
            new SendMessageRequest
            {
                QueueUrl = administration.Value.QueueUrl,
                MessageBody = JsonSerializer.Serialize(command, s_jsonOptions),
                MessageDeduplicationId = command.IdempotencyKey,
                MessageGroupId = digest.CreateRecipientLane(command.EmailAddress),
            },
            source.Token
        );
        EnsureTimely(started, source.Token);
        if (response?.HttpStatusCode != HttpStatusCode.OK || response.MessageId is not { Length: > 0 and <= 100 })
            throw new InvalidOperationException("Verification command publication was not confirmed.");

        return new(command.IdempotencyKey, response.MessageId);
    }

    private void EnsureTimely(long started, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(administration.Value.DependencyTimeoutSeconds))
            throw new TimeoutException("Verification command creation exceeded its timeout.");
    }
}
