using Defra.WasteObligations.Consumer.Commands;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace Defra.WasteObligations.Consumer.Delivery;

public sealed class MongoNotificationDeliveryRecordStore : INotificationDeliveryRecordStore
{
    private const string CollectionName = "notificationDeliveryRecords";
    private const string NotificationKeyIndexName = "notificationKey_unique";
    private readonly INotificationCommandDigest _digest;
    private readonly Lazy<Task> _indexCreation;
    private readonly IMongoCollection<NotificationDeliveryRecord> _records;

    public MongoNotificationDeliveryRecordStore(
        IMongoClient mongoClient,
        IOptions<NotificationCommandDeliveryOptions> options,
        INotificationCommandDigest digest
    )
    {
        _digest = digest;
        _records = mongoClient
            .GetDatabase(options.Value.MongoDatabaseName)
            .GetCollection<NotificationDeliveryRecord>(CollectionName);
        _indexCreation = new Lazy<Task>(CreateIndexes);
    }

    public async Task<SuppressionClaimResult> RecordSuppression(
        NotificationCommand command,
        CancellationToken cancellationToken
    )
    {
        await _indexCreation.Value.WaitAsync(cancellationToken);
        var notificationKey = _digest.CreateIdempotencyKeyDigest(command.IdempotencyKey);
        var immutableFields = _digest.CreateImmutableFieldsDigest(command);
        var record = new NotificationDeliveryRecord
        {
            NotificationKey = notificationKey,
            ImmutableFields = immutableFields,
            Recipient = _digest.CreateRecipientDigest(command.EmailAddress),
            NotificationType = command.NotificationType,
            ActionOccurredAtUtc = command.ActionOccurredAtUtc.UtcDateTime,
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

    private Task CreateIndexes() =>
        _records.Indexes.CreateOneAsync(
            new CreateIndexModel<NotificationDeliveryRecord>(
                Builders<NotificationDeliveryRecord>.IndexKeys.Ascending(record => record.NotificationKey),
                new CreateIndexOptions { Name = NotificationKeyIndexName, Unique = true }
            )
        );
}
