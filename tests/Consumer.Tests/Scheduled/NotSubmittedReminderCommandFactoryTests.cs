using Defra.WasteObligations.Consumer.Scheduled;
using Xunit;

namespace Defra.WasteObligations.Consumer.Tests.Scheduled;

public sealed class NotSubmittedReminderCommandFactoryTests
{
    [Fact]
    public void Create_ShouldUseStableIdempotencyKeyWithoutRawEmail()
    {
        var organisationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var actionTime = new DateTimeOffset(2026, 12, 1, 10, 0, 0, TimeSpan.Zero);
        var personalisation = System.Text.Json.JsonSerializer.SerializeToElement(
            new
            {
                firstName = "A",
                lastName = "B",
                year = 2027,
                regulatorEmail = "ea@example.test",
            }
        );

        var command = NotSubmittedReminderCommandFactory.Create(
            NotSubmittedReminderCampaign.Email1,
            2026,
            organisationId,
            actionTime,
            new NotSubmittedReminderMessage(
                "declaration-not-submitted-reminder-en",
                "54acaa9f-93c4-4052-bd0d-f5bc9e122ce6",
                personalisation
            ),
            "Recipient.Example@domain.test"
        );

        Assert.StartsWith("not-submitted-reminder-1:2026:11111111111111111111111111111111:", command.IdempotencyKey);
        Assert.DoesNotContain("@", command.IdempotencyKey);
        Assert.True(command.IdempotencyKey.Length <= 128);
        Assert.Equal("recipient.example@domain.test", command.EmailAddress);
        Assert.Equal(actionTime, command.ActionOccurredAtUtc);
    }

    [Fact]
    public void Create_WhenSecondReminder_ShouldUseSeparateKeyWithinLengthLimit()
    {
        var organisationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var personalisation = System.Text.Json.JsonSerializer.SerializeToElement(new { firstName = "A" });

        var command = NotSubmittedReminderCommandFactory.Create(
            NotSubmittedReminderCampaign.Email2,
            2026,
            organisationId,
            new DateTimeOffset(2027, 1, 1, 10, 0, 0, TimeSpan.Zero),
            new NotSubmittedReminderMessage(
                "declaration-not-submitted-reminder-2-en",
                "458cd86f-861b-46ad-b67f-560a3c78ee94",
                personalisation
            ),
            "recipient@domain.test"
        );

        Assert.StartsWith("not-submitted-reminder-2:2026:11111111111111111111111111111111:", command.IdempotencyKey);
        Assert.Equal(127, command.IdempotencyKey.Length);
    }
}
