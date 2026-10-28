using Defra.WasteObligations.Consumer.IntegrationTests.Helpers;
using Defra.WasteObligations.Consumer.Scheduled;
using Defra.WasteObligations.Consumer.Services.AccountBackend;

namespace Defra.WasteObligations.Consumer.IntegrationTests.Scenarios;

public sealed class NotSubmittedReminderSendTests : IntegrationTestBase
{
    private const string EnglishTemplate = "54acaa9f-93c4-4052-bd0d-f5bc9e122ce6";
    private const string WelshTemplate = "156214dd-3735-4ed0-bb8d-c955facc7dd0";
    private static readonly string[] ExpectedPartialFailureEmails =
    [
        "first@example.test",
        "first@example.test",
        "second@example.test",
    ];

    [Fact]
    public async Task WhenScanCompletes_ShouldSkipLaterScansUntilReopenedAndSendOnceToEachDistinctRecipient()
    {
        await using var fixture = await NotSubmittedReminderFixture.Create();
        fixture.State.AddOrganisation(
            "GB-ENG",
            Person("approved-a@example.test", "Approved Person"),
            Person("delegated-a@example.test", "Delegated Person")
        );
        fixture.State.AddOrganisation(
            "GB-WLS",
            Person("same@example.test", "Approved Person"),
            Person(" SAME@example.test ", "Delegated Person")
        );

        var first = await fixture.Scan();
        await WaitForAsync(async () =>
        {
            Assert.Equal(3, (await fixture.NotifyRequests()).Length);
            await fixture.AssertAcceptedRecords(3);
            await fixture.AssertCommandQueueEmpty();
        });
        var firstCommands = fixture.ConfirmedCommands();
        var completed = await fixture.RunStatus();

        var skipped = await fixture.Scan();

        Assert.Equal(new NotSubmittedReminderScanResult(2, 3, 0, 0, null), first);
        Assert.Equal(
            new NotSubmittedReminderScanResult(0, 0, 0, 0, NotSubmittedReminderScanService.CompletedReason),
            skipped
        );
        Assert.Equal(2, fixture.State.UnsubmittedReads);
        Assert.Equal(3, fixture.ConfirmedCommands().Count);
        Assert.Equal("completed", completed.Status);
        Assert.Equal(1, completed.ScanCount);
        Assert.Equal(
            (2, 3, 0, 0),
            (
                completed.OrganisationsProcessed,
                completed.CommandsPublished,
                completed.OrganisationsSkipped,
                completed.RetryableFailures
            )
        );
        Assert.NotNull(completed.CompletedAtUtc);

        Assert.Null(await fixture.Reopen());
        var reopened = await fixture.Scan();
        await WaitForAsync(async () =>
        {
            Assert.Equal(6, fixture.ConfirmedCommands().Count);
            await fixture.AssertCommandQueueEmpty();
        });

        Assert.Equal(first, reopened);
        Assert.Equal(4, fixture.State.UnsubmittedReads);
        Assert.Equal(2, (await fixture.RunStatus()).ScanCount);
        Assert.Equal(
            firstCommands.Select(command => command.IdempotencyKey),
            fixture.ConfirmedCommands().Skip(3).Select(command => command.IdempotencyKey)
        );
        Assert.Equal(3, (await fixture.NotifyRequests()).Length);
        await fixture.AssertAcceptedRecords(3);
        var welsh = Assert.Single(firstCommands, command => command.TemplateId == WelshTemplate);
        Assert.Equal("same@example.test", welsh.EmailAddress);
        Assert.Equal(2, firstCommands.Count(command => command.TemplateId == EnglishTemplate));
    }

    [Fact]
    public async Task WhenFirstScanFailsPartway_ShouldCompleteOnRerunWithoutResending()
    {
        await using var fixture = await NotSubmittedReminderFixture.Create();
        fixture.State.AddOrganisation("GB-ENG", Person("first@example.test", "Approved Person"));
        fixture.State.AddOrganisation("GB-SCT", Person("second@example.test", "Approved Person"));
        fixture.Sqs.FailPublicationAttempt = 2;

        var first = await fixture.Scan();
        await WaitForAsync(async () => Assert.Single(await fixture.NotifyRequests()));

        var second = await fixture.Scan();
        await WaitForAsync(async () =>
        {
            Assert.Equal(2, (await fixture.NotifyRequests()).Length);
            await fixture.AssertAcceptedRecords(2);
            await fixture.AssertCommandQueueEmpty();
        });

        Assert.Equal(new NotSubmittedReminderScanResult(2, 1, 1, 1, null), first);
        Assert.Equal(new NotSubmittedReminderScanResult(2, 2, 0, 0, null), second);
        Assert.Equal(ExpectedPartialFailureEmails, fixture.ConfirmedCommands().Select(command => command.EmailAddress));
        var status = await fixture.RunStatus();
        Assert.Equal("completed", status.Status);
        Assert.Equal(2, status.ScanCount);
        Assert.Equal(0, status.RetryableFailures);
    }

    [Fact]
    public async Task WhenOrganisationSubmitsBeforeRerun_ShouldNotSendItsPendingReminder()
    {
        await using var fixture = await NotSubmittedReminderFixture.Create();
        fixture.State.AddOrganisation("GB-ENG", Person("first@example.test", "Approved Person"));
        var submitted = fixture.State.AddOrganisation("GB-NIR", Person("second@example.test", "Approved Person"));
        fixture.Sqs.FailPublicationAttempt = 2;

        await fixture.Scan();
        await WaitForAsync(async () => Assert.Single(await fixture.NotifyRequests()));
        fixture.State.RemoveOrganisation(submitted);

        var second = await fixture.Scan();
        await WaitForAsync(async () =>
        {
            Assert.Equal(2, fixture.ConfirmedCommands().Count);
            await fixture.AssertCommandQueueEmpty();
        });

        Assert.Equal(new NotSubmittedReminderScanResult(1, 1, 0, 0, null), second);
        var request = Assert.Single(await fixture.NotifyRequests());
        Assert.Equal("first@example.test", request.GetProperty("email_address").GetString());
        Assert.DoesNotContain(fixture.ConfirmedCommands(), command => command.EmailAddress == "second@example.test");
        await fixture.AssertAcceptedRecords(1);
    }

    private static OrganisationPerson Person(string email, string role) =>
        new(Guid.NewGuid(), "Synthetic", "Person", email, role);
}
