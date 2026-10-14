using Defra.WasteObligations.Consumer.Consumers;
using Microsoft.Extensions.Options;

namespace Defra.WasteObligations.Consumer.Submission;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSubmittedEmailProducer(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SubmissionOptions>()
            .Bind(configuration.GetSection(SubmissionOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(options => options.HasValidTemplates, "Submission template mappings must be nonempty UUIDs")
            .Validate<IOptions<AnalyticsEventConsumerOptions>>((options, analytics) => !analytics.Value.ProcessingEnabled || options.HasReadAccess,
                "Submission service read address and credentials must be configured when analytics processing is enabled")
            .ValidateOnStart();
        services.AddHttpClient<IDeclarationReadClient, DeclarationReadClient>((provider, client) =>
        {
            var options = provider.GetRequiredService<IOptions<SubmissionOptions>>().Value;
            if (options.HasReadAccess)
                client.BaseAddress = new Uri(options.BaseAddress.TrimEnd('/') + "/");
            client.Timeout = Timeout.InfiniteTimeSpan;
        }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        }).RemoveAllLoggers();
        services.AddTransient<SubmittedEmailProducer>();

        return services;
    }
}
