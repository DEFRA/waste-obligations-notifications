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
    private readonly NotificationCommandDeliveryOptions _delivery;
    private readonly MongoMigrationReadiness _migrationReadiness;
    private readonly IMongoCollection<NotificationDeliveryRecord> _records;

    public MongoNotificationDeliveryRecordStore(
        IMongoClient mongoClient,
        IOptions<MongoDbOptions> options,
        INotificationCommandDigest digest,
        MongoMigrationReadiness migrationReadiness,
        IOptions<NotificationCommandDeliveryOptions> delivery
    )
    {
        _digest = digest;
        _delivery = delivery.Value;
        _records = mongoClient
            .GetDatabase(options.Value.DatabaseName)
            .GetCollection<NotificationDeliveryRecord>(CollectionName);
        _migrationReadiness = migrationReadiness;
    }

    public async Task<NotificationDeliveryState> Inspect(
        NotificationCommand command,
        CancellationToken cancellationToken
    )
    {
        await _migrationReadiness.Wait(cancellationToken);
        var fields = new BsonDocument
        {
            { "_id", 0 },
            { "outcome", 1 },
            { "recordedAtUtc", 1 },
            { "leaseExpiresAtUtc", 1 },
            {
                "matches",
                new BsonDocument(
                    "$eq",
                    new BsonArray { "$immutableFields", Literal(_digest.CreateImmutableFieldsDigest(command)) }
                )
            },
            {
                "active",
                new BsonDocument(
                    "$gt",
                    new BsonArray
                    {
                        new BsonDocument(
                            "$ifNull",
                            new BsonArray { "$leaseExpiresAtUtc", new BsonDateTime(DateTime.UnixEpoch) }
                        ),
                        "$$NOW",
                    }
                )
            },
        };
        var record = await _records
            .Aggregate()
            .Match(new BsonDocument("notificationKey", _digest.CreateIdempotencyKeyDigest(command.IdempotencyKey)))
            .Project<BsonDocument>(fields)
            .FirstOrDefaultAsync(cancellationToken);
        if (record is null)
            return new("unrecorded", null, null);
        var classification = record["outcome"].AsString switch
        {
            "delivery-pending" => record["active"].AsBoolean ? "active-claim" : "expired-claim",
            "delivery-accepted" => "delivery-accepted",
            "delivery-suppressed" => "delivery-suppressed",
            "delivery-abandoned" => "delivery-abandoned",
            _ => "unknown-delivery-state",
        };
        if (!record["matches"].AsBoolean)
            classification = "immutable-conflict";

        return new(classification, ReadTimestamp(record, "recordedAtUtc"), ReadTimestamp(record, "leaseExpiresAtUtc"));
    }

    private static DateTimeOffset? ReadTimestamp(BsonDocument record, string name) =>
        record.TryGetValue(name, out var value) && value.BsonType == BsonType.DateTime
            ? new DateTimeOffset(value.ToUniversalTime())
            : null;

    public async Task<AbandonmentResult> RecordAbandonment(
        NotificationCommand command,
        CancellationToken cancellationToken
    )
    {
        await _migrationReadiness.Wait(cancellationToken);
        var notificationKey = _digest.CreateIdempotencyKeyDigest(command.IdempotencyKey);
        var immutableFields = _digest.CreateImmutableFieldsDigest(command);
        var filter = new BsonDocument
        {
            { "notificationKey", notificationKey },
            { "immutableFields", immutableFields },
            { "outcome", "delivery-pending" },
        };
        var abandoned = new BsonDocument
        {
            { "_id", "$_id" },
            { "notificationKey", Literal(notificationKey) },
            { "immutableFields", Literal(immutableFields) },
            { "recipient", Literal(_digest.CreateRecipientDigest(command.EmailAddress)) },
            { "notificationType", Literal(_delivery.GetDiagnosticNotificationType(command.NotificationType)) },
            { "actionOccurredAtUtc", command.ActionOccurredAtUtc.UtcDateTime },
            { "outcome", "delivery-abandoned" },
            { "recordedAtUtc", "$$NOW" },
        };
        var expired = new BsonDocument(
            "$lte",
            new BsonArray
            {
                new BsonDocument(
                    "$ifNull",
                    new BsonArray { "$leaseExpiresAtUtc", new BsonDateTime(DateTime.UnixEpoch) }
                ),
                "$$NOW",
            }
        );
        var update = new PipelineUpdateDefinition<NotificationDeliveryRecord>(
            new[]
            {
                new BsonDocument(
                    "$replaceWith",
                    new BsonDocument("$cond", new BsonArray { expired, abandoned, "$$ROOT" })
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

            return record.Outcome == "delivery-abandoned" ? AbandonmentResult.Recorded : AbandonmentResult.Conflict;
        }
        catch (MongoCommandException exception) when (exception.Code == 11000)
        {
            return await ReadExistingAbandonment(notificationKey, immutableFields, cancellationToken);
        }
        catch (MongoWriteException exception) when (exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return await ReadExistingAbandonment(notificationKey, immutableFields, cancellationToken);
        }
    }

    private async Task<AbandonmentResult> ReadExistingAbandonment(
        string notificationKey,
        string immutableFields,
        CancellationToken cancellationToken
    )
    {
        var record = await _records
            .Find(new BsonDocument("notificationKey", notificationKey))
            .Project<BsonDocument>(
                new BsonDocument
                {
                    { "_id", 0 },
                    { "immutableFields", 1 },
                    { "outcome", 1 },
                }
            )
            .FirstOrDefaultAsync(cancellationToken);

        return
            record is not null
            && record.GetValue("immutableFields", BsonNull.Value) == immutableFields
            && record.GetValue("outcome", BsonNull.Value) == "delivery-abandoned"
            ? AbandonmentResult.AlreadyAbandoned
            : AbandonmentResult.Conflict;
    }

    public async Task<SuppressionClaimResult> RecordSuppression(
        NotificationCommand command,
        CancellationToken cancellationToken
    )
    {
        await _migrationReadiness.Wait(cancellationToken);
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
        await _migrationReadiness.Wait(cancellationToken);
        var notificationKey = _digest.CreateIdempotencyKeyDigest(command.IdempotencyKey);
        var immutableFields = _digest.CreateImmutableFieldsDigest(command);
        var filter = new BsonDocument
        {
            { "notificationKey", notificationKey },
            { "immutableFields", immutableFields },
            { "outcome", "delivery-pending" },
        };
        var fields = new BsonDocument
        {
            { "notificationKey", Literal(notificationKey) },
            { "immutableFields", Literal(immutableFields) },
            { "recipient", Literal(_digest.CreateRecipientDigest(command.EmailAddress)) },
            { "notificationType", Literal(command.NotificationType) },
            { "actionOccurredAtUtc", command.ActionOccurredAtUtc.UtcDateTime },
            { "outcome", "delivery-pending" },
            { "recordedAtUtc", "$$NOW" },
            { "attemptOwner", Literal(attemptOwner) },
            {
                "leaseExpiresAtUtc",
                new BsonDocument(
                    "$dateAdd",
                    new BsonDocument
                    {
                        { "startDate", "$$NOW" },
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
                "$$NOW",
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
        await _migrationReadiness.Wait(cancellationToken);
        if (!acceptance.Matches(command, _digest.CreateNotifyReference(command.IdempotencyKey)))
            throw new InvalidDataException("Notify acceptance evidence is incomplete or inconsistent.");
        var filter = new BsonDocument
        {
            { "notificationKey", _digest.CreateIdempotencyKeyDigest(command.IdempotencyKey) },
            { "immutableFields", _digest.CreateImmutableFieldsDigest(command) },
            { "attemptOwner", attemptOwner },
            { "outcome", "delivery-pending" },
            { "$expr", new BsonDocument("$gt", new BsonArray { "$leaseExpiresAtUtc", "$$NOW" }) },
        };
        var fields = new BsonDocument
        {
            { "outcome", NotificationDeliveryOutcome.DeliveryAccepted.ToStorageValue() },
            { "notifyReference", Literal(acceptance.Reference) },
            { "templateId", Literal(acceptance.TemplateId) },
            { "templateVersion", acceptance.TemplateVersion },
            { "notifyNotificationId", Literal(acceptance.NotificationId) },
            { "acceptedAtUtc", "$$NOW" },
            { "recordedAtUtc", "$$NOW" },
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
