using Defra.WasteObligations.Consumer.Consumers;
using Microsoft.Extensions.Options;

namespace Defra.WasteObligations.Consumer.Submission;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSubmittedEmailProducer(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        services
            .AddOptions<SubmissionOptions>()
            .Bind(configuration.GetSection(SubmissionOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                options => options.HasValidTemplates,
                "Submission template mappings must be nonempty hyphenated UUIDs"
            )
            .Validate<IOptions<AnalyticsEventConsumerOptions>>(
                (options, analytics) => !analytics.Value.ProcessingEnabled || options.HasReadAccess,
                "Submission read access requires HTTPS or loopback HTTP and configured credentials when analytics processing is enabled"
            )
            .ValidateOnStart();
        services
            .AddHttpClient<IDeclarationReadClient, DeclarationReadClient>(
                (provider, client) =>
                {
                    var options = provider.GetRequiredService<IOptions<SubmissionOptions>>().Value;
                    if (options.HasReadAccess)
                    {
                        var address = new UriBuilder(options.BaseAddress);
                        if (!address.Path.EndsWith('/'))
                            address.Path += "/";
                        client.BaseAddress = address.Uri;
                    }
                    client.Timeout = Timeout.InfiniteTimeSpan;
                }
            )
            .ConfigurePrimaryHttpMessageHandler(() =>
                new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(2) }
            )
            .RemoveAllLoggers();
        services.AddTransient<SubmittedEmailProducer>();

        return services;
    }
}
