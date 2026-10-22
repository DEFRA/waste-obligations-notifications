using Microsoft.Extensions.Configuration;

namespace Defra.WasteObligations.Consumer.IntegrationTests.Helpers;

public static class NotifyPreviewSettings
{
    public static (IConfiguration Configuration, string ApiKey) Load()
    {
        var root = RepositoryRoot();
        var key = Environment.GetEnvironmentVariable("GOVUKNOTIFY_APIKEY") ?? LocalKey(root);
        if (string.IsNullOrWhiteSpace(key))
        {
            if (Environment.GetEnvironmentVariable("NOTIFY_PREVIEWS_REQUIRED") == "1")
                Assert.Fail("Required Notify preview credential is missing.");
            Assert.Skip("Notify previews were not executed: GOVUKNOTIFY_APIKEY is missing.");
        }
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(root, "src/Consumer/appsettings.json"))
            .AddEnvironmentVariables()
            .Build();

        return (configuration, key);
    }

    private static string RepositoryRoot()
    {
        for (
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            directory is not null;
            directory = directory.Parent
        )
            if (File.Exists(Path.Combine(directory.FullName, "waste-obligations-notifications.slnx")))
                return directory.FullName;
        throw new InvalidOperationException("Repository root was not found for Notify preview configuration.");
    }

    private static string? LocalKey(string root)
    {
        var path = Path.Combine(root, ".env");
        if (!File.Exists(path))
            return null;
        foreach (var line in File.ReadLines(path))
        {
            var parts = line.Trim().Split('=', 2);
            if (parts.Length != 2 || parts[0].Trim() != "GovukNotify_ApiKey")
                continue;
            var value = parts[1].Trim();
            if (value.Length >= 2 && value[0] == value[^1] && value[0] is '\'' or '"')
                value = value[1..^1];

            return value;
        }

        return null;
    }
}
