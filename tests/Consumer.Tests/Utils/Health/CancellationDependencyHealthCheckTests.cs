using Defra.WasteObligations.Consumer.Consumers;
using Defra.WasteObligations.Consumer.Services.AccountBackend;
using Defra.WasteObligations.Consumer.Services.WasteOrganisations;
using Defra.WasteObligations.Consumer.Utils.Health;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Defra.WasteObligations.Consumer.Tests.Utils.Health;

public sealed class CancellationDependencyHealthCheckTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WhenDependencyFails_ShouldExposeOnlyFixedFailure(bool account)
    {
        var accountClient = Substitute.For<IAccountBackendClient>();
        var organisationClient = Substitute.For<IWasteOrganisationsClient>();
        accountClient
            .CheckHealth(Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new HttpRequestException("private@example.test token")));
        organisationClient
            .CheckHealth(Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new HttpRequestException("private@example.test token")));
        var options = Options.Create(
            new AnalyticsEventConsumerOptions { QueueUrl = "http://localhost/analytics", ProcessingEnabled = true }
        );
        IHealthCheck check = account
            ? new AccountBackendHealthCheck(options, accountClient)
            : new WasteOrganisationsHealthCheck(options, organisationClient);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal(
            account ? "AccountBackend service unavailable" : "WasteOrganisations service unavailable",
            result.Description
        );
        Assert.Null(result.Exception);
        Assert.Empty(result.Data);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WhenAuthenticatedDependencyIsAvailable_ShouldReportHealthy(bool account)
    {
        var accountClient = Substitute.For<IAccountBackendClient>();
        var organisationClient = Substitute.For<IWasteOrganisationsClient>();
        var options = Options.Create(
            new AnalyticsEventConsumerOptions { QueueUrl = "http://sqs.test/analytics", ProcessingEnabled = true }
        );
        IHealthCheck check = account
            ? new AccountBackendHealthCheck(options, accountClient)
            : new WasteOrganisationsHealthCheck(options, organisationClient);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Null(result.Exception);

        if (account)
            await accountClient.Received(1).CheckHealth(Arg.Is<CancellationToken>(token => token.CanBeCanceled));
        else
            await organisationClient.Received(1).CheckHealth(Arg.Is<CancellationToken>(token => token.CanBeCanceled));
    }

    [Fact]
    public async Task WhenAnalyticsIsDisabled_ShouldSkipBothDependencies()
    {
        var accountClient = Substitute.For<IAccountBackendClient>();
        var organisationClient = Substitute.For<IWasteOrganisationsClient>();
        var options = Options.Create(
            new AnalyticsEventConsumerOptions { QueueUrl = "http://localhost/analytics", ProcessingEnabled = false }
        );

        Assert.Equal(
            HealthStatus.Healthy,
            (
                await new AccountBackendHealthCheck(options, accountClient).CheckHealthAsync(
                    new HealthCheckContext(),
                    TestContext.Current.CancellationToken
                )
            ).Status
        );
        Assert.Equal(
            HealthStatus.Healthy,
            (
                await new WasteOrganisationsHealthCheck(options, organisationClient).CheckHealthAsync(
                    new HealthCheckContext(),
                    TestContext.Current.CancellationToken
                )
            ).Status
        );

        await accountClient.DidNotReceive().CheckHealth(Arg.Any<CancellationToken>());
        await organisationClient.DidNotReceive().CheckHealth(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WhenDependencyHangs_ShouldFinishAtItsOwnDeadline(bool account)
    {
        var accountClient = Substitute.For<IAccountBackendClient>();
        var organisationClient = Substitute.For<IWasteOrganisationsClient>();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken dependencyToken = default;
        accountClient
            .CheckHealth(Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                dependencyToken = call.Arg<CancellationToken>();

                return pending.Task;
            });
        organisationClient
            .CheckHealth(Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                dependencyToken = call.Arg<CancellationToken>();

                return pending.Task;
            });
        var clock = Substitute.For<TimeProvider>();
        TimerCallback? expire = null;
        object? timerState = null;
        clock
            .CreateTimer(
                Arg.Any<TimerCallback>(),
                Arg.Any<object?>(),
                TimeSpan.FromSeconds(10),
                Timeout.InfiniteTimeSpan
            )
            .Returns(call =>
            {
                expire = call.Arg<TimerCallback>();
                timerState = call[1];

                return Substitute.For<ITimer>();
            });
        var options = Options.Create(
            new AnalyticsEventConsumerOptions { QueueUrl = "http://localhost/analytics", ProcessingEnabled = true }
        );
        IHealthCheck check = account
            ? new AccountBackendHealthCheck(options, accountClient, clock)
            : new WasteOrganisationsHealthCheck(options, organisationClient, clock);

        try
        {
            var resultTask = check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);
            Assert.False(resultTask.IsCompleted);
            Assert.NotNull(expire);

            expire(timerState);
            var result = await resultTask.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

            Assert.Equal(HealthStatus.Unhealthy, result.Status);
            Assert.True(dependencyToken.IsCancellationRequested);
            Assert.Equal(
                account ? "AccountBackend service unavailable" : "WasteOrganisations service unavailable",
                result.Description
            );
            Assert.Null(result.Exception);
        }
        finally
        {
            pending.TrySetResult();
        }
    }

    [Fact]
    public async Task WhenDependencyIgnoresCancellation_ShouldStillFinishDiagnostic()
    {
        var client = Substitute.For<IAccountBackendClient>();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.CheckHealth(Arg.Any<CancellationToken>()).Returns(pending.Task);
        using var cancellation = new CancellationTokenSource();
        var check = new AccountBackendHealthCheck(
            Options.Create(
                new AnalyticsEventConsumerOptions { QueueUrl = "http://localhost/analytics", ProcessingEnabled = true }
            ),
            client
        );
        var resultTask = check.CheckHealthAsync(new HealthCheckContext(), cancellation.Token);

        await cancellation.CancelAsync();
        var result = await resultTask.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        pending.SetResult();
    }
}
