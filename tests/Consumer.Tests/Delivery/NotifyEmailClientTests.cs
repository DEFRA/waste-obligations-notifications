using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Defra.WasteObligations.Consumer.Commands;
using Defra.WasteObligations.Consumer.Delivery;
using Microsoft.Extensions.Options;
using Notify.Client;
using Notify.Interfaces;

namespace Defra.WasteObligations.Consumer.Tests.Delivery;

public sealed class NotifyEmailClientTests
{
    private const string Reference = "v1:opaque-reference";
    private const string NotificationId = "01234567-89ab-cdef-0123-456789abcdef";
    private static string ApiKey => NotifyTestCredentials.ApiKey;

    [Fact]
    public async Task CheckHealth_WhenNotifyResponds_ShouldUseSdkTemplateListAuthenticationAndDisposeContent()
    {
        var requests = 0;
        using var content = new ObservedContent(TemplateListBody());
        using var handler = new ControlledHandler(
            (request, _) =>
            {
                requests++;
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal("http://notify.local/v2/sdk-template-list?type=email", request.RequestUri!.AbsoluteUri);
                Assert.Null(request.Content);
                Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
                var token = request.Headers.Authorization.Parameter!.Split('.');
                Assert.Equal(3, token.Length);
                using var header = JsonDocument.Parse(DecodeBase64Url(token[0]));
                using var payload = JsonDocument.Parse(DecodeBase64Url(token[1]));
                Assert.Equal("HS256", header.RootElement.GetProperty("alg").GetString());
                Assert.Equal(
                    NotifyTestCredentials.ServiceId.ToString(),
                    payload.RootElement.GetProperty("iss").GetString()
                );
                Assert.Equal(
                    HMACSHA256.HashData(
                        Encoding.UTF8.GetBytes(NotifyTestCredentials.SecretId.ToString()),
                        Encoding.ASCII.GetBytes($"{token[0]}.{token[1]}")
                    ),
                    DecodeBase64Url(token[2])
                );

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            }
        );
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://notify.local") };
        var subject = new NotifyEmailClient(
            client,
            Options.Create(new NotifyOptions { ApiKey = ApiKey }),
            (transport, notify) =>
                new NotificationClient(transport, notify.ApiKey) { GET_ALL_TEMPLATES_URL = "v2/sdk-template-list" }
        );

        await subject.CheckHealth(TestContext.Current.CancellationToken);

        Assert.Equal(1, requests);
        Assert.True(content.WasDisposed);
    }

    [Theory]
    [InlineData(201)]
    [InlineData(202)]
    [InlineData(204)]
    [InlineData(302)]
    [InlineData(403)]
    [InlineData(500)]
    public async Task CheckHealth_WhenNotifyReturnsNon200_ShouldFailWithoutRetrying(int statusCode)
    {
        var requests = 0;
        using var content = new ObservedContent(TemplateListBody());
        using var handler = new ControlledHandler(
            (_, _) =>
            {
                requests++;

                return Task.FromResult(new HttpResponseMessage((HttpStatusCode)statusCode) { Content = content });
            }
        );
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://notify.local") };
        var subject = new NotifyEmailClient(
            client,
            Options.Create(new NotifyOptions { ApiKey = ApiKey }),
            (transport, notify) => new NotificationClient(transport, notify.ApiKey)
        );

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            subject.CheckHealth(TestContext.Current.CancellationToken)
        );

