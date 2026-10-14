
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
            .ValidateOnStart();

        services.AddTransient<SubmittedEmailProducer>();

        return services;
    }
}
