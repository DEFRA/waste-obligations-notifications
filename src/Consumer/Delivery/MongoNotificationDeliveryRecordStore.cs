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
    private const string OutcomeField = "outcome";
    private const string ServerNow = "$$NOW";
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

            if (existing.ImmutableFields != immutableFields)
                return SuppressionClaimResult.Conflict;

            return IsTerminal(existing.Outcome)
                ? SuppressionClaimResult.TerminalDuplicate
                : SuppressionClaimResult.ActiveClaim;
        }
    }

    public async Task<DeliveryClaimResult> Claim(
        NotificationCommand command,
        string attemptOwner,
        int leaseDurationSeconds,
        CancellationToken cancellationToken
    )
    {
        var notificationKey = _digest.CreateIdempotencyKeyDigest(command.IdempotencyKey);
        var immutableFields = _digest.CreateImmutableFieldsDigest(command);
        var filter = new BsonDocument
        {
            { "notificationKey", notificationKey },
            { "immutableFields", immutableFields },
            { OutcomeField, "delivery-pending" },
        };
        var fields = new BsonDocument
        {
            { "notificationKey", Literal(notificationKey) },
            { "immutableFields", Literal(immutableFields) },
            { "recipient", Literal(_digest.CreateRecipientDigest(command.EmailAddress)) },
            { "notificationType", Literal(command.NotificationType) },
            { "actionOccurredAtUtc", command.ActionOccurredAtUtc.UtcDateTime },
            { OutcomeField, "delivery-pending" },
            { "recordedAtUtc", ServerNow },
            { "attemptOwner", Literal(attemptOwner) },
            {
                "leaseExpiresAtUtc",
                new BsonDocument(
                    "$dateAdd",
                    new BsonDocument
                    {
                        { "startDate", ServerNow },
                        { "unit", "second" },
                        { "amount", leaseDurationSeconds },
                    }
                )
            },
        };
        var expired = new BsonDocument(
            "$lte",
            new BsonArray
            {
                new BsonDocument(
                    "$ifNull",
                    new BsonArray { "$leaseExpiresAtUtc", new BsonDateTime(DateTime.UnixEpoch) }
                ),
                ServerNow,
            }
        );
        var update = new PipelineUpdateDefinition<NotificationDeliveryRecord>(
            new[]
            {
                new BsonDocument(
                    "$replaceWith",
                    new BsonDocument(
                        "$cond",
                        new BsonArray
                        {
                            expired,
                            new BsonDocument("$mergeObjects", new BsonArray { "$$ROOT", fields }),
                            "$$ROOT",
                        }
                    )
                ),
            }
        );
        try
        {
            var record = await _records.FindOneAndUpdateAsync(
                filter,
                update,
                new FindOneAndUpdateOptions<NotificationDeliveryRecord>
                {
                    IsUpsert = true,
                    ReturnDocument = ReturnDocument.After,
                },
                cancellationToken
            );

            return record.AttemptOwner == attemptOwner ? DeliveryClaimResult.Claimed : DeliveryClaimResult.ActiveClaim;
        }
        catch (MongoCommandException exception) when (exception.Code == 11000)
        {
            return await ReadExistingClaim(notificationKey, immutableFields, cancellationToken);
        }
        catch (MongoWriteException exception) when (exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return await ReadExistingClaim(notificationKey, immutableFields, cancellationToken);
        }
    }

    public async Task<bool> RecordAcceptance(
        NotificationCommand command,
        string attemptOwner,
        NotifyAcceptance acceptance,
        CancellationToken cancellationToken
    )
    {
        if (!acceptance.Matches(command, _digest.CreateNotifyReference(command.IdempotencyKey)))
            throw new InvalidDataException("Notify acceptance evidence is incomplete or inconsistent.");
        var filter = new BsonDocument
        {
            { "notificationKey", _digest.CreateIdempotencyKeyDigest(command.IdempotencyKey) },
            { "immutableFields", _digest.CreateImmutableFieldsDigest(command) },
            { "attemptOwner", attemptOwner },
            { OutcomeField, "delivery-pending" },
        };
        var fields = new BsonDocument
        {
            { OutcomeField, NotificationDeliveryOutcome.DeliveryAccepted.ToStorageValue() },
            { "notifyReference", Literal(acceptance.Reference) },
            { "templateId", Literal(acceptance.TemplateId) },
            { "templateVersion", acceptance.TemplateVersion },
            { "notifyNotificationId", Literal(acceptance.NotificationId) },
            { "acceptedAtUtc", ServerNow },
            { "recordedAtUtc", ServerNow },
        };
        var update = new PipelineUpdateDefinition<NotificationDeliveryRecord>(
            new[]
            {
                new BsonDocument("$set", fields),
                new BsonDocument("$unset", new BsonArray { "attemptOwner", "leaseExpiresAtUtc" }),
            }
        );
        var result = await _records.UpdateOneAsync(filter, update, cancellationToken: cancellationToken);

        return result.ModifiedCount == 1;
    }

    private async Task<DeliveryClaimResult> ReadExistingClaim(
        string notificationKey,
        string immutableFields,
        CancellationToken cancellationToken
    )
    {
        var existing = await _records
            .Find(record => record.NotificationKey == notificationKey)
            .FirstOrDefaultAsync(cancellationToken);
        if (existing is null)
            return DeliveryClaimResult.Unavailable;
        if (existing.ImmutableFields != immutableFields)
            return DeliveryClaimResult.Conflict;

        return IsTerminal(existing.Outcome) ? DeliveryClaimResult.TerminalDuplicate : DeliveryClaimResult.ActiveClaim;
    }

    private static BsonDocument Literal(string value) => new("$literal", value);

    private static bool IsTerminal(string outcome) =>
        outcome is "delivery-accepted" or "delivery-suppressed" or "delivery-abandoned";
}
