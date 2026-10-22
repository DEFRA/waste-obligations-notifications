using Amazon.SQS.Model;
using Defra.WasteObligations.Consumer.IntegrationTests.Helpers;
using Defra.WasteObligations.Consumer.Services.AccountBackend;

namespace Defra.WasteObligations.Consumer.IntegrationTests.Scenarios;

public sealed class CancellationEmailSendTests : IntegrationTestBase
{
    [Theory]
    [InlineData(false, "GB-ENG", false, "3e03c93f-955c-4db3-936c-bfa3f7725a5f", "certificate", 3)]
    [InlineData(true, "GB-ENG", false, "3e03c93f-955c-4db3-936c-bfa3f7725a5f", "statement", 3)]
    [InlineData(false, "GB-WLS", false, "e419a544-e1b3-4ea5-b8eb-a074e63aea1a", "certificate", 3)]
    [InlineData(false, "GB-ENG", true, "3e03c93f-955c-4db3-936c-bfa3f7725a5f", "certificate", 2)]
    public async Task WhenCancellationAnalyticsIsHandled_ShouldPublishPredecessorFirstAndSendLiveRecipients(
        bool scheme,
        string country,
        bool deduplicate,
        string template,
        string document,
        int total
    )
    {
        await using var fixture = await CancellationEmailFixture.Create(scheme, country, deduplicate);

        await fixture.SendAnalytics(before: scheme ? "Accepted" : "Submitted");
        await WaitForAsync(async () =>
        {
            Assert.Equal(1, fixture.Sqs.AnalyticsDeletions);
            Assert.Equal(total, (await fixture.NotifyRequests()).Length);
            await fixture.AssertAcceptedRecords(total);
            await fixture.AssertQueuesEmpty();
        });

        var commands = fixture.Sqs.Confirmed.ToArray();
        Assert.Equal(total, commands.Length);
        Assert.StartsWith("declaration-submitted-", commands[0].NotificationType);
        Assert.Equal(deduplicate ? "submitter@example.test" : "primary@example.test", commands[1].EmailAddress);
        Assert.All(
            commands.Skip(1),
            command =>
            {
                Assert.Equal(template, command.TemplateId);
                Assert.Equal(
                    DateTimeOffset.Parse("2026-10-01T12:00:00.123Z", System.Globalization.CultureInfo.InvariantCulture),
                    command.ActionOccurredAtUtc
                );
            }
        );
        var requests = (await fixture.NotifyRequests())
            .Where(request => request.GetProperty("template_id").GetString() == template)
            .ToArray();
        Assert.Equal(total - 1, requests.Length);
        Assert.All(
            requests,
            request =>
            {
                var body = request.GetProperty("personalisation");
                Assert.Equal(2027, body.GetProperty("year").GetInt32());
                Assert.Equal(document, body.GetProperty("certOrStatement").GetString());
                Assert.Equal("Natural Resources Wales", body.GetProperty("regulator").GetString());
                Assert.Equal("stored-regulator@example.test", body.GetProperty("regulatorEmail").GetString());
                if (country == "GB-WLS")
                    Assert.Equal("Cyfoeth Naturiol Cymru", body.GetProperty("regulator_cy").GetString());
                else
                    Assert.False(body.TryGetProperty("regulator_cy", out _));
            }
        );
        var submitter = Assert.Single(
            requests,
            request => request.GetProperty("email_address").GetString() == "submitter@example.test"
        );
        Assert.Equal(" Original ", submitter.GetProperty("personalisation").GetProperty("firstName").GetString());
        Assert.Equal(" Submitter ", submitter.GetProperty("personalisation").GetProperty("lastName").GetString());
        Assert.Equal(1, fixture.State.DeclarationReads);
        Assert.Equal(scheme ? 1 : 0, fixture.State.SearchReads);
        Assert.Equal(1, fixture.State.TokenReads);

        await fixture.SendAnalytics();
        await WaitForAsync(async () =>
        {
            Assert.Equal(2, fixture.Sqs.AnalyticsDeletions);
            Assert.Equal(total * 2, fixture.Sqs.Confirmed.Count);
            await fixture.AssertQueuesEmpty();
        });
        Assert.Equal(total, (await fixture.NotifyRequests()).Length);
        Assert.Equal(commands[0].IdempotencyKey, fixture.Sqs.Confirmed.ToArray()[total].IdempotencyKey);

        var deletedBeforeTransportCopies = fixture.Sqs.CommandDeletions;
        foreach (var command in commands.Skip(1))
            await fixture.SendPublishedReplay(command);

        await WaitForAsync(async () =>
        {
            Assert.Equal(deletedBeforeTransportCopies + total - 1, fixture.Sqs.CommandDeletions);
            await fixture.AssertQueuesEmpty();
        });

        Assert.Equal(total, (await fixture.NotifyRequests()).Length);
        await fixture.AssertAcceptedRecords(total);
    }

    [Theory]
    [InlineData("no-persons")]
    [InlineData("outside-transition")]
    public async Task WhenNoCancellationIsDue_ShouldAcknowledgeWithoutAnyPublication(string scenario)
    {
        await using var fixture = await CancellationEmailFixture.Create();
        if (scenario == "no-persons")
            fixture.State.Persons = [];

        await fixture.SendAnalytics(before: scenario == "outside-transition" ? "Draft" : "Submitted");
        await WaitForAsync(async () =>
        {
            Assert.Equal(1, fixture.Sqs.AnalyticsDeletions);
            await fixture.AssertQueuesEmpty();
        });

        Assert.Empty(fixture.Sqs.Confirmed);
        if (scenario != "no-persons")
            Assert.Equal(0, fixture.State.LiveReads);
    }

