using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Amazon.CloudWatch.EMF.Model;
using Defra.WasteObligations.Consumer.Delivery;
using Defra.WasteObligations.Consumer.Utils.Metrics;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CommandMetrics = Defra.WasteObligations.Consumer.Utils.Metrics.Metrics;

namespace Defra.WasteObligations.Consumer.Tests.Utils.Metrics;

public sealed class MetricsExporterTests
{
    private static readonly string[] s_expectedOutcomes =
    [
        "active-claim",
        "conflict",
        "delivery-abandoned",
        "delivery-accepted",
        "delivery-suppressed",
        "failure",
        "terminal-duplicate",
        "unavailable",
    ];
    private static readonly double[] s_receivedValues = [1d, 9d];
    private static readonly string[] s_guardedProperties = ["NotificationCommandOutcome", "Service", "_aws"];
    private const string ProbeEnvironment = "NOTIFICATIONS_EMF_TEST_PROBE";
    private const string PrivateValue = "private-key-recipient@example.invalid";

    [Theory]
    [InlineData("enabled")]
    [InlineData("fallback")]
    [InlineData("disabled")]
    public async Task WhenCommandMetricsAreRecorded_ShouldEmitRealSdkLocalDocumentsWithSafeDimensions(string mode)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(typeof(MetricsExporterTests).Assembly.Location);
        start.ArgumentList.Add("--filter-method");
        start.ArgumentList.Add("*WhenExporterProbeRunsInItsOwnProcess*");
        foreach (
            var key in start
                .Environment.Keys.Where(key => key.StartsWith("AWS_EMF_", StringComparison.Ordinal))
                .ToArray()
        )
            start.Environment.Remove(key);
        start.Environment[ProbeEnvironment] = mode;
        start.Environment["AWS_EMF_ENVIRONMENT"] = "Local";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var errors = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
        var text = await output + await errors;
        Assert.Equal(0, process.ExitCode);
        Assert.DoesNotContain(PrivateValue, text, StringComparison.Ordinal);
        var documents = text.Split('\n')
            .Where(line => line.TrimStart().StartsWith('{'))
            .Select(line => JsonSerializer.Deserialize<JsonElement>(line))
            .ToArray();
        if (mode == "disabled")
        {
            Assert.Empty(documents);
            return;
        }
        Assert.Contains("Notification command EMF export failed (ArgumentException).", text, StringComparison.Ordinal);
        Assert.Equal(17, documents.Length);
        var ns = mode == "fallback" ? CommandMetrics.MeterName : "notifications-test";
        foreach (var document in documents)
        {
            if (document.TryGetProperty("NotificationType", out _))
                AssertDocument(document, ns);
            else
                AssertGuardedDocument(document, ns);
        }
        Assert.Equal(
            s_expectedOutcomes,
            documents
                .Where(document =>
                    document.TryGetProperty("NotificationCommandOutcome", out _)
                    && document.TryGetProperty("Outcome", out _)
                )
                .Select(document => document.GetProperty("Outcome").GetString())
                .Order()
        );
        Assert.Equal(
            s_receivedValues,
            documents
                .Where(document => document.TryGetProperty("NotificationCommandReceived", out _))
                .Select(document => document.GetProperty("NotificationCommandReceived").GetDouble())
                .Order()
        );
        Assert.Equal(
            8,
            documents
                .Select(document =>
                    document
                        .GetProperty("_aws")
                        .GetProperty("CloudWatchMetrics")[0]
                        .GetProperty("Metrics")[0]
                        .GetProperty("Name")
                        .GetString()
                )
                .Distinct()
                .Count()
        );
    }

    private static void AssertDocument(JsonElement root, string ns)
    {
        var expected = new Dictionary<string, (double Value, string Unit, string? Outcome)>
        {
            ["NotificationCommandReceived"] = (1, "Count", null),
            ["NotificationCommandOutcome"] = (1, "Count", "delivery-accepted"),
            ["NotificationCommandLeaseClaim"] = (1, "Count", "claimed"),
            ["NotificationCommandLeaseClaimDuration"] = (125.5, "Milliseconds", "claimed"),
            ["NotificationCommandNotifySendAccepted"] = (1, "Count", null),
            ["NotificationCommandNotifySendFailure"] = (1, "Count", null),
            ["NotificationCommandNotifySendDuration"] = (75.25, "Milliseconds", null),
            ["NotificationCommandDuplicateSuppressed"] = (1, "Count", null),
        };
        var directive = Assert.Single(root.GetProperty("_aws").GetProperty("CloudWatchMetrics").EnumerateArray());
        var metric = Assert.Single(directive.GetProperty("Metrics").EnumerateArray());
        var name = metric.GetProperty("Name").GetString()!;
        var (value, unit, outcome) = expected[name];
        Assert.Equal(ns, directive.GetProperty("Namespace").GetString());
        Assert.Equal(unit, metric.GetProperty("Unit").GetString());
        if (name == "NotificationCommandReceived")
            Assert.Contains(root.GetProperty(name).GetDouble(), s_receivedValues);
        else
            Assert.Equal(value, root.GetProperty(name).GetDouble());
        Assert.Equal("waste-obligations-notifications", root.GetProperty("Service").GetString());
        Assert.Equal("submitted", root.GetProperty("NotificationType").GetString());
        if (name == "NotificationCommandOutcome")
            outcome = root.GetProperty("Outcome").GetString();
        string[] dimensions = outcome is null
            ? ["NotificationType", "Service"]
            : ["NotificationType", "Outcome", "Service"];
        Assert.Equal(
            dimensions,
            Assert
                .Single(directive.GetProperty("Dimensions").EnumerateArray())
                .EnumerateArray()
                .Select(dimension => dimension.GetString())
                .Order()
        );
        Assert.Equal(
            dimensions.Concat(["_aws", name]).Order(),
            root.EnumerateObject().Select(property => property.Name).Order()
        );
        if (outcome is not null)
            Assert.Equal(outcome, root.GetProperty("Outcome").GetString());
    }

    private static void AssertGuardedDocument(JsonElement root, string ns)
    {
        Assert.Equal(s_guardedProperties.Order(), root.EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal(1d, root.GetProperty("NotificationCommandOutcome").GetDouble());
        Assert.Equal("waste-obligations-notifications", root.GetProperty("Service").GetString());
        var directive = Assert.Single(root.GetProperty("_aws").GetProperty("CloudWatchMetrics").EnumerateArray());
        Assert.Equal(ns, directive.GetProperty("Namespace").GetString());
        Assert.Equal(
            "Service",
            Assert
                .Single(Assert.Single(directive.GetProperty("Dimensions").EnumerateArray()).EnumerateArray())
                .GetString()
        );
        var metric = Assert.Single(directive.GetProperty("Metrics").EnumerateArray());
        Assert.Equal("NotificationCommandOutcome", metric.GetProperty("Name").GetString());
        Assert.Equal("Count", metric.GetProperty("Unit").GetString());
    }

    [Fact]
    public async Task WhenExporterProbeRunsInItsOwnProcess()
    {
        var mode = Environment.GetEnvironmentVariable(ProbeEnvironment);
        if (mode is not ("enabled" or "fallback" or "disabled"))
            return;
        Assert.Equal("Local", Environment.GetEnvironmentVariable("AWS_EMF_ENVIRONMENT"));
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders().AddProvider(new ProbeLoggerProvider());
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["AWS_EMF_ENABLED"] = (mode != "disabled").ToString(),
                ["AWS_EMF_NAMESPACE"] = mode == "fallback" ? null : "notifications-test",
            }
        );
        builder.Services.AddNotificationCommandMetrics().AddNotificationCommandEmfExport(builder.Configuration);
        await using var app = builder.Build();
        app.UseNotificationCommandMetrics();
        var metrics = app.Services.GetRequiredService<INotificationCommandMetrics>();
        metrics.RecordReceived("submitted");
        metrics.RecordOutcome("submitted", "delivery-accepted");
        metrics.RecordClaim("submitted", DeliveryClaimResult.Claimed, 125.5);
        metrics.RecordSendAccepted("submitted");
        metrics.RecordSendFailure("submitted");
        metrics.RecordSendDuration("submitted", 75.25);
        metrics.RecordDuplicate("submitted");
        foreach (
            var outcome in new[]
            {
                "delivery-suppressed",
                "delivery-abandoned",
                "terminal-duplicate",
                "conflict",
                "active-claim",
                "unavailable",
                "failure",
            }
        )
            metrics.RecordOutcome("submitted", outcome);
        metrics.RecordOutcome(PrivateValue, PrivateValue);
        using var meter = new Meter(CommandMetrics.MeterName);
        meter
            .CreateCounter<long>("NotificationCommandReceived", nameof(Unit.COUNT))
            .Add(
                9,
                new KeyValuePair<string, object?>("NotificationType", "submitted"),
                new KeyValuePair<string, object?>("Service", PrivateValue),
                new KeyValuePair<string, object?>("PrivateData", PrivateValue)
            );
        meter.CreateCounter<long>("NotificationCommandReceived", PrivateValue).Add(1);
        meter.CreateCounter<long>(PrivateValue, nameof(Unit.COUNT)).Add(1);
        using var otherMeter = new Meter("unrelated-meter");
        otherMeter.CreateCounter<long>("NotificationCommandReceived", nameof(Unit.COUNT)).Add(1);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("set-automatically-when-deployed")]
    [InlineData("private namespace@example.invalid")]
    public async Task WhenEnabledNamespaceIsInvalid_ShouldFailWithoutExposingItsValue(string? value)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["AWS_EMF_NAMESPACE"] = value,
                ["AWS_EMF_ENABLED"] = "true",
                ["AWS_EMF_ENVIRONMENT"] = "Agent",
            }
        );
        builder.Services.AddNotificationCommandEmfExport(builder.Configuration);
        await using var app = builder.Build();
        var error = Assert.Throws<OptionsValidationException>(() => app.UseNotificationCommandMetrics());
        Assert.Equal(
            "AWS_EMF_NAMESPACE must be configured when AWS_EMF_ENABLED is true unless AWS_EMF_ENVIRONMENT is Local",
            Assert.Single(error.Failures)
        );
        if (value is not null)
            Assert.DoesNotContain(value, error.Message, StringComparison.Ordinal);
    }

    private sealed class ProbeLoggerProvider : ILoggerProvider, ILogger
    {
        public ILogger CreateLogger(string categoryName) => this;

        public bool IsEnabled(LogLevel logLevel) => true;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public void Dispose() { }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => Console.WriteLine(formatter(state, exception));
    }
}
