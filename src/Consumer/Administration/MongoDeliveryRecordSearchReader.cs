using System.Diagnostics;
using Defra.WasteObligations.Consumer.Commands;
using Defra.WasteObligations.Consumer.Data;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Defra.WasteObligations.Consumer.Administration;

public sealed class MongoDeliveryRecordSearchReader(
    IMongoClient mongoClient,
    IOptions<MongoDbOptions> mongoOptions,
    IOptions<CommandDlqAdministrationOptions> administration
) : IDeliveryRecordSearchReader
{
    private const string ParentField = "resendOfNotificationKey";
    private const string RecordedAtField = "recordedAtUtc";
    private const string ActionOccurredAtField = "actionOccurredAtUtc";
    private const string NotificationKeyField = "notificationKey";
    private const string NotificationTypeField = "notificationType";
    private const string OutcomeField = "outcome";
    private const string StringType = "string";

    public async Task<DeliveryRecordSearchResult> Search(
        DeliveryRecordSearchQuery query,
        CancellationToken cancellationToken
    )
    {
        query.Validate();
        var timeout = TimeSpan.FromSeconds(administration.Value.DependencyTimeoutSeconds);
        var started = Stopwatch.GetTimestamp();
        using var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(timeout);
        try
        {
            source.Token.ThrowIfCancellationRequested();
            var filters = new List<FilterDefinition<BsonDocument>> { ReadableRecords() };
            var builder = Builders<BsonDocument>.Filter;
            if (query.RecordedFromUtc is { } from)
                filters.Add(builder.Gte(RecordedAtField, RoundUpToMillisecond(from)));
            if (query.RecordedToUtc is { } to)
                filters.Add(builder.Lt(RecordedAtField, RoundUpToMillisecond(to)));
            AddExact(filters, "notifyNotificationId", query.NotifyNotificationId);
            AddExact(filters, NotificationKeyField, query.NotificationKey);
            AddExact(filters, ParentField, query.ResendOfNotificationKey);
            // Make the partial-index predicate visible to the planner; AddExact still rejects array values.
            if (query.ResendOfNotificationKey is not null)
                filters.Add(builder.Type(ParentField, BsonType.String));
            AddExact(filters, OutcomeField, query.Outcome);
            AddExact(filters, "source.type", query.SourceType);
            AddExact(filters, "source.id", query.SourceId);
            AddExact(filters, "recipient", query.RecipientDigest);
            if (query.ReadCursor() is { } cursor)
                filters.Add(
                    builder.Or(
                        builder.Lt(RecordedAtField, cursor.RecordedAtUtc),
                        builder.And(builder.Eq(RecordedAtField, cursor.RecordedAtUtc), builder.Lt("_id", cursor.Id))
                    )
                );
            var projection = new BsonDocument
            {
                { "_id", 1 },
                { NotificationKeyField, 1 },
                { NotificationTypeField, 1 },
                { OutcomeField, 1 },
                { RecordedAtField, 1 },
                { ActionOccurredAtField, 1 },
                { "acceptedAtUtc", 1 },
                { "leaseExpiresAtUtc", 1 },
                { "notifyNotificationId", 1 },
                { "notifyReference", 1 },
                { "templateId", 1 },
                { "templateVersion", 1 },
                { ParentField, 1 },
                { "source", 1 },
            };
            var records = mongoClient
                .GetDatabase(mongoOptions.Value.DatabaseName)
                .GetCollection<BsonDocument>("NotificationDeliveryRecord");
            var documents = await records
                .Find(
                    filters.Count == 0 ? builder.Empty : builder.And(filters),
                    new FindOptions { Collation = Collation.Simple, MaxTime = timeout }
                )
                .Sort(Builders<BsonDocument>.Sort.Descending(RecordedAtField).Descending("_id"))
                .Project<BsonDocument>(projection)
                .Limit(query.PageSize + 1)
                .ToListAsync(source.Token)
                .WaitAsync(source.Token);
            source.Token.ThrowIfCancellationRequested();
            if (Stopwatch.GetElapsedTime(started) >= timeout)
                throw new TimeoutException();
            var page = documents.Take(query.PageSize).ToArray();
            var nextCursor =
                documents.Count > query.PageSize
                    ? query.CreateCursor(page[^1][RecordedAtField].ToUniversalTime(), page[^1]["_id"].AsObjectId)
                    : null;

            return new(page.Select(Summarise).ToArray(), nextCursor);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            throw new InvalidOperationException("Delivery record search failed.");
        }
    }

    private static BsonDocument ReadableRecords() =>
        new(
            "$expr",
            new BsonDocument(
                "$and",
                new BsonArray
                {
                    HasScalarType("_id", "objectId"),
                    HasScalarType(NotificationKeyField, StringType),
                    HasScalarType(NotificationTypeField, StringType),
                    HasScalarType(OutcomeField, StringType),
                    HasScalarType(RecordedAtField, "date"),
                    HasScalarType(ActionOccurredAtField, "date"),
                    DateBound(RecordedAtField, "$gte", DateTimeOffset.MinValue.ToUnixTimeMilliseconds()),
                    DateBound(RecordedAtField, "$lte", DateTimeOffset.MaxValue.ToUnixTimeMilliseconds()),
                    DateBound(ActionOccurredAtField, "$gte", DateTimeOffset.MinValue.ToUnixTimeMilliseconds()),
                    DateBound(ActionOccurredAtField, "$lte", DateTimeOffset.MaxValue.ToUnixTimeMilliseconds()),
                }
            )
        );

    private static BsonDocument HasScalarType(string field, string type) =>
        new("$eq", new BsonArray { new BsonDocument("$type", "$" + field), type });

    private static BsonDocument DateBound(string field, string comparison, long milliseconds) =>
        new(comparison, new BsonArray { "$" + field, new BsonDateTime(milliseconds) });

    private static void AddExact(List<FilterDefinition<BsonDocument>> filters, string field, string? value)
    {
        if (value is not null)
        {
            filters.Add(Builders<BsonDocument>.Filter.Eq(field, value));
            if (field != NotificationKeyField && field != OutcomeField)
                filters.Add(new BsonDocument("$expr", HasScalarType(field, StringType)));
        }
    }

    // Stored timestamps have millisecond precision; both range bounds round upward to preserve comparisons.
    private static BsonDateTime RoundUpToMillisecond(DateTime value)
    {
        var remainder = value.Ticks % TimeSpan.TicksPerMillisecond;

        var roundedTicks = value.Ticks + (remainder == 0 ? 0 : TimeSpan.TicksPerMillisecond - remainder);

        return new BsonDateTime((roundedTicks - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerMillisecond);
    }

    private static DeliveryRecordSummary Summarise(BsonDocument record) =>
        new(
            record[NotificationKeyField].AsString,
            record[NotificationTypeField].AsString,
            record[OutcomeField].AsString,
            record[RecordedAtField].ToUniversalTime(),
            record[ActionOccurredAtField].ToUniversalTime(),
            ReadDate(record, "acceptedAtUtc"),
            ReadDate(record, "leaseExpiresAtUtc"),
            ReadString(record, "notifyNotificationId"),
            ReadString(record, "notifyReference"),
            ReadString(record, "templateId"),
            ReadTemplateVersion(record),
            ReadString(record, ParentField) is { } parent
            && Commands.NotificationCommand.IsOpaqueNotificationKey(parent)
                ? parent
                : null,
            ReadSource(record)
        );

    private static NotificationSource? ReadSource(BsonDocument record)
    {
        if (!record.TryGetValue("source", out var value) || value is not BsonDocument source)
            return null;
        var type = ReadString(source, "type");
        var id = ReadString(source, "id");
        return type is not null && id is not null ? new NotificationSource(type, id) : null;
    }

    private static string? ReadString(BsonDocument record, string name) =>
        record.TryGetValue(name, out var value) && value.IsString ? value.AsString : null;

    private static DateTime? ReadDate(BsonDocument record, string name) =>
        record.TryGetValue(name, out var value)
        && value is BsonDateTime date
        && date.MillisecondsSinceEpoch >= DateTimeOffset.MinValue.ToUnixTimeMilliseconds()
        && date.MillisecondsSinceEpoch <= DateTimeOffset.MaxValue.ToUnixTimeMilliseconds()
            ? date.ToUniversalTime()
            : null;

    private static int? ReadTemplateVersion(BsonDocument record)
    {
        if (!record.TryGetValue("templateVersion", out var value))
            return null;

        return value switch
        {
            BsonInt32 version => version.Value,
            BsonInt64 version when version.Value is >= int.MinValue and <= int.MaxValue => (int)version.Value,
            _ => null,
        };
    }
}
