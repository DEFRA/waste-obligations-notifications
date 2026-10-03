using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using Defra.WasteObligations.Consumer.Authentication;
using Defra.WasteObligations.Consumer.Delivery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using AuthClaims = Defra.WasteObligations.Consumer.Authentication.Claims;

namespace Defra.WasteObligations.Consumer.Tests.Authentication;

public sealed class BasicAuthenticationTests
{
    private const string Secret = "private-päss:secret";

    [Theory]
    [InlineData("missing", 401)]
    [InlineData("malformed", 401)]
    [InlineData("syntax", 401)]
    [InlineData("base64", 401)]
    [InlineData("utf8", 401)]
    [InlineData("empty-client", 401)]
    [InlineData("empty-secret", 401)]
    [InlineData("missing-colon", 401)]
    [InlineData("unknown-client", 401)]
    [InlineData("wrong-secret", 401)]
    [InlineData("same-length-secret", 401)]
    [InlineData("short-secret", 401)]
    [InlineData("long-secret", 401)]
    [InlineData("normalized-secret", 401)]
    [InlineData("multiple", 401)]
    [InlineData("bearer", 401)]
    [InlineData("oauth", 401)]
    [InlineData("read", 403)]
    [InlineData("write", 403)]
    public async Task WhenCredentialsAreNotAnAuthenticatedBasicAdmin_ShouldDenyWithoutDependencyEffects(
        string condition,
        int statusCode
    )
    {
        await using var fixture = await AuthenticationFixture.Create();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/authentication-probe");
        var headers = condition switch
        {
            "missing" => Array.Empty<string>(),
            "malformed" => ["Basic"],
            "syntax" => ["Basic: private-header"],
            "base64" => ["Basic private-invalid-base64"],
            "utf8" => ["Basic /w=="],
            "empty-client" => [Basic($":{Secret}")],
            "empty-secret" => [Basic("admin:")],
            "missing-colon" => [Basic("admin")],
            "unknown-client" => [Basic($"unknown:{Secret}")],
            "wrong-secret" => [Basic("admin:wrong-private-secret")],
            "same-length-secret" => [Basic("admin:private-päss:secreu")],
            "short-secret" => [Basic($"admin:{Secret[..^1]}")],
            "long-secret" => [Basic($"admin:{Secret}x")],
            "normalized-secret" => [Basic("admin:private-pa\u0308ss:secret")],
            "multiple" => [Basic($"admin:{Secret}"), Basic($"admin:{Secret}")],
            "bearer" => [$"Bearer {Convert.ToBase64String(Encoding.UTF8.GetBytes($"admin:{Secret}"))}"],
            _ => [Basic($"{condition}:{Secret}")],
        };
        if (headers.Length > 0)
            request.Headers.TryAddWithoutValidation("Authorization", headers);

        using var response = await fixture.Client.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal((HttpStatusCode)statusCode, response.StatusCode);
        Assert.Empty(fixture.Sqs.ReceivedCalls());
        Assert.Empty(fixture.Store.ReceivedCalls());
        Assert.Equal(0, fixture.EndpointCalls);
        Assert.DoesNotContain(Secret, body, StringComparison.Ordinal);
        Assert.DoesNotContain("wrong-private-secret", body, StringComparison.Ordinal);
        Assert.All(fixture.Logs.Messages, message => Assert.DoesNotContain(Secret, message, StringComparison.Ordinal));
        Assert.All(
            fixture.Logs.Messages,
            message => Assert.DoesNotContain("wrong-private-secret", message, StringComparison.Ordinal)
        );
        foreach (var header in headers.Where(header => header != "Basic"))
        {
            Assert.DoesNotContain(header, body, StringComparison.Ordinal);
            Assert.All(
                fixture.Logs.Messages,
                message => Assert.DoesNotContain(header, message, StringComparison.Ordinal)
            );
        }
    }

