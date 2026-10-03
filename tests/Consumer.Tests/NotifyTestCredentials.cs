namespace Defra.WasteObligations.Consumer.Tests;

// Synthetic credentials exist only in this test process and cannot authenticate to GOV.UK Notify.
internal static class NotifyTestCredentials
{
    internal static Guid ServiceId { get; } = Guid.NewGuid();

    internal static Guid SecretId { get; } = Guid.NewGuid();

    internal static string ApiKey => $"synthetic-fixture-{ServiceId:D}-{SecretId:D}";
}
