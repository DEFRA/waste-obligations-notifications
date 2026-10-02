using Defra.WasteObligations.Consumer.Commands;
using Defra.WasteObligations.Consumer.Data;
using Defra.WasteObligations.Consumer.Data.Entities;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace Defra.WasteObligations.Consumer.Delivery;

public sealed class MongoNotificationDeliveryRecordStore : INotificationDeliveryRecordStore
{
    internal const string CollectionName = nameof(NotificationDeliveryRecord);
    private readonly INotificationCommandDigest _digest;
    private readonly IMongoCollection<NotificationDeliveryRecord> _records;

    public MongoNotificationDeliveryRecordStore(
        IMongoClient mongoClient,
        IOptions<MongoDbOptions> options,
        INotificationCommandDigest digest
    )
    {
        _digest = digest;
        _records = mongoClient
            .GetDatabase(options.Value.DatabaseName)
            .GetCollection<NotificationDeliveryRecord>(CollectionName);
    }

    // A future PR will add delivery claims and leases; this path only records terminal suppression.
    public async Task<SuppressionClaimResult> RecordSuppression(
        NotificationCommand command,
        CancellationToken cancellationToken
    )
    {
        var notificationKey = _digest.CreateIdempotencyKeyDigest(command.IdempotencyKey);
        var immutableFields = _digest.CreateImmutableFieldsDigest(command);
        var record = new NotificationDeliveryRecord
        {
            NotificationKey = notificationKey,
            ImmutableFields = immutableFields,
            Recipient = _digest.CreateRecipientDigest(command.EmailAddress),
            NotificationType = command.NotificationType,
            ActionOccurredAtUtc = MongoDateTime.TruncateToMilliseconds(command.ActionOccurredAtUtc).UtcDateTime,
            Outcome = NotificationDeliveryOutcome.DeliverySuppressed.ToStorageValue(),
            RecordedAtUtc = DateTime.UtcNow,
        };

        try
        {
            await _records.InsertOneAsync(record, cancellationToken: cancellationToken);

            return SuppressionClaimResult.Recorded;
        }
        catch (MongoWriteException exception) when (exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            var existing = await _records
                .Find(existingRecord => existingRecord.NotificationKey == notificationKey)
                .FirstOrDefaultAsync(cancellationToken);

            if (existing is null)
            {
                throw;
            }

            return existing.ImmutableFields == immutableFields
                ? SuppressionClaimResult.TerminalDuplicate
                : SuppressionClaimResult.Conflict;
        }
    }
}
