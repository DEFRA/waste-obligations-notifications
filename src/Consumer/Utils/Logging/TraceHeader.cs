using System.ComponentModel.DataAnnotations;

namespace Defra.WasteObligations.Consumer.Utils.Logging;

public sealed class TraceHeader
{
    [ConfigurationKeyName("TraceHeader")]
    [Required]
    public required string Name { get; set; }
}
