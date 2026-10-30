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
            2026,
            organisationId,
            actionTime,
            "declaration-not-submitted-reminder-en",
            "54acaa9f-93c4-4052-bd0d-f5bc9e122ce6",
            "Recipient.Example@domain.test",
            personalisation
        );

        Assert.StartsWith("not-submitted-reminder-1:2026:11111111111111111111111111111111:", command.IdempotencyKey);
        Assert.DoesNotContain("@", command.IdempotencyKey);
        Assert.True(command.IdempotencyKey.Length <= 128);
        Assert.Equal("recipient.example@domain.test", command.EmailAddress);
        Assert.Equal(actionTime, command.ActionOccurredAtUtc);
    }
}
