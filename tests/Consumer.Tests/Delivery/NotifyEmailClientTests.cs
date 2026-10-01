using System.Net;
using System.Text.Json;
using Defra.WasteObligations.Consumer.Commands;
using Defra.WasteObligations.Consumer.Delivery;
using Microsoft.Extensions.Options;

namespace Defra.WasteObligations.Consumer.Tests.Delivery;

public sealed class NotifyEmailClientTests
{
    private const string Reference = "v1:opaque-reference";
    private const string NotificationId = "01234567-89ab-cdef-0123-456789abcdef";
    private static string ApiKey => NotifyTestCredentials.ApiKey;

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
        var subject = new NotifyEmailClient(client, Options.Create(new NotifyOptions { ApiKey = ApiKey }));

        var acceptance = await subject.Send(Command(), Reference, TestContext.Current.CancellationToken);

        Assert.Equal(new NotifyAcceptance(NotificationId, Reference, "template-1", 2), acceptance);
        Assert.Equal(1, requests);
        Assert.DoesNotContain(
            "private-rendered-content",
            JsonSerializer.Serialize(acceptance),
            StringComparison.Ordinal
        );
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
        var subject = new NotifyEmailClient(client, Options.Create(new NotifyOptions { ApiKey = ApiKey }));

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
        var subject = new NotifyEmailClient(client, Options.Create(new NotifyOptions { ApiKey = ApiKey }));

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
        var subject = new NotifyEmailClient(client, Options.Create(new NotifyOptions { ApiKey = ApiKey }));

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
        using var source = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var handler = new ControlledHandler(
            async (_, token) =>
            {
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
        var subject = new NotifyEmailClient(client, Options.Create(new NotifyOptions { ApiKey = ApiKey }));
        var send = subject.Send(Command(), Reference, source.Token);
        await source.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);
        Assert.True(cancelled);
    }

    [Fact]
    public async Task WhenCancelledDuringResponseBuffering_ShouldCancelBodyRead()
    {
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var source = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var handler = new ControlledHandler(
            (_, _) =>
                Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.Created) { Content = new BlockingContent(reading) }
                )
        );
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://notify.local") };
        var subject = new NotifyEmailClient(client, Options.Create(new NotifyOptions { ApiKey = ApiKey }));
        var send = subject.Send(Command(), Reference, source.Token);
        await reading.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await source.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);
    }

    private sealed class BlockingContent(TaskCompletionSource reading) : HttpContent
    {
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