    [Fact]
    public async Task WhenCancellationReasonIsUnknown_ShouldRetainAndRetryWithoutAnyPublication()
    {
        await using var fixture = await CancellationEmailFixture.Create();
        fixture.State.Reason = "Not a known reason";

        await fixture.SendAnalytics();
        await WaitForAsync(() =>
        {
            Assert.Equal(1, Volatile.Read(ref fixture.State.DeclarationReads));
            Assert.Equal(0, fixture.Sqs.AnalyticsDeletions);
            Assert.Empty(fixture.Sqs.Confirmed);

            return Task.CompletedTask;
        });

        await fixture.RetryAnalytics();
        await WaitForAsync(async () =>
        {
            Assert.Equal(2, Volatile.Read(ref fixture.State.DeclarationReads));
            Assert.Equal(0, fixture.Sqs.AnalyticsDeletions);
            Assert.Equal(0, fixture.Sqs.PublicationAttempts);
            Assert.Empty(fixture.Sqs.Confirmed);
            var queue = await fixture.Sqs.GetQueueAttributesAsync(
                new GetQueueAttributesRequest
                {
                    QueueUrl = fixture.Sqs.AnalyticsUrl,
                    AttributeNames = ["ApproximateNumberOfMessages", "ApproximateNumberOfMessagesNotVisible"],
                },
                TestContext.Current.CancellationToken
            );
            Assert.Equal("0", queue.Attributes["ApproximateNumberOfMessages"]);
            Assert.Equal("1", queue.Attributes["ApproximateNumberOfMessagesNotVisible"]);
        });

        Assert.Equal(0, fixture.State.LiveReads);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task WhenPublicationIsUnconfirmed_ShouldRetainThenReplaySamePublicationPrefix(int failedAttempt)
    {
        await using var fixture = await CancellationEmailFixture.Create();
        fixture.Sqs.FailPublicationAttempt = failedAttempt;
        await fixture.SendAnalytics();
        await WaitForAsync(() =>
        {
            Assert.Equal(failedAttempt, fixture.Sqs.PublicationAttempts);
            Assert.Equal(failedAttempt - 1, fixture.Sqs.Confirmed.Count);
            Assert.Equal(0, fixture.Sqs.AnalyticsDeletions);

            return Task.CompletedTask;
        });
        var beforeRetry = fixture.Sqs.Confirmed.ToArray();

        await fixture.RetryAnalytics();
        await WaitForAsync(async () =>
        {
            Assert.Equal(1, fixture.Sqs.AnalyticsDeletions);
            Assert.Equal(failedAttempt + 2, fixture.Sqs.Confirmed.Count);
            Assert.Equal(3, (await fixture.NotifyRequests()).Length);
            await fixture.AssertQueuesEmpty();
        });

        var confirmed = fixture.Sqs.Confirmed.ToArray();
        Assert.StartsWith("declaration-submitted-", confirmed[failedAttempt - 1].NotificationType);
        if (failedAttempt == 3)
        {
            Assert.Equal(beforeRetry[0].IdempotencyKey, confirmed[2].IdempotencyKey);
            Assert.Equal(beforeRetry[1].IdempotencyKey, confirmed[3].IdempotencyKey);
        }
    }

    [Fact]
    public async Task WhenLiveContactChangesOnReplay_ShouldPublishNewAddressAndKeepExistingRecipientIdentity()
    {
        await using var fixture = await CancellationEmailFixture.Create();
        await fixture.SendAnalytics();
        await WaitForAsync(async () =>
        {
            Assert.Equal(1, fixture.Sqs.AnalyticsDeletions);
            Assert.Equal(3, (await fixture.NotifyRequests()).Length);
        });
        var original = fixture.Sqs.Confirmed.ToArray();
        fixture.State.Persons =
        [
            fixture.State.Persons[0],
            new OrganisationPerson(null, "New", "Contact", "new@example.test", "Approved Person"),
        ];

        await fixture.SendAnalytics();
        await WaitForAsync(async () =>
        {
            Assert.Equal(2, fixture.Sqs.AnalyticsDeletions);
            Assert.Equal(4, (await fixture.NotifyRequests()).Length);
            await fixture.AssertAcceptedRecords(4);
            await fixture.AssertQueuesEmpty();
        });

        var replay = fixture.Sqs.Confirmed.ToArray().Skip(3).ToArray();
        Assert.Equal(original[0].IdempotencyKey, replay[0].IdempotencyKey);
        Assert.Equal(original[2].IdempotencyKey, replay[2].IdempotencyKey);
        Assert.NotEqual(original[1].IdempotencyKey, replay[1].IdempotencyKey);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenReadIsOlderOrFails_ShouldRetainWithoutPublicationUntilUsableRead(bool notFound)
    {
        await using var fixture = await CancellationEmailFixture.Create();
        fixture.State.Version = 1;
        fixture.State.ReadStatus = notFound ? 404 : 200;
        await fixture.SendAnalytics();
        await WaitForAsync(() =>
        {
            Assert.True(Volatile.Read(ref fixture.State.DeclarationReads) > 0);
            Assert.Equal(0, fixture.Sqs.AnalyticsDeletions);
            Assert.Empty(fixture.Sqs.Confirmed);

            return Task.CompletedTask;
        });
        fixture.State.Version = 3;
        fixture.State.ReadStatus = 200;

        await fixture.RetryAnalytics();
        await WaitForAsync(async () =>
        {
            Assert.Equal(1, fixture.Sqs.AnalyticsDeletions);
            Assert.Equal(3, (await fixture.NotifyRequests()).Length);
            await fixture.AssertQueuesEmpty();
        });
    }
}
