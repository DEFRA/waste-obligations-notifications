using Defra.WasteObligations.Consumer.Services.AccountBackend;
using Defra.WasteObligations.Consumer.Services.WasteOrganisations;

namespace Defra.WasteObligations.Consumer.Cancellation;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCancellationEmailProducer(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        services
            .AddOptions<CancellationOptions>()
            .Bind(configuration.GetSection(CancellationOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                options => options.HasValidTemplates,
                "Cancellation template mappings must be nonempty hyphenated UUIDs"
            )
            .ValidateOnStart();
        services.AddAccountBackendClient(configuration).AddWasteOrganisationsClient(configuration);
        services.AddTransient<CancellationEmailProducer>();

        return services;
    }
}
