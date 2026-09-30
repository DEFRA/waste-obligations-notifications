using System.Text.Json;
using Defra.WasteObligations.Consumer.Commands;
using Defra.WasteObligations.Consumer.Delivery;

namespace Defra.WasteObligations.Consumer.Tests.Commands;

[Collection(nameof(UtcTimestampTimezoneTests))]
public sealed class UtcTimestampTimezoneTests
{
    [Theory]
    [InlineData("UTC", 0)]
    [InlineData("Europe/London", 1)]
    public void WhenHostTimezoneDiffers_ShouldRejectOffsetFreeCutoverAndCommand(string timezone, int offsetHours)
    {
        Assert.SkipWhen(
            OperatingSystem.IsWindows(),
            "TZ overrides exercise Linux deployment and macOS host timezones."
        );
        var originalTimezone = Environment.GetEnvironmentVariable("TZ");
        try
        {
            Environment.SetEnvironmentVariable("TZ", timezone);
            TimeZoneInfo.ClearCachedData();
            Assert.Equal(
                TimeSpan.FromHours(offsetHours),
                TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Unspecified))
            );
            const string timestamp = "2026-10-01T00:00:00";
            var options = new NotificationCommandDeliveryOptions
            {
                QueueUrl = "commands.fifo",
                EmailDeliveryCutoverUtc = timestamp,
                EvidenceDigestSecret = "test-evidence-secret",
                RecipientLaneSecret = "test-lane-secret",
            };

            Assert.False(options.TryReadCutover(out _));
            Assert.Throws<JsonException>(() =>
                NotificationCommandMessageReader.Read(
                    NotificationCommandMessageReaderTests.CreateMessage(JsonSerializer.Serialize(timestamp))
                )
            );
            Assert.True((options with { EmailDeliveryCutoverUtc = timestamp + "Z" }).TryReadCutover(out var cutover));
            Assert.Equal(TimeSpan.Zero, cutover.Offset);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TZ", originalTimezone);
            TimeZoneInfo.ClearCachedData();
        }
    }
}

[CollectionDefinition(nameof(UtcTimestampTimezoneTests), DisableParallelization = true)]
public sealed class UtcTimestampTimezoneCollection;
