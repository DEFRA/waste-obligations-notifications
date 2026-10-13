using Microsoft.Extensions.Options;

namespace Defra.WasteObligations.Consumer.Submission;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSubmissionEmails(this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddOptions<SubmissionEmailOptions>()
            .Bind(configuration.GetSection(SubmissionEmailOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(options => options.HasValidTemplates, "Submission templates must be UUIDs")
            .Validate(
                options =>
                    !configuration.GetValue<bool>("AnalyticsEventConsumer:ProcessingEnabled")
                    || options.HasReadConfiguration,
                "Enabled analytics requires declaration read API and credentials"
            )
            .ValidateOnStart();
        services.AddSingleton<SubmissionCommandMapper>();
        services
            .AddHttpClient<IDeclarationClient, DeclarationClient>(
                (provider, client) =>
                {
                    var options = provider.GetRequiredService<IOptions<SubmissionEmailOptions>>().Value;
                    if (options.HasReadConfiguration)
                        client.BaseAddress = new Uri(options.ApiBaseAddress.TrimEnd('/') + "/");
                    client.Timeout = Timeout.InfiniteTimeSpan;
                }
            )
            .ConfigurePrimaryHttpMessageHandler(() =>
                new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(2) }
            )
            .RemoveAllLoggers();

        return services;
    }
}
