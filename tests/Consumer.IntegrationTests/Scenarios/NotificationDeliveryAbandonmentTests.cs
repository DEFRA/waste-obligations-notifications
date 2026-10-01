using System.Text.Json;
using Defra.WasteObligations.Consumer.Commands;
using Defra.WasteObligations.Consumer.Data;
using Defra.WasteObligations.Consumer.Delivery;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Defra.WasteObligations.Consumer.IntegrationTests.Scenarios;

public sealed class NotificationDeliveryAbandonmentTests : IntegrationTestBase
{
    [Fact]
    public async Task WhenThreeHostsAbandonNewCommand_ShouldCreateOneMinimalServerTimedTerminalRecord()
    {
        await using var context = new AbandonmentContext();
        await context.Initialise();
        var token = TestContext.Current.CancellationToken;
        var command = Command() with { NotificationType = "private-recipient@example.com" };
        var before = (
            await context.Database.RunCommandAsync<BsonDocument>(new BsonDocument("hello", 1), cancellationToken: token)
        )["localTime"]
            .ToUniversalTime();
        var results = await Task.WhenAll(
            Enumerable.Range(0, 3).Select(_ => context.CreateStore().RecordAbandonment(command, token))
        );
        var after = (
            await context.Database.RunCommandAsync<BsonDocument>(new BsonDocument("hello", 1), cancellationToken: token)
        )["localTime"]
            .ToUniversalTime();

        Assert.Equal(1, results.Count(result => result == AbandonmentResult.Recorded));
        Assert.Equal(2, results.Count(result => result == AbandonmentResult.AlreadyAbandoned));
        var record = Assert.Single(await context.Records.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(token));
        Assert.Equal(
            [
                "_id",
                "actionOccurredAtUtc",
                "immutableFields",
                "notificationKey",
                "notificationType",
                "outcome",
                "recipient",
                "recordedAtUtc",
            ],
            record.Names.Order(StringComparer.Ordinal)
        );
        Assert.Equal("delivery-abandoned", record["outcome"].AsString);
        Assert.Equal("other", record["notificationType"].AsString);
        Assert.DoesNotContain(command.NotificationType, record.ToJson(), StringComparison.Ordinal);
        Assert.Equal(
            context.Digest.CreateIdempotencyKeyDigest(command.IdempotencyKey),
            record["notificationKey"].AsString
        );
        Assert.Equal(context.Digest.CreateImmutableFieldsDigest(command), record["immutableFields"].AsString);
        Assert.Equal(context.Digest.CreateRecipientDigest(command.EmailAddress), record["recipient"].AsString);
        Assert.InRange(record["recordedAtUtc"].ToUniversalTime(), before, after);
        Assert.Equal(
            DeliveryClaimResult.TerminalDuplicate,
            await context.Store.Claim(command, "future-attempt", 60, token)
        );
        Assert.Equal(SuppressionClaimResult.TerminalDuplicate, await context.Store.RecordSuppression(command, token));
        Assert.Equal(record, await context.Records.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync(token));
        AssertPrivacy(command, record);
    }

