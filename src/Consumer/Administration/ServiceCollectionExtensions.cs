using Defra.WasteObligations.Consumer.Delivery;
using Microsoft.Extensions.Options;

namespace Defra.WasteObligations.Consumer.Administration;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCommandDlqAdministration(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        services
            .AddOptions<CommandDlqAdministrationOptions>()
            .Configure(options =>
            {
                try
                {
                    configuration.GetSection(CommandDlqAdministrationOptions.SectionName).Bind(options);
                }
                catch (Exception)
                {
                    throw new OptionsValidationException(
                        Options.DefaultName,
                        typeof(CommandDlqAdministrationOptions),
                        ["Command DLQ administration configuration is invalid."]
                    );
                }
            })
            .ValidateDataAnnotations()
            .Validate(
                options => options.HasValidSelectionBudget,
                "Command DLQ selection lifetime must exceed its positive dependency timeout and be below 300 seconds."
            )
            .Validate(
                options => IsQueueConfigured(options.QueueUrl),
                "A FIFO DLQ URL must be configured for command DLQ administration."
            )
            .Validate(
                options =>
                    IsQueueConfigured(configuration[$"{NotificationCommandDeliveryOptions.SectionName}:QueueUrl"]),
                "A FIFO command queue URL must be configured for command DLQ administration."
            )
            .Validate(
                options =>
                    HasSeparateQueues(
                        options.QueueUrl,
                        configuration[$"{NotificationCommandDeliveryOptions.SectionName}:QueueUrl"]
                    ),
                "Command and DLQ queue URLs must identify different queues for command DLQ administration."
            )
            .ValidateOnStart();
        services.AddSingleton<CommandDlqSelectionTokens>();
        services.AddSingleton<CommandDlqDiagnostics>();
        services.AddSingleton<CommandDlqInspector>();
        services.AddSingleton<CommandDlqRedriver>();
        services.AddSingleton<CommandDlqDiscarder>();
        services.AddSingleton<CommandDlqVerificationCommandCreator>();
        services.AddSingleton<CommandDlqQueueOperations>();

        return services;
    }

    private static bool IsQueueConfigured(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https"
        && uri.AbsolutePath.EndsWith(".fifo", StringComparison.Ordinal);

    private static bool HasSeparateQueues(string dlq, string? commandQueue) =>
        Uri.TryCreate(dlq, UriKind.Absolute, out var dlqUri)
        && Uri.TryCreate(commandQueue, UriKind.Absolute, out var commandUri)
        && Uri.Compare(
            dlqUri,
            commandUri,
            UriComponents.SchemeAndServer | UriComponents.Path,
            UriFormat.SafeUnescaped,
            StringComparison.Ordinal
        ) != 0;
}
