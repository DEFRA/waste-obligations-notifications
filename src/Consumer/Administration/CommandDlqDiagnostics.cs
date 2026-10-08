using Defra.WasteObligations.Consumer.Delivery;
using Microsoft.Extensions.Options;

namespace Defra.WasteObligations.Consumer.Administration;

public sealed class CommandDlqDiagnostics(
    IOptions<NotificationCommandDeliveryOptions> delivery,
    ILogger<CommandDlqDiagnostics> logger
)
{
    public void InvalidCommand() =>
        logger.LogWarning("Command DLQ inspection classified an invalid or unsupported command.");

    public void Inspected(string classification, string notificationType)
    {
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation(
                "Command DLQ inspection classified {Classification} for {NotificationType}",
                classification,
                delivery.Value.GetDiagnosticNotificationType(notificationType)
            );
    }

    public void Redriven(string notificationType)
    {
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation(
                "Command DLQ redrive completed for {NotificationType}",
                delivery.Value.GetDiagnosticNotificationType(notificationType)
            );
    }

    public void Discarded(string notificationType)
    {
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation(
                "Command DLQ discard completed for {NotificationType}",
                delivery.Value.GetDiagnosticNotificationType(notificationType)
            );
    }

    public void InspectionFailed() => logger.LogError("Command DLQ inspection failed.");

    public void StatusFailed() => logger.LogError("Command DLQ status failed.");

    public void RedriveFailed() => logger.LogError("Command DLQ redrive failed.");

    public void DiscardFailed() => logger.LogError("Command DLQ discard failed.");
}
