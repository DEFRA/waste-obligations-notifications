using System.Diagnostics;
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
            var filters = new List<FilterDefinition<BsonDocument>>();
            var builder = Builders<BsonDocument>.Filter;
            if (query.RecordedFromUtc is { } from)
                filters.Add(builder.Gte("recordedAtUtc", RoundUpToMillisecond(from)));
            if (query.RecordedToUtc is { } to)
                filters.Add(builder.Lt("recordedAtUtc", RoundUpToMillisecond(to)));
            AddExact(filters, "notifyNotificationId", query.NotifyNotificationId);
            AddExact(filters, "notificationKey", query.NotificationKey);
            AddExact(filters, "resendOfNotificationKey", query.ResendOfNotificationKey);
            AddExact(filters, "outcome", query.Outcome);
            if (query.ReadCursor() is { } cursor)
                filters.Add(
                    builder.Or(
                        builder.Lt("recordedAtUtc", cursor.RecordedAtUtc),
                        builder.And(builder.Eq("recordedAtUtc", cursor.RecordedAtUtc), builder.Lt("_id", cursor.Id))
                    )
                );
            var projection = new BsonDocument
            {
                { "_id", 1 },
                { "notificationKey", 1 },
                { "resendOfNotificationKey", 1 },
                { "notificationType", 1 },
                { "outcome", 1 },
                { "recordedAtUtc", 1 },
                { "actionOccurredAtUtc", 1 },
                { "acceptedAtUtc", 1 },
                { "leaseExpiresAtUtc", 1 },
                { "notifyNotificationId", 1 },
                { "notifyReference", 1 },
                { "templateId", 1 },
                { "templateVersion", 1 },
            };
            var records = mongoClient
                .GetDatabase(mongoOptions.Value.DatabaseName)
                .GetCollection<BsonDocument>("NotificationDeliveryRecord");
            var documents = await records
                .Find(
                    filters.Count == 0 ? builder.Empty : builder.And(filters),
                    new FindOptions { Collation = Collation.Simple, MaxTime = timeout }
                )
                .Sort(Builders<BsonDocument>.Sort.Descending("recordedAtUtc").Descending("_id"))
                .Project<BsonDocument>(projection)
                .Limit(query.PageSize + 1)
                .ToListAsync(source.Token)
                .WaitAsync(source.Token);
            source.Token.ThrowIfCancellationRequested();
            if (Stopwatch.GetElapsedTime(started) >= timeout)
                throw new TimeoutException();
            var page = documents.Take(query.PageSize).ToArray();
            var nextCursor = documents.Count > query.PageSize
                ? query.CreateCursor(page[^1]["recordedAtUtc"].ToUniversalTime(), page[^1]["_id"].AsObjectId)
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

    private static void AddExact(List<FilterDefinition<BsonDocument>> filters, string field, string? value)
    {
        if (value is not null)
            filters.Add(Builders<BsonDocument>.Filter.Eq(field, value));
    }

    // Stored timestamps have millisecond precision; both range bounds round upward to preserve comparisons.
    private static BsonDateTime RoundUpToMillisecond(DateTime value)
    {
        var remainder = value.Ticks % TimeSpan.TicksPerMillisecond;

        var roundedTicks = value.Ticks + (remainder == 0 ? 0 : TimeSpan.TicksPerMillisecond - remainder);

        return new BsonDateTime((roundedTicks - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerMillisecond);
    }

    private static DeliveryRecordSummary Summarise(BsonDocument record)
    {
        var parent = ReadString(record, "resendOfNotificationKey");

        return new(
            record["notificationKey"].AsString,
            parent,
            record["notificationType"].AsString,
            record["outcome"].AsString,
            record["recordedAtUtc"].ToUniversalTime(),
            record["actionOccurredAtUtc"].ToUniversalTime(),
            ReadDate(record, "acceptedAtUtc"),
            ReadDate(record, "leaseExpiresAtUtc"),
            ReadString(record, "notifyNotificationId"),
            ReadString(record, "notifyReference"),
            ReadString(record, "templateId"),
            record.TryGetValue("templateVersion", out var version) && !version.IsBsonNull ? version.AsInt32 : null,
            parent is null
                ? null
                : new("/admin/notification-commands/delivery-records?notificationKey=" + Uri.EscapeDataString(parent))
        );
    }

    private static string? ReadString(BsonDocument record, string name) =>
        record.TryGetValue(name, out var value) && !value.IsBsonNull ? value.AsString : null;

    private static DateTime? ReadDate(BsonDocument record, string name) =>
        record.TryGetValue(name, out var value) && !value.IsBsonNull ? value.ToUniversalTime() : null;
}