    [Theory]
    [InlineData("active")]
    [InlineData("expired")]
    [InlineData("conflict")]
    [InlineData("accepted")]
    [InlineData("suppressed")]
    [InlineData("unknown")]
    [InlineData("abandoned")]
    public async Task WhenAbandonmentMeetsExistingEvidence_ShouldChangeOnlyEligibleExpiredPendingHistory(string state)
    {
        await using var context = new AbandonmentContext();
        await context.Initialise();
        var token = TestContext.Current.CancellationToken;
        var command = Command();
        if (state is "suppressed")
            Assert.Equal(SuppressionClaimResult.Recorded, await context.Store.RecordSuppression(command, token));
        else if (state is "abandoned")
            Assert.Equal(AbandonmentResult.Recorded, await context.Store.RecordAbandonment(command, token));
        else
            Assert.Equal(
                DeliveryClaimResult.Claimed,
                await context.Store.Claim(command, "original-attempt", 60, token)
            );
        if (state == "accepted")
            Assert.True(
                await context.Store.RecordAcceptance(command, "original-attempt", Acceptance(context, command), token)
            );
        if (state == "unknown")
            await context.Records.UpdateOneAsync(
                FilterDefinition<BsonDocument>.Empty,
                new BsonDocument("$set", new BsonDocument("outcome", "private-unknown-outcome")),
                cancellationToken: token
            );
        if (state == "expired")
            await context.Records.UpdateOneAsync(
                FilterDefinition<BsonDocument>.Empty,
                new BsonDocument("$set", new BsonDocument("leaseExpiresAtUtc", DateTime.UnixEpoch)),
                cancellationToken: token
            );
        var before = await context.Records.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync(token);
        var requested = state == "conflict" ? command with { TemplateId = "changed-private-template" } : command;

        var result = await context.Store.RecordAbandonment(requested, token);

        var expected = state switch
        {
            "expired" => AbandonmentResult.Recorded,
            "abandoned" => AbandonmentResult.AlreadyAbandoned,
            _ => AbandonmentResult.Conflict,
        };
        Assert.Equal(expected, result);
        var after = await context.Records.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync(token);
        if (state == "expired")
        {
            Assert.Equal(before["_id"], after["_id"]);
            Assert.Equal(before["notificationKey"], after["notificationKey"]);
            Assert.Equal(before["immutableFields"], after["immutableFields"]);
            Assert.Equal("delivery-abandoned", after["outcome"].AsString);
            Assert.Equal(8, after.ElementCount);
            Assert.False(after.Contains("attemptOwner"));
            Assert.False(after.Contains("leaseExpiresAtUtc"));
            Assert.Equal(
                DeliveryClaimResult.TerminalDuplicate,
                await context.Store.Claim(command, "later-attempt", 60, token)
            );
            Assert.False(
                await context.Store.RecordAcceptance(command, "original-attempt", Acceptance(context, command), token)
            );
        }
        else
            Assert.Equal(before, after);
        if (state == "expired")
            Assert.Equal("submitted", after["notificationType"].AsString);
        AssertPrivacy(command, after);
    }

