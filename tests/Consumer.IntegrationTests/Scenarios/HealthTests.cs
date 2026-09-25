namespace Defra.WasteObligations.Consumer.IntegrationTests.Scenarios;

public sealed class HealthTests : IntegrationTestBase
{
    [Fact]
    public async Task WhenConsumerQueueIsAvailable_HealthAllShouldBeHealthy()
    {
        using var client = CreateClient();
        using var response = await client.GetAsync("/health/all", TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();

        await VerifyJson(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }
}
