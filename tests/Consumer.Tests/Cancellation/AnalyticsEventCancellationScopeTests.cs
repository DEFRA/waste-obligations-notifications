using System.Text.Json;
using Defra.WasteObligations.Consumer.Consumers;

namespace Defra.WasteObligations.Consumer.Tests.Cancellation;

public sealed class AnalyticsEventCancellationScopeTests
{
    [Theory]
    [InlineData("Submitted", "Cancelled", "submission.amended", true)]
    [InlineData("Accepted", "Cancelled", "submission.amended", true)]
    [InlineData("Draft", "Cancelled", "submission.amended", false)]
    [InlineData(null, "Cancelled", "submission.amended", false)]
    [InlineData("Submitted", "Accepted", "submission.amended", false)]
    [InlineData("Submitted", "Cancelled", "submission.created", false)]
    [InlineData("submitted", "Cancelled", "submission.amended", false)]
    public void WhenParsingTransition_ShouldRequireExactTrigger(
        string? before,
        string after,
        string eventType,
        bool expected
    )
    {
        var body = JsonSerializer.Serialize(
            new
            {
                eventId = "event",
                entityId = "entity",
                entity = "compliance_declaration",
                eventType,
                before = new { status = before },
                after = new { status = after },
            }
        );

        var result = AnalyticsEvent.Parse(body);

        Assert.Equal(expected, result.IsCancellation);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("true")]
    [InlineData("123")]
    [InlineData("null")]
    [InlineData("{}")]
    public void WhenBeforeStatusIsNotAString_ShouldRemainOutOfScope(string status)
    {
        var body =
            "{\"eventId\":\"event\",\"entityId\":\"entity\",\"entity\":\"compliance_declaration\",\"eventType\":\"submission.amended\",\"before\":{\"status\":"
            + status
            + "},\"after\":{\"status\":\"Cancelled\"}}";

        Assert.False(AnalyticsEvent.Parse(body).IsCancellation);
    }
}
