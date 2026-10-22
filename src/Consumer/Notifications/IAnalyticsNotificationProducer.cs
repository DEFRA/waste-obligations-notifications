using Defra.WasteObligations.Consumer.Commands;
using Defra.WasteObligations.Consumer.Consumers;

namespace Defra.WasteObligations.Consumer.Notifications;

public interface IAnalyticsNotificationProducer
{
    bool CanHandle(AnalyticsEvent analyticsEvent);

    Task<IReadOnlyList<NotificationCommand>> Create(
        Guid organisationId,
        string declarationId,
        int eventVersion,
        CancellationToken cancellationToken
    );
}