    [Fact]
    public async Task WhenAbandonmentRacesClaimRecovery_ShouldPermitOnlyOneTransitionFromExpiredPending()
    {
        await using var context = new AbandonmentContext();
        await context.Initialise();
        var token = TestContext.Current.CancellationToken;
        for (var iteration = 0; iteration < 10; iteration++)
        {
            var command = Command() with { IdempotencyKey = $"race-{iteration}" };
            Assert.Equal(DeliveryClaimResult.Claimed, await context.Store.Claim(command, "old-attempt", 60, token));
            var filter = new BsonDocument(
                "notificationKey",
                context.Digest.CreateIdempotencyKeyDigest(command.IdempotencyKey)
            );
            await context.Records.UpdateOneAsync(
                filter,
                new BsonDocument("$set", new BsonDocument("leaseExpiresAtUtc", DateTime.UnixEpoch)),
                cancellationToken: token
            );
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task<DeliveryClaimResult> Claim()
            {
                await start.Task;
                return await context.CreateStore().Claim(command, "new-attempt", 60, token);
            }
            async Task<AbandonmentResult> Abandon()
            {
                await start.Task;
                return await context.CreateStore().RecordAbandonment(command, token);
            }
            var claiming = Claim();
            var abandoning = Abandon();
            start.SetResult();
            await Task.WhenAll(claiming, abandoning);
            var claimResult = await claiming;
            var abandonmentResult = await abandoning;

            Assert.NotEqual(
                claimResult == DeliveryClaimResult.Claimed,
                abandonmentResult == AbandonmentResult.Recorded
            );
            var record = await context.Records.Find(filter).SingleAsync(token);
            Assert.Equal(
                claimResult == DeliveryClaimResult.Claimed ? "delivery-pending" : "delivery-abandoned",
                record["outcome"].AsString
            );
            Assert.False(
                await context.Store.RecordAcceptance(command, "old-attempt", Acceptance(context, command), token)
            );
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenAbandonmentRacesAcceptance_ShouldPreserveOnlyTheServerTimeEligibleTransition(bool expired)
    {
        await using var context = new AbandonmentContext();
        await context.Initialise();
        var token = TestContext.Current.CancellationToken;
        var command = Command();
        Assert.Equal(DeliveryClaimResult.Claimed, await context.Store.Claim(command, "accepting-attempt", 60, token));
        if (expired)
            await context.Records.UpdateOneAsync(
                FilterDefinition<BsonDocument>.Empty,
                new BsonDocument("$set", new BsonDocument("leaseExpiresAtUtc", DateTime.UnixEpoch)),
                cancellationToken: token
            );
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<bool> Accept()
        {
            await start.Task;
            return await context
                .CreateStore()
                .RecordAcceptance(command, "accepting-attempt", Acceptance(context, command), token);
        }
        async Task<AbandonmentResult> Abandon()
        {
            await start.Task;
            return await context.CreateStore().RecordAbandonment(command, token);
        }
        var accepting = Accept();
        var abandoning = Abandon();
        start.SetResult();
        await Task.WhenAll(accepting, abandoning);
        var accepted = await accepting;
        var abandonmentResult = await abandoning;

        Assert.Equal(!expired, accepted);
        Assert.Equal(expired ? AbandonmentResult.Recorded : AbandonmentResult.Conflict, abandonmentResult);
        var record = await context.Records.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync(token);
        Assert.Equal(expired ? "delivery-abandoned" : "delivery-accepted", record["outcome"].AsString);
        Assert.Equal(
            AbandonmentResult.Conflict,
            await context.Store.RecordAbandonment(command with { TemplateId = "conflicting-private-template" }, token)
        );
        Assert.Equal(record, await context.Records.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync(token));
    }

    private static NotificationCommand Command() =>
        new(
            1,
            "private-abandonment-key",
            new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
            "submitted",
            "private-recipient@example.com",
            "private-template",
            JsonSerializer.SerializeToElement(new { body = "private-personalisation" })
        );

    private static NotifyAcceptance Acceptance(AbandonmentContext context, NotificationCommand command) =>
        new(
            Guid.NewGuid().ToString(),
            context.Digest.CreateNotifyReference(command.IdempotencyKey),
            command.TemplateId,
            1
        );

    private static void AssertPrivacy(NotificationCommand command, BsonDocument record)
    {
        foreach (var value in new[] { command.IdempotencyKey, command.EmailAddress, "private-personalisation" })
            Assert.DoesNotContain(value, record.ToJson(), StringComparison.Ordinal);
    }

    private sealed class AbandonmentContext : IAsyncDisposable
    {
        private readonly IMongoClient _client = CreateMongoClient();
        private readonly ILoggerFactory _logs = LoggerFactory.Create(_ => { });
        private readonly MongoMigrationReadiness _readiness = new();
        private readonly string _databaseName = $"notifications_abandonment_{Guid.NewGuid():N}";
        public NotificationCommandDigest Digest { get; } =
            new NotificationCommandDigest(
                Options.Create(
                    new NotificationCommandDeliveryOptions
                    {
                        QueueUrl = "local",
                        EmailDeliveryCutoverUtc = "2026-10-01T00:00:00Z",
                        EvidenceDigestSecret = "local-abandonment-evidence-secret",
                        RecipientLaneSecret = "local-abandonment-lane-secret",
                    }
                )
            );
        public IMongoDatabase Database => _client.GetDatabase(_databaseName);
        public IMongoCollection<BsonDocument> Records =>
            Database.GetCollection<BsonDocument>("NotificationDeliveryRecord");
        public MongoNotificationDeliveryRecordStore Store => CreateStore();

        public Task Initialise() =>
            new MongoMigrationRunner(Database, _logs.CreateLogger<MongoMigrationRunner>(), _readiness).Run(
                TestContext.Current.CancellationToken
            );

        public MongoNotificationDeliveryRecordStore CreateStore() =>
            new MongoNotificationDeliveryRecordStore(
                _client,
                Options.Create(
                    new MongoDbOptions { DatabaseUri = "mongodb://localhost:27017", DatabaseName = _databaseName }
                ),
                Digest,
                _readiness,
                Options.Create(
                    new NotificationCommandDeliveryOptions
                    {
                        QueueUrl = "local",
                        EmailDeliveryCutoverUtc = "2026-10-01T00:00:00Z",
                        EvidenceDigestSecret = "local-abandonment-evidence-secret",
                        RecipientLaneSecret = "local-abandonment-lane-secret",
                        DiagnosticNotificationTypes = ["submitted"],
                    }
                )
            );

        public async ValueTask DisposeAsync()
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await _client.DropDatabaseAsync(_databaseName, cleanup.Token);
            }
            finally
            {
                _client.Dispose();
                _logs.Dispose();
            }
        }
    }
}
