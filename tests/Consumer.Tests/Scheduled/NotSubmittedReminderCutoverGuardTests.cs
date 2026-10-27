using Defra.WasteObligations.Consumer.Delivery;
using Defra.WasteObligations.Consumer.Scheduled;
using Microsoft.Extensions.Options;
using Xunit;

namespace Defra.WasteObligations.Consumer.Tests.Scheduled;

public sealed class NotSubmittedReminderCutoverGuardTests
{
    private const string Cutover = "2026-09-01T00:00:00Z";

    [Theory]
    [InlineData("2026-12-01T10:00:00Z")]
    [InlineData("2026-12-08T09:59:59Z")]
    public void WhenWithinSendWindowAfterCutover_ShouldAllowScan(string now)
    {
        var guard = CreateGuard(now);

        Assert.Null(guard.GetSkipReason(2026));
    }

    [Theory]
    [InlineData("2026-03-01T10:00:00Z")]
    [InlineData("2026-12-01T09:59:59Z")]
    [InlineData("2026-12-08T10:00:00Z")]
    public void WhenOutsideSendWindow_ShouldSkipScan(string now)
    {
        var guard = CreateGuard(now);

        Assert.Equal(NotSubmittedReminderCutoverGuard.OutsideSendWindowReason, guard.GetSkipReason(2026));
    }

    [Fact]
    public void WhenDisabled_ShouldSkipScan()
    {
        var guard = CreateGuard("2026-12-01T10:00:00Z", enabled: false);

        Assert.Equal(NotSubmittedReminderCutoverGuard.DisabledReason, guard.GetSkipReason(2026));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-timestamp")]
    [InlineData("2026-12-01T10:00:00.001Z")]
    public void WhenCutoverIsMissingInvalidOrAfterOccurrence_ShouldSkipScan(string? cutover)
    {
        var guard = CreateGuard("2026-12-01T10:00:00Z", cutover: cutover);

        Assert.Equal(NotSubmittedReminderCutoverGuard.CutoverMisconfiguredReason, guard.GetSkipReason(2026));
    }

    [Fact]
    public void WhenOutsideSendWindowWithMissingCutover_ShouldReportWindowBeforeCutover()
    {
        var guard = CreateGuard("2026-06-01T10:00:00Z", cutover: null);

        Assert.Equal(NotSubmittedReminderCutoverGuard.OutsideSendWindowReason, guard.GetSkipReason(2026));
    }

    private static NotSubmittedReminderCutoverGuard CreateGuard(
        string now,
        bool enabled = true,
        string? cutover = Cutover
    )
    {
        var reminder = Options.Create(
            new NotSubmittedReminderOptions
            {
                Enabled = enabled,
                Month = 12,
                Day = 1,
                Hour = 10,
                SendWindow = TimeSpan.FromDays(7),
            }
        );
        var delivery = Options.Create(
            new NotificationCommandDeliveryOptions
            {
                QueueUrl = "https://example.test/000000000000/queue.fifo",
                EmailDeliveryCutoverUtc = cutover,
                EvidenceDigestSecret = "secret",
                RecipientLaneSecret = "secret",
            }
        );

        return new NotSubmittedReminderCutoverGuard(
            reminder,
            delivery,
            new NotSubmittedReminderOccurrence(reminder),
            new Clock(DateTimeOffset.Parse(now, System.Globalization.CultureInfo.InvariantCulture))
        );
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
