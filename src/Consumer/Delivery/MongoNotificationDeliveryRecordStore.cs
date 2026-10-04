using Defra.WasteObligations.Consumer.Commands;
using Defra.WasteObligations.Consumer.Data;
using Defra.WasteObligations.Consumer.Data.Entities;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
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

    public async Task<SuppressionClaimResult?> GetSuppression(
        NotificationCommand command,
        CancellationToken cancellationToken
    )
    {
        var notificationKey = _digest.CreateIdempotencyKeyDigest(command.IdempotencyKey);
        var existing = await _records
            .Find(record => record.NotificationKey == notificationKey)
            .FirstOrDefaultAsync(cancellationToken);
        if (existing is null)
            return null;
        if (existing.ImmutableFields != _digest.CreateImmutableFieldsDigest(command))
            return SuppressionClaimResult.Conflict;

        return existing.Outcome == NotificationDeliveryOutcome.DeliverySuppressed.ToStorageValue()
            ? SuppressionClaimResult.TerminalDuplicate
            : null;
    }

    // A future PR will add delivery claims and leases; this path only records terminal suppression.
    public async Task<SuppressionClaimResult> RecordSuppression(
        NotificationCommand command,
        CancellationToken cancellationToken
    )
    {
        var notificationKey = _digest.CreateIdempotencyKeyDigest(command.IdempotencyKey);
        var immutableFields = _digest.CreateImmutableFieldsDigest(command);
        var update = Builders<NotificationDeliveryRecord>
            .Update.SetOnInsert(record => record.NotificationKey, notificationKey)
            .SetOnInsert(record => record.ImmutableFields, immutableFields)
            .SetOnInsert(record => record.Recipient, _digest.CreateRecipientDigest(command.EmailAddress))
            .SetOnInsert(record => record.NotificationType, command.NotificationType)
            .SetOnInsert(
                record => record.ActionOccurredAtUtc,
                MongoDateTime.TruncateToMilliseconds(command.ActionOccurredAtUtc).UtcDateTime
            )
            .SetOnInsert(record => record.Outcome, NotificationDeliveryOutcome.DeliverySuppressed.ToStorageValue())
            .CurrentDate(record => record.RecordedAtUtc);

        try
        {
            // A fresh ID forces insertion; the unique notification key protects existing evidence.
            var filter = Builders<NotificationDeliveryRecord>.Filter.Eq(record => record.Id, ObjectId.GenerateNewId());
            await _records.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true }, cancellationToken);

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