        Assert.Equal("Notify health request failed.", exception.Message);
        Assert.Null(exception.InnerException);
        Assert.Equal(1, requests);
        Assert.True(content.WasDisposed);
    }

    [Fact]
    public async Task CheckHealth_WhenDependencyThrowsPrivateError_ShouldSanitizeItsFailure()
    {
        using var handler = new ControlledHandler(
            (_, _) => throw new HttpRequestException($"recipient@example.com private-template {ApiKey}")
        );
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://notify.local") };
        var subject = new NotifyEmailClient(
            client,
            Options.Create(new NotifyOptions { ApiKey = ApiKey }),
            (transport, notify) => new NotificationClient(transport, notify.ApiKey)
        );

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            subject.CheckHealth(TestContext.Current.CancellationToken)
        );

        Assert.Equal("Notify health request failed.", exception.Message);
        Assert.Null(exception.InnerException);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CheckHealth_WhenCallerCancelsOrTimeoutExpires_ShouldCancelActualRequest(bool timeout)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = false;
        using var source = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var handler = new ControlledHandler(
            async (_, token) =>
            {
                started.SetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                    throw;
                }

                return new HttpResponseMessage(HttpStatusCode.OK);
            }
        );
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://notify.local"),
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var subject = new NotifyEmailClient(
            client,
            Options.Create(new NotifyOptions { ApiKey = ApiKey }),
            (transport, notify) => new NotificationClient(transport, notify.ApiKey)
        );
        var check = subject.CheckHealth(source.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        if (timeout)
            source.CancelAfter(TimeSpan.FromMilliseconds(50));
        else
            await source.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check);
        Assert.True(cancelled);
    }

    [Fact]
    public async Task CheckHealth_WhenCancellationResistantRequestReturnsLate200_ShouldRejectAndDisposeIt()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var source = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var content = new ObservedContent(TemplateListBody());
        using var handler = new ControlledHandler(
            async (_, _) =>
            {
                started.SetResult();
                await release.Task;

                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            }
        );
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://notify.local"),
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var subject = new NotifyEmailClient(
            client,
            Options.Create(new NotifyOptions { ApiKey = ApiKey }),
            (transport, notify) => new NotificationClient(transport, notify.ApiKey)
        );
        var check = subject.CheckHealth(source.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await source.CancelAsync();
        release.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check);
        Assert.True(content.WasDisposed);
    }

    [Theory]
    [InlineData("private-malformed-template-body")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"templates\":null}")]
    [InlineData("{\"templates\":\"private-template-body\"}")]
    public async Task CheckHealth_WhenSdkResponseIsInvalid_ShouldSanitizeAndDisposeWithoutRetry(string body)
    {
        var requests = 0;
        using var content = new ObservedContent(body);
        using var handler = new ControlledHandler(
            (_, _) =>
            {
                requests++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            }
        );
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://notify.local") };
        var subject = new NotifyEmailClient(
            client,
            Options.Create(new NotifyOptions { ApiKey = ApiKey }),
            (transport, notify) => new NotificationClient(transport, notify.ApiKey)
        );

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            subject.CheckHealth(TestContext.Current.CancellationToken)
        );

        Assert.Equal("Notify health request failed.", failure.Message);
        Assert.Null(failure.InnerException);
        Assert.DoesNotContain(body, failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(ApiKey, failure.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, requests);
        Assert.True(content.WasDisposed);
    }

    [Fact]
    public async Task CheckHealth_WhenCancelledDuringResponseBuffering_ShouldCancelActualReadAndDisposeContent()
    {
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var content = new BlockingContent(reading);
        using var source = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var handler = new ControlledHandler(
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content })
        );
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://notify.local") };
        var subject = new NotifyEmailClient(
            client,
            Options.Create(new NotifyOptions { ApiKey = ApiKey }),
            (transport, notify) => new NotificationClient(transport, notify.ApiKey)
        );

        var checking = subject.CheckHealth(source.Token);
        await reading.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await source.CancelAsync();
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => checking);

        Assert.Equal(source.Token, failure.CancellationToken);
        Assert.Null(failure.InnerException);
        Assert.True(content.WasDisposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CheckHealth_WhenSdkOrUnexpectedCancellationThrowsPrivateError_ShouldSanitize(bool factoryFails)
    {
        var privateText = $"recipient@example.com private-template {ApiKey}";
        using var handler = new ControlledHandler((_, _) => throw new OperationCanceledException(privateText));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://notify.local") };
        var subject = new NotifyEmailClient(
            client,
            Options.Create(new NotifyOptions { ApiKey = ApiKey }),
            (transport, notify) =>
                factoryFails
                    ? throw new InvalidOperationException(privateText)
                    : new NotificationClient(transport, notify.ApiKey)
        );

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            subject.CheckHealth(TestContext.Current.CancellationToken)
        );

        Assert.Equal("Notify health request failed.", failure.Message);
        Assert.Null(failure.InnerException);
        Assert.DoesNotContain(privateText, failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenNotifyAccepts_ShouldSendOneAuthenticatedNormalisedRequestAndProjectOnlyAcceptance()
    {
        var requests = 0;
        using var handler = new ControlledHandler(
            async (request, token) =>
            {
                requests++;
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("/v2/notifications/email", request.RequestUri!.AbsolutePath);
                Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                Assert.Equal("recipient@example.com", body.RootElement.GetProperty("email_address").GetString());
                Assert.Equal("template-1", body.RootElement.GetProperty("template_id").GetString());
                Assert.Equal(Reference, body.RootElement.GetProperty("reference").GetString());
                Assert.Equal(
                    "private-body",
                    body.RootElement.GetProperty("personalisation").GetProperty("body").GetString()
                );

                return Response(HttpStatusCode.Created, AcceptanceBody());
            }
        );
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://notify.local") };
        var subject = new NotifyEmailClient(
            client,
            Options.Create(new NotifyOptions { ApiKey = ApiKey }),
            (transport, notify) => new NotificationClient(transport, notify.ApiKey)
        );

        var acceptance = await subject.Send(Command(), Reference, TestContext.Current.CancellationToken);

        Assert.Equal(new NotifyAcceptance(NotificationId, Reference, "template-1", 2), acceptance);
        Assert.Equal(1, requests);
        Assert.DoesNotContain(
            "private-rendered-content",
            JsonSerializer.Serialize(acceptance),
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task WhenSendingTwice_ShouldUseSdkHeadersAndKeepConfiguredTransportReusable()
    {
        var requests = 0;
        using var handler = new ControlledHandler(
            (request, _) =>
            {
                requests++;
                Assert.Equal("http://notify.local/custom/v2/notifications/email", request.RequestUri!.AbsoluteUri);
                Assert.Contains(request.Headers.Accept, header => header.MediaType == "application/json");
                Assert.StartsWith("NOTIFY-API-NET-CLIENT/", request.Headers.GetValues("User-Agent").Single());

                return Task.FromResult(Response(HttpStatusCode.Created, AcceptanceBody()));
            }
        );
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://notify.local/custom/") };
        var subject = new NotifyEmailClient(
            client,
            Options.Create(new NotifyOptions { ApiKey = ApiKey }),
            (transport, notify) => new NotificationClient(transport, notify.ApiKey)
        );

        var first = await subject.Send(Command(), Reference, TestContext.Current.CancellationToken);
        var second = await subject.Send(Command(), Reference, TestContext.Current.CancellationToken);

        Assert.Equal(new NotifyAcceptance(NotificationId, Reference, "template-1", 2), first);
        Assert.Equal(first, second);
        Assert.Equal(2, requests);
        Assert.Equal(new Uri("http://notify.local/custom/"), client.BaseAddress);
        Assert.Empty(client.DefaultRequestHeaders.Accept);
        Assert.False(client.DefaultRequestHeaders.Contains("User-Agent"));
    }

    [Theory]
    [InlineData("01234567-89AB-CDEF-0123-456789ABCDEF")]
    [InlineData("{01234567-89AB-CDEF-0123-456789ABCDEF}")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF")]
    public async Task WhenNotifyReturnsCanonicalTemplateUuid_ShouldAcceptItsEquivalentRequestedIdentity(
        string requestedTemplateId
    )
    {
        const string canonicalTemplateId = "01234567-89ab-cdef-0123-456789abcdef";
        using var handler = new ControlledHandler(
            async (request, token) =>
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                Assert.Equal(requestedTemplateId, body.RootElement.GetProperty("template_id").GetString());

                return Response(
                    HttpStatusCode.Created,
                    JsonSerializer.Serialize(
                        new
                        {
                            id = NotificationId,
                            reference = Reference,
                            template = new { id = canonicalTemplateId, version = 2 },
                        }
                    )
                );
            }
        );
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://notify.local") };
        var subject = new NotifyEmailClient(
            client,
            Options.Create(new NotifyOptions { ApiKey = ApiKey }),
            (transport, notify) => new NotificationClient(transport, notify.ApiKey)
        );

        var acceptance = await subject.Send(
            Command() with
            {
                TemplateId = requestedTemplateId,
            },
            Reference,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(canonicalTemplateId, acceptance.TemplateId);
        Assert.Equal(NotificationId, acceptance.NotificationId);
    }

    [Fact]
    public async Task WhenNotifyReturnsDifferentTemplateUuid_ShouldRejectAcceptance()
    {
        const string returnedTemplateId = "01234567-89ab-cdef-0123-456789abcdee";
        using var handler = new ControlledHandler(
            (_, _) =>
                Task.FromResult(
                    Response(
                        HttpStatusCode.Created,
                        JsonSerializer.Serialize(
                            new
                            {
                                id = NotificationId,
                                reference = Reference,
                                template = new { id = returnedTemplateId, version = 2 },
                            }
                        )
                    )
                )
        );
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://notify.local") };
        var subject = new NotifyEmailClient(
            client,
            Options.Create(new NotifyOptions { ApiKey = ApiKey }),
            (transport, notify) => new NotificationClient(transport, notify.ApiKey)
        );

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            subject.Send(
                Command() with
                {
                    TemplateId = "01234567-89AB-CDEF-0123-456789ABCDEF",
                },
                Reference,
                TestContext.Current.CancellationToken
            )
        );

        Assert.DoesNotContain(returnedTemplateId, exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(400, "{\"private-address\":\"recipient@example.com\"}")]
    [InlineData(201, "not-json-private-body")]
    [InlineData(201, "{}")]
    [InlineData(201, "{\"id\":\"invalid\",\"reference\":\"wrong\",\"template\":{\"id\":\"template-1\",\"version\":0}}")]
    public async Task WhenNotifyFailsOrAcceptanceIsMalformed_ShouldNotRetryOrExposeTheResponse(
        int statusCode,
        string body
    )
    {
        var requests = 0;
        using var handler = new ControlledHandler(
            (_, _) =>
            {
                requests++;

                return Task.FromResult(Response((HttpStatusCode)statusCode, body));
            }
        );
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://notify.local") };
        var subject = new NotifyEmailClient(
            client,
            Options.Create(new NotifyOptions { ApiKey = ApiKey }),
            (transport, notify) => new NotificationClient(transport, notify.ApiKey)
        );

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            subject.Send(Command(), Reference, TestContext.Current.CancellationToken)
        );

        Assert.Null(exception.InnerException);
        Assert.DoesNotContain(body, exception.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task WhenCancelledDuringHttpRequest_ShouldCancelActualDependencyWork()
    {
        var cancelled = false;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var source = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var handler = new ControlledHandler(
            async (_, token) =>
            {
                started.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                    throw;
                }

                return Response(HttpStatusCode.Created, AcceptanceBody());
            }
        );
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://notify.local") };
        var subject = new NotifyEmailClient(
            client,
            Options.Create(new NotifyOptions { ApiKey = ApiKey }),
            (transport, notify) => new NotificationClient(transport, notify.ApiKey)
        );
        var send = subject.Send(Command(), Reference, source.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await source.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);
        Assert.True(cancelled);
    }

    [Fact]
    public async Task WhenCancelledDuringResponseBuffering_ShouldCancelBodyRead()
    {
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var source = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var content = new BlockingContent(reading);
        using var handler = new ControlledHandler(
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created) { Content = content })
        );
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://notify.local") };
        var subject = new NotifyEmailClient(
            client,
            Options.Create(new NotifyOptions { ApiKey = ApiKey }),
            (transport, notify) => new NotificationClient(transport, notify.ApiKey)
        );
        var send = subject.Send(Command(), Reference, source.Token);
        await reading.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await source.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);
        Assert.True(content.WasDisposed);
    }

    [Fact]
    public async Task WhenUsingInjectedSdkFactory_ShouldUseItsActualSdkOperationAuthenticationAndModels()
    {
        var factoryCalls = 0;
        using var handler = new ControlledHandler(
            (request, _) =>
            {
                Assert.Equal("/v2/sdk-email-operation", request.RequestUri!.AbsolutePath);
                var token = request.Headers.Authorization!.Parameter!.Split('.');
                using var payload = JsonDocument.Parse(DecodeBase64Url(token[1]));
                Assert.Equal(
                    NotifyTestCredentials.ServiceId.ToString(),
                    payload.RootElement.GetProperty("iss").GetString()
                );
                Assert.Equal(
                    HMACSHA256.HashData(
                        Encoding.UTF8.GetBytes(NotifyTestCredentials.SecretId.ToString()),
                        Encoding.ASCII.GetBytes($"{token[0]}.{token[1]}")
                    ),
                    DecodeBase64Url(token[2])
                );

                return Task.FromResult(Response(HttpStatusCode.Created, AcceptanceBody()));
            }
        );
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://notify.local") };
        var subject = new NotifyEmailClient(
            client,
            Options.Create(new NotifyOptions { ApiKey = ApiKey }),
            (transport, notify) =>
            {
                factoryCalls++;

                return new NotificationClient(transport, notify.ApiKey)
                {
                    SEND_EMAIL_NOTIFICATION_URL = "v2/sdk-email-operation",
                };
            }
        );

        var acceptance = await subject.Send(Command(), Reference, TestContext.Current.CancellationToken);

        Assert.Equal(1, factoryCalls);
        Assert.Equal(new NotifyAcceptance(NotificationId, Reference, "template-1", 2), acceptance);
    }

    [Fact]
    public async Task WhenPersonalisationContainsNestedAndLargeValues_ShouldPreserveJsonSemanticsThroughSdkSerialization()
    {
        using var personalisation = JsonDocument.Parse(
            """
            {"date":"2026-10-01T00:00:00Z","null":null,"large":1234567890123456789012345678901234567890,"precise":0.123456789012345678901234567890123456789,"nested":{"date":"2026-10-01","list":[true,false,null,{"number":1e123,"text":"private\ntext"}]},"array":[1,"2",{"value":null}],"duplicate":1,"duplicate":2}
            """
        );
        var requests = 0;
        using var handler = new ControlledHandler(
            async (request, token) =>
            {
                requests++;
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                var actual = body.RootElement.GetProperty("personalisation");
                foreach (
                    var name in personalisation
                        .RootElement.EnumerateObject()
                        .Select(property => property.Name)
                        .Distinct()
                )
                    Assert.Equal(
                        personalisation.RootElement.GetProperty(name).GetRawText(),
                        actual.GetProperty(name).GetRawText()
                    );
                Assert.Equal(JsonValueKind.String, actual.GetProperty("date").ValueKind);
                Assert.Equal(JsonValueKind.Null, actual.GetProperty("null").ValueKind);

                return Response(HttpStatusCode.Created, AcceptanceBody());
            }
        );
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://notify.local") };
        var subject = new NotifyEmailClient(
            client,
            Options.Create(new NotifyOptions { ApiKey = ApiKey }),
            (transport, notify) => new NotificationClient(transport, notify.ApiKey)
        );

        await subject.Send(
            Command() with
            {
                Personalisation = personalisation.RootElement,
            },
            Reference,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(1, requests);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(202)]
    [InlineData(204)]
    [InlineData(302)]
    [InlineData(500)]
    public async Task WhenSdkWouldAcceptAnotherStatus_ShouldRequire201AndDisposeWithoutRetry(int statusCode)
    {
        var requests = 0;
        HttpRequestMessage? sent = null;
        using var content = new ObservedContent(AcceptanceBody());
        using var handler = new ControlledHandler(
            (request, _) =>
            {
                requests++;
                sent = request;

                return Task.FromResult(new HttpResponseMessage((HttpStatusCode)statusCode) { Content = content });
            }
        );
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://notify.local") };
        var subject = new NotifyEmailClient(
            client,
            Options.Create(new NotifyOptions { ApiKey = ApiKey }),
            (transport, notify) => new NotificationClient(transport, notify.ApiKey)
        );

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            subject.Send(Command(), Reference, TestContext.Current.CancellationToken)
        );

        Assert.Equal("Notify email request failed or returned invalid acceptance evidence.", failure.Message);
        Assert.Null(failure.InnerException);
        Assert.Equal(1, requests);
        Assert.True(content.WasDisposed);
        Assert.NotNull(sent);
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            sent.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken)
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenSendCompletesOrParsingFails_ShouldDisposeRequestAndResponse(bool malformed)
    {
        HttpRequestMessage? sent = null;
        using var content = new ObservedContent(malformed ? "private-malformed-response" : AcceptanceBody());
        using var handler = new ControlledHandler(
            (request, _) =>
            {
                sent = request;

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created) { Content = content });
            }
        );
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://notify.local") };
        var subject = new NotifyEmailClient(
            client,
            Options.Create(new NotifyOptions { ApiKey = ApiKey }),
            (transport, notify) => new NotificationClient(transport, notify.ApiKey)
        );
        if (malformed)
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                subject.Send(Command(), Reference, TestContext.Current.CancellationToken)
            );
        else
            await subject.Send(Command(), Reference, TestContext.Current.CancellationToken);

        Assert.True(content.WasDisposed);
        Assert.NotNull(sent);
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            sent.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task WhenCancelledRequestReturnsCompleteLate201_ShouldRetainAcceptanceAndDispose()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var content = new ObservedContent(AcceptanceBody());
        using var source = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var handler = new ControlledHandler(
            async (_, _) =>
            {
                entered.TrySetResult();
                await release.Task;

                return new HttpResponseMessage(HttpStatusCode.Created) { Content = content };
            }
        );
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://notify.local") };
        var subject = new NotifyEmailClient(
            client,
            Options.Create(new NotifyOptions { ApiKey = ApiKey }),
            (transport, notify) => new NotificationClient(transport, notify.ApiKey)
        );
        var sending = subject.Send(Command(), Reference, source.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await source.CancelAsync();
        }
        finally
        {
            release.TrySetResult();
        }

        var acceptance = await sending;

        Assert.True(acceptance.Matches(Command(), Reference));
        Assert.True(content.WasDisposed);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("sdk")]
    [InlineData("unexpected-cancellation")]
    public async Task WhenSdkOrTransportThrowsPrivateFailure_ShouldReturnOnlySafeError(string stage)
    {
        var privateText = $"recipient@example.com private-body private-template {ApiKey}";
        using var handler = new ControlledHandler(
            (_, _) =>
                stage == "unexpected-cancellation"
                    ? throw new OperationCanceledException(privateText)
                    : throw new HttpRequestException(privateText)
        );
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://notify.local") };
        var subject = new NotifyEmailClient(
            client,
            Options.Create(new NotifyOptions { ApiKey = ApiKey }),
            (transport, notify) =>
                stage == "sdk"
                    ? throw new InvalidOperationException(privateText)
                    : new NotificationClient(transport, notify.ApiKey)
        );

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            subject.Send(Command(), Reference, TestContext.Current.CancellationToken)
        );

        Assert.Equal("Notify email request failed or returned invalid acceptance evidence.", failure.Message);
        Assert.Null(failure.InnerException);
        Assert.DoesNotContain(privateText, failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenOperationAttemptsAnotherTransportRequest_ShouldRefuseAndDisposeWithoutSecondHttpCall()
    {
        var requests = 0;
        using var handler = new ControlledHandler(
            (_, _) =>
            {
                requests++;

                return Task.FromResult(Response(HttpStatusCode.Created, AcceptanceBody()));
            }
        );
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://notify.local") };
        using var transport = new NotifySdkHttpClient(
            client,
            HttpStatusCode.Created,
            TestContext.Current.CancellationToken
        );
        using var first = new HttpRequestMessage(HttpMethod.Post, "v2/notifications/email");
        await transport.SendAsync(first);
        using var rejectedContent = new ObservedContent("private-request");
        using var second = new HttpRequestMessage(HttpMethod.Post, "v2/notifications/email")
        {
            Content = rejectedContent,
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.SendAsync(second));

        Assert.Equal(1, requests);
        Assert.True(rejectedContent.WasDisposed);
    }

    private sealed class ObservedContent(string text) : StringContent(text)
    {
        public bool WasDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }

    private static byte[] DecodeBase64Url(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');

        return Convert.FromBase64String(base64.PadRight((base64.Length + 3) / 4 * 4, '='));
    }

    private sealed class BlockingContent(TaskCompletionSource reading) : HttpContent
    {
        public bool WasDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new InvalidOperationException("Response buffering did not receive cancellation.");

        protected override async Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken
        )
        {
            reading.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;

            return false;
        }
    }

    private static NotificationCommand Command() =>
        new(
            1,
            "private-key",
            new DateTimeOffset(2026, 9, 29, 0, 0, 0, TimeSpan.Zero),
            "submitted",
            " Recipient@Example.com ",
            "template-1",
            JsonDocument.Parse("{\"body\":\"private-body\"}").RootElement.Clone()
        );

    private static string TemplateListBody() =>
        "{\"templates\":[{\"id\":\"private-template-id\",\"name\":\"private-template-name\",\"type\":\"email\",\"version\":1,\"body\":\"private-content\",\"subject\":\"private-subject\"}]}";

    private static string AcceptanceBody() =>
        JsonSerializer.Serialize(
            new
            {
                id = NotificationId,
                reference = Reference,
                template = new { id = "template-1", version = 2 },
                content = new { body = "private-rendered-content" },
            }
        );

    private static HttpResponseMessage Response(HttpStatusCode statusCode, string body) =>
        new(statusCode) { Content = new StringContent(body) };

    private sealed class ControlledHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => send(request, cancellationToken);
    }
}
