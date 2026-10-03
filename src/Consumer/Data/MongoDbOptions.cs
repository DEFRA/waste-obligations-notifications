using System.ComponentModel.DataAnnotations;

namespace Defra.WasteObligations.Consumer.Data;

public sealed record MongoDbOptions
{
    public const string SectionName = "Mongo";

    [Required]
    public required string DatabaseUri { get; init; }

    [Required]
    public required string DatabaseName { get; init; }
}
