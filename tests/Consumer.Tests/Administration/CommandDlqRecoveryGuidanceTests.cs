using System.Reflection;
using Defra.WasteObligations.Consumer.Administration;
using Defra.WasteObligations.Consumer.Delivery;

namespace Defra.WasteObligations.Consumer.Tests.Administration;

public sealed class CommandDlqRecoveryGuidanceTests
{
    [Fact]
    public void WhenClassificationsAreDefined_ShouldProvideSpecificGuidanceForEveryClassification()
    {
        var expected = new Dictionary<string, string>
        {
            [NotificationDeliveryClassifications.DeliveryAccepted] = "Notify acceptance is recorded",
            [NotificationDeliveryClassifications.ActiveClaim] = "An unexpired lease owns the attempt",
            [NotificationDeliveryClassifications.ExpiredClaim] = "The lease has expired",
            [NotificationDeliveryClassifications.Unrecorded] = "No matching delivery record was found",
            [NotificationDeliveryClassifications.DeliverySuppressed] = "Confirm the handover policy",
            [NotificationDeliveryClassifications.DeliveryAbandoned] = "Confirm the previous operator decision",
            [NotificationDeliveryClassifications.ImmutableConflict] = "different immutable email details",
            [NotificationDeliveryClassifications.UnknownDeliveryState] = "The stored outcome is unrecognised",
            [NotificationDeliveryClassifications.InvalidOrUnsupportedCommand] = "Command inputs cannot be trusted",
        };
        var classifications = typeof(NotificationDeliveryClassifications)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToArray();

        Assert.Equal(expected.Keys.Order(), classifications.Order());
        var fallback = CommandDlqRecoveryGuidance.For("future-undefined-classification");
        foreach (var classification in classifications)
        {
            var guidance = CommandDlqRecoveryGuidance.For(classification);
            Assert.NotEqual(fallback, guidance);
            Assert.Contains(expected[classification], guidance, StringComparison.Ordinal);
        }
        Assert.Empty(CommandDlqRecoveryGuidance.Actions("future-undefined-classification"));
    }
}
