using System.Text.Json;
using Defra.WasteObligations.Consumer.IntegrationTests.Helpers;
using Defra.WasteObligations.Consumer.Scheduled;
using Defra.WasteObligations.Consumer.Services.AccountBackend;

namespace Defra.WasteObligations.Consumer.IntegrationTests.Scenarios;

public sealed class NotSubmittedReminder2SendTests : IntegrationTestBase
{
    private const string FirstEnglishTemplate = "54acaa9f-93c4-4052-bd0d-f5bc9e122ce6";
    private const string EnglishTemplate = "458cd86f-861b-46ad-b67f-560a3c78ee94";
    private const string WelshTemplate = "5f9e4390-41d6-418a-99bd-d0035ba39bc2";

    [Fact]
    public async Task WhenMetStatusVaries_ShouldSendOnlyToMetProducersAndRetryUntilStatusIsKnown()
    {
        await using var fixture = await NotSubmittedReminderFixture.Create();
        var year = fixture.State.ObligationYear;
        fixture.State.AddOrganisation(
            "GB-ENG",
            true,
            Person("approved-met@example.test", "Approved Person"),
            Person("delegated-met@example.test", "Delegated Person")
        );
        fixture.State.AddOrganisation("GB-WLS", true, Person("welsh-met@example.test", "Approved Person"));
        fixture.State.AddOrganisation("GB-SCT", false, Person("not-met@example.test", "Approved Person"));
        var unknown = fixture.State.AddOrganisation(
            "GB-NIR",
            (bool?)null,
            Person("unknown@example.test", "Approved Person")
        );

        var first = await fixture.Scan(NotSubmittedReminderCampaign.Email2);
        await WaitForAsync(async () =>
        {
            Assert.Equal(3, (await fixture.NotifyRequests()).Length);
            await fixture.AssertAcceptedRecords(3);
            await fixture.AssertCommandQueueEmpty();
        });
        var open = await fixture.RunStatus(NotSubmittedReminderCampaign.Email2);

        Assert.Equal(new NotSubmittedReminderScanResult(4, 3, 2, 1, null), first);
        Assert.Equal(("in-progress", 1), (open.Status, open.RetryableFailures));
        var commands = fixture.ConfirmedCommands();
        Assert.All(commands, command => Assert.StartsWith($"not-submitted-reminder-2:{year}:", command.IdempotencyKey));
        var welsh = Assert.Single(commands, command => command.TemplateId == WelshTemplate);
        Assert.Equal("declaration-not-submitted-reminder-2-cy", welsh.NotificationType);
        Assert.Equal(2, commands.Count(command => command.TemplateId == EnglishTemplate));
        Assert.All(
            await fixture.NotifyRequests(),
            request =>
            {
                var personalisation = request.GetProperty("personalisation");
                Assert.Equal(year, personalisation.GetProperty("obligationYear").GetInt32());
                Assert.Equal(year + 1, personalisation.GetProperty("certYear").GetInt32());
                Assert.Equal(
                    NotSubmittedReminderFixture.ReportPackagingDataAccountUrl,
                    personalisation.GetProperty("reportPackagingDataAccountUrl").GetString()
                );
                Assert.False(personalisation.TryGetProperty("year", out _));
            }
        );

        fixture.State.SetRecyclingObligationsMet(unknown, true);
        var second = await fixture.Scan(NotSubmittedReminderCampaign.Email2);
        await WaitForAsync(async () =>
        {
            Assert.Equal(4, (await fixture.NotifyRequests()).Length);
            await fixture.AssertAcceptedRecords(4);
            await fixture.AssertCommandQueueEmpty();
        });

        Assert.Equal(new NotSubmittedReminderScanResult(4, 4, 1, 0, null), second);
        Assert.Equal("completed", (await fixture.RunStatus(NotSubmittedReminderCampaign.Email2)).Status);
        Assert.DoesNotContain(
            await fixture.NotifyRequests(),
            request => request.GetProperty("email_address").GetString() == "not-met@example.test"
        );
    }

    [Fact]
    public async Task WhenBothRemindersRunForTheSameProducer_ShouldSendEachReminderOnce()
    {
        await using var fixture = await NotSubmittedReminderFixture.Create();
        var organisationId = fixture.State.AddOrganisation(
            "GB-ENG",
            true,
            Person("approved@example.test", "Approved Person")
        );

        var first = await fixture.Scan();
        var second = await fixture.Scan(NotSubmittedReminderCampaign.Email2);
        await WaitForAsync(async () =>
        {
            Assert.Equal(2, (await fixture.NotifyRequests()).Length);
            await fixture.AssertAcceptedRecords(2);
            await fixture.AssertCommandQueueEmpty();
        });

        Assert.Equal(new NotSubmittedReminderScanResult(1, 1, 0, 0, null), first);
        Assert.Equal(new NotSubmittedReminderScanResult(1, 1, 0, 0, null), second);
        Assert.Equal(
            [FirstEnglishTemplate, EnglishTemplate],
            fixture.ConfirmedCommands().Select(command => command.TemplateId)
        );
        Assert.Equal(
            [
                $"not-submitted-reminder-1:{fixture.State.ObligationYear}:{organisationId:N}:",
                $"not-submitted-reminder-2:{fixture.State.ObligationYear}:{organisationId:N}:",
            ],
            fixture.ConfirmedCommands().Select(command => command.IdempotencyKey[..^64])
        );
        Assert.Equal("completed", (await fixture.RunStatus()).Status);
        Assert.Equal("completed", (await fixture.RunStatus(NotSubmittedReminderCampaign.Email2)).Status);
    }

    private static OrganisationPerson Person(string email, string role) =>
        new(Guid.NewGuid(), "Synthetic", "Person", email, role);
}