    [Fact]
    public async Task WhenUtf8AdminCredentialsContainAColonInSecret_ShouldAuthenticateWithSourceClaims()
    {
        await using var fixture = await AuthenticationFixture.Create();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/authentication-probe");
        request.Headers.TryAddWithoutValidation("Authorization", Basic($"admin:{Secret}"));

        using var response = await fixture.Client.SendAsync(request, TestContext.Current.CancellationToken);
        using var body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("admin", body.RootElement.GetProperty("name").GetString());
        Assert.Equal("admin", body.RootElement.GetProperty("clientId").GetString());
        Assert.Equal("Basic", body.RootElement.GetProperty("authenticationType").GetString());
        Assert.Equal(
            ["admin", "read"],
            body.RootElement.GetProperty("scopes").EnumerateArray().Select(value => value.GetString())
        );
        Assert.Equal(1, fixture.EndpointCalls);
        await fixture
            .Sqs.Received(1)
            .ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>());
        Assert.DoesNotContain(Secret, body.RootElement.GetRawText(), StringComparison.Ordinal);
        Assert.All(fixture.Logs.Messages, message => Assert.DoesNotContain(Secret, message, StringComparison.Ordinal));
    }

    [Fact]
    public async Task WhenConfiguredSecretIsInvalidUtf8_ShouldDenyWithoutReplacingInvalidText()
    {
        await using var fixture = await AuthenticationFixture.Create("\ud800-private-secret");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/authentication-probe");
        request.Headers.TryAddWithoutValidation("Authorization", Basic("admin:\ufffd-private-secret"));

        using var response = await fixture.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, fixture.EndpointCalls);
        Assert.Empty(fixture.Sqs.ReceivedCalls());
        Assert.Empty(fixture.Store.ReceivedCalls());
        Assert.DoesNotContain(
            "private-secret",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            StringComparison.Ordinal
        );
        Assert.All(
            fixture.Logs.Messages,
            message => Assert.DoesNotContain("private-secret", message, StringComparison.Ordinal)
        );
    }

    private static string Basic(string credentials) =>
        $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials))}";

    // This route exercises the genuine ASP.NET authentication/authorization boundary; product inspection is tested separately.
    internal sealed class AuthenticationFixture(
        WebApplication application,
        IAmazonSQS sqs,
        INotificationDeliveryRecordStore store,
        RecordingLogs logs
    ) : IAsyncDisposable
    {
        private HttpClient? _client;
        public HttpClient Client =>
            _client ?? throw new InvalidOperationException("Authentication fixture has not started.");
        public IAmazonSQS Sqs { get; } = sqs;
        public INotificationDeliveryRecordStore Store { get; } = store;
        public RecordingLogs Logs { get; } = logs;
        public int EndpointCalls { get; private set; }

        public static async Task<AuthenticationFixture> Create(string adminSecret = Secret)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.WebHost.UseTestServer();
            builder.Configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Acl:Clients:admin:Type"] = "ApiKey",
                    ["Acl:Clients:admin:Secret"] = adminSecret,
                    ["Acl:Clients:admin:Scopes:0"] = "admin",
                    ["Acl:Clients:admin:Scopes:1"] = "read",
                    ["Acl:Clients:read:Type"] = "ApiKey",
                    ["Acl:Clients:read:Secret"] = Secret,
                    ["Acl:Clients:read:Scopes:0"] = "read",
                    ["Acl:Clients:write:Type"] = "ApiKey",
                    ["Acl:Clients:write:Secret"] = Secret,
                    ["Acl:Clients:write:Scopes:0"] = "write",
                    ["Acl:Clients:oauth:Type"] = "OAuth",
                    ["Acl:Clients:oauth:Secret"] = Secret,
                    ["Acl:Clients:oauth:Scopes:0"] = "admin",
                    ["Acl:Clients:oauth:Scopes:1"] = "read",
                    ["Acl:Clients:oauth-read:Type"] = "OAuth",
                    ["Acl:Clients:oauth-read:Scopes:0"] = "read",
                }
            );
            var logs = new RecordingLogs();
            builder.Logging.ClearProviders().AddProvider(logs);
            builder.Services.AddAuthenticationAuthorization(builder.Configuration);
            var sqs = Substitute.For<IAmazonSQS>();
            sqs.ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
                .Returns(new ReceiveMessageResponse());
            var store = Substitute.For<INotificationDeliveryRecordStore>();
            builder.Services.AddSingleton(sqs);
            builder.Services.AddSingleton(store);
            var app = builder.Build();
            var fixture = new AuthenticationFixture(app, sqs, store, logs);
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapPost(
                    "/admin/authentication-probe",
                    async (HttpContext context, IAmazonSQS dependency) =>
                    {
                        fixture.EndpointCalls++;
                        await dependency.ReceiveMessageAsync(
                            new ReceiveMessageRequest { QueueUrl = "local" },
                            context.RequestAborted
                        );

                        return Results.Ok(
                            new
                            {
                                name = context.User.Identity!.Name,
                                clientId = context.User.FindFirst(AuthClaims.ClientId)?.Value,
                                authenticationType = context.User.Identity.AuthenticationType,
                                scopes = context.User.FindAll(AuthClaims.Scope).Select(claim => claim.Value),
                            }
                        );
                    }
                )
                .RequireAuthorization(PolicyNames.Admin);
            await app.StartAsync(TestContext.Current.CancellationToken);
            fixture._client = app.GetTestClient();

            return fixture;
        }

        public async ValueTask DisposeAsync()
        {
            _client?.Dispose();
            await application.DisposeAsync();
        }
    }

    internal sealed class RecordingLogs : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(Messages);

        public void Dispose() { }

        private sealed class RecordingLogger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter
            ) => messages.Enqueue($"{formatter(state, exception)} {exception}");
        }
    }
}
