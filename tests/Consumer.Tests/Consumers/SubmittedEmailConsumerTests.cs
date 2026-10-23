using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.SQS;
using Amazon.SQS.Model;
using Defra.WasteObligations.Consumer.Cancellation;
using Defra.WasteObligations.Consumer.Commands;
using Defra.WasteObligations.Consumer.Consumers;
using Defra.WasteObligations.Consumer.Delivery;
using Defra.WasteObligations.Consumer.Notifications;
using Defra.WasteObligations.Consumer.Services.AccountBackend;
using Defra.WasteObligations.Consumer.Services.WasteObligations;
using Defra.WasteObligations.Consumer.Services.WasteOrganisations;
using Defra.WasteObligations.Consumer.Startup;
using Defra.WasteObligations.Consumer.Submission;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Polly.Timeout;

namespace Defra.WasteObligations.Consumer.Tests.Consumers;

public sealed class SubmittedEmailConsumerTests
{
    private const string DeclarationId = "65f1f6570bb08052a8a27b01";
    private const string OrganisationId = "fbf1ba88-cd09-42f6-aad5-0ddfc29e5765";
    private const string PrivateEmail = " Original-Private@Example.test ";
    private const string PrivateName = "Distinctive original submitter";
    private const string UnsafeDependency = "unsafe dependency response with recipient@example.test";
    private const string DirectEnglish = "11111111-1111-4111-8111-111111111111";
    private const string DirectWelsh = "22222222-2222-4222-8222-222222222222";
    private const string SchemeEnglish = "33333333-3333-4333-8333-333333333333";
    private const string SchemeWelsh = "44444444-4444-4444-8444-444444444444";

    private static readonly JsonSerializerOptions s_webOptions = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("01jz8rxbmty2k15sjb3pcfn3d5")]
    [InlineData("3b9b504c-31b9-4a56-bb99-d3c46b16e6af")]
    public async Task WhenSubmissionEventIdHasAnotherFormat_ShouldPublishAndAcknowledge(string eventId)
    {
        using var fixture = new Fixture((_, _) => Task.FromResult(Response(Declaration())));

        await fixture.Run([Event(eventId)]);

        Assert.Single(fixture.Reads);
        Assert.Single(fixture.Publications);
        Assert.Equal(1, fixture.Deletions);
        fixture.AssertSafeLogs();
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("1.2")]
    [InlineData("1.4")]
    [InlineData("2.0")]
    public async Task WhenSubmissionSchemaContainsOrganisationIdentity_ShouldPublishForMinimumAndLaterSchemas(
        string schema
    )
    {
        using var fixture = new Fixture((_, _) => Task.FromResult(Response(Declaration())));
        var body = Event("01JZ8RXBMTY2K15SJB3PCFN3D5")
            .Replace("compliance_declaration_v1.3", $"compliance_declaration_v{schema}", StringComparison.Ordinal);

        await fixture.Run([body]);

        Assert.Single(fixture.Reads);
        Assert.Single(fixture.Publications);
        Assert.Equal(1, fixture.Deletions);
        fixture.AssertSafeLogs();
    }

    [Fact]
    public async Task WhenOutOfScopeIdentifiersAreUnrecognised_ShouldLogOpaqueCorrelationsAndAcknowledge()
    {
        const string eventId = "private-event-correlation";
        const string entityId = "private-entity-correlation";
        var body = JsonSerializer.Serialize(
            new
            {
                eventId,
                entityId,
                entity = "organisation",
                eventType = "updated",
            }
        );
        using var fixture = new Fixture((_, _) => Task.FromResult(Response(Declaration())));

        await fixture.Run([body]);

        Assert.Empty(fixture.Reads);
        Assert.Empty(fixture.Publications);
        Assert.Equal(1, fixture.Deletions);
        var consumed = Assert.Single(
            fixture.Logs.Entries,
            entry =>
                entry.Level == LogLevel.Information
                && entry.Text.StartsWith("Consumed analytics event", StringComparison.Ordinal)
        );
        Assert.Matches("Consumed analytics event opaque-[0-9a-f]{16} for opaque-[0-9a-f]{16}", consumed.Text);
        Assert.All(
            fixture.Logs.Entries,
            entry =>
            {
                Assert.DoesNotContain(eventId, entry.Text, StringComparison.Ordinal);
                Assert.DoesNotContain(entityId, entry.Text, StringComparison.Ordinal);
            }
        );
        fixture.AssertSafeLogs();
    }

    [Theory]
    [InlineData("\"compliance_declaration\"", "\"cancellation.created\"")]
    [InlineData("\"organisation\"", "\"submission.created\"")]
    [InlineData("5", "\"submission.created\"")]
    [InlineData("{}", "\"submission.created\"")]
    [InlineData("null", "\"submission.created\"")]
    [InlineData("\"compliance_declaration\"", "5")]
    [InlineData("\"compliance_declaration\"", "{}")]
    [InlineData("\"compliance_declaration\"", "null")]
    public async Task WhenEventIsOutsideSubmissionScope_ShouldAcknowledgeWithoutReadingOrPublishing(
        string entity,
        string eventType
    )
    {
        var body = Event("01JZ8RXBMTY2K15SJB3PCFN3D5")
            .Replace("\"entity\":\"compliance_declaration\"", $"\"entity\":{entity}", StringComparison.Ordinal)
            .Replace("\"eventType\":\"submission.created\"", $"\"eventType\":{eventType}", StringComparison.Ordinal);
        using var fixture = new Fixture((_, _) => Task.FromResult(Response(Declaration())));

        await fixture.Run([body]);

        Assert.Empty(fixture.Reads);
        Assert.Empty(fixture.Publications);
        Assert.Equal(1, fixture.Deletions);
        fixture.AssertSafeLogs();
    }

    [Theory]
    [InlineData(
        "DirectProducer",
        "GB-ENG",
        DirectEnglish,
        "declaration-submitted-direct-en",
        "The Stored Regulator",
        "the Stored Regulator",
        1,
        "Submitted"
    )]
    [InlineData(
        "DirectProducer",
        "GB-WLS",
        DirectWelsh,
        "declaration-submitted-direct-cy",
        "Stored Regulator",
        "Stored Regulator",
        2,
        "Cancelled"
    )]
    [InlineData(
        "ComplianceScheme",
        "GB-ENG",
        SchemeEnglish,
        "declaration-submitted-scheme-en",
        "The Stored Regulator",
        "the Stored Regulator",
        3,
        "Accepted"
    )]
    [InlineData(
        "ComplianceScheme",
        "GB-WLS",
        SchemeWelsh,
        "declaration-submitted-scheme-cy",
        "Stored Regulator",
        "Stored Regulator",
        1,
        "Accepted"
    )]
    [InlineData(
        "DirectProducer",
        "GB-NIR",
        DirectEnglish,
        "declaration-submitted-direct-en",
        "The Stored Regulator",
        "the Stored Regulator",
        1,
        "Submitted"
    )]
    [InlineData(
        "OtherRegistration",
        "GB-SCT",
        DirectEnglish,
        "declaration-submitted-direct-en",
        "The Stored Regulator",
        "the Stored Regulator",
        1,
        "Submitted"
    )]
    public async Task WhenSubmitted_ShouldPublishOriginalPersistedEmailAndAcknowledge(
        string registration,
        string country,
        string template,
        string type,
        string leading,
        string inline,
        int version,
        string status
    )
    {
        using var fixture = new Fixture(
            (_, _) => Task.FromResult(Response(Declaration(registration, country, version, status)))
        );
        await fixture.Run([Event("01JZ8RXBMTY2K15SJB3PCFN3D5")]);

        var request = Assert.Single(fixture.Publications);
        var command = JsonSerializer.Deserialize<NotificationCommand>(request.MessageBody, s_webOptions)!;
        Assert.Equal("compliance-declaration:65f1f6570bb08052a8a27b01:submitted:v1", command.IdempotencyKey);
        Assert.Equal(template, command.TemplateId);
        Assert.Equal(type, command.NotificationType);
        Assert.Equal("original-private@example.test", command.EmailAddress);
        Assert.Equal(new DateTimeOffset(2026, 9, 20, 11, 12, 13, TimeSpan.Zero), command.ActionOccurredAtUtc);
        var values = command.Personalisation;
        Assert.Equal(5, values.EnumerateObject().Count());
        Assert.Equal(JsonValueKind.Number, values.GetProperty("obligationYear").ValueKind);
        Assert.Equal(2025, values.GetProperty("obligationYear").GetInt32());
        Assert.Equal(leading, values.GetProperty("regulatorLeading").GetString());
        Assert.Equal(inline, values.GetProperty("regulatorInline").GetString());
        Assert.Equal("stored-regulator@example.test", values.GetProperty("regulatorEmail").GetString());
        Assert.Equal(PrivateName, values.GetProperty("user").GetString());
        Assert.Equal(1, fixture.Deletions);
        var read = Assert.Single(fixture.Reads);
        Assert.Equal($"/organisations/{OrganisationId}/compliance-declarations/{DeclarationId}", read.Path);
        Assert.Equal("Basic dGVzdC1jbGllbnQ6dGVzdC1zZWNyZXQ=", read.Authorization);
        Assert.Contains(
            fixture.Logs.Entries,
            entry =>
                entry.Level == LogLevel.Information
                && entry.Text.Contains("Published notification command", StringComparison.Ordinal)
                && entry.Text.Contains(
                    fixture.Digest.CreateNotifyReference(command.IdempotencyKey),
                    StringComparison.Ordinal
                )
        );
        fixture.AssertSafeLogs();
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(false, "null")]
    [InlineData(false, "\"\"")]
    [InlineData(false, "5")]
    [InlineData(true, null)]
    [InlineData(true, "null")]
    [InlineData(true, "\"\"")]
    [InlineData(true, "5")]
    public async Task WhenCountryIsAbsentOrNotWelsh_ShouldPublishEnglishAndAcknowledge(bool scheme, string? country)
    {
        var body = Declaration(scheme ? "ComplianceScheme" : "DirectProducer")
            .Replace(
                "\"businessCountry\":\"GB-ENG\",",
                country is null ? "" : $"\"businessCountry\":{country},",
                StringComparison.Ordinal
            );
        using var fixture = new Fixture((_, _) => Task.FromResult(Response(body)));

        await fixture.Run([Event("01JZ8RXBMTY2K15SJB3PCFN3D5")]);

        var publication = Assert.Single(fixture.Publications);
        var command = JsonSerializer.Deserialize<NotificationCommand>(publication.MessageBody, s_webOptions)!;
        Assert.Equal(scheme ? SchemeEnglish : DirectEnglish, command.TemplateId);
        Assert.Equal(
            scheme ? "declaration-submitted-scheme-en" : "declaration-submitted-direct-en",
            command.NotificationType
        );
        Assert.Equal("The Stored Regulator", command.Personalisation.GetProperty("regulatorLeading").GetString());
        Assert.Equal("the Stored Regulator", command.Personalisation.GetProperty("regulatorInline").GetString());
        Assert.Single(fixture.Reads);
        Assert.Equal(1, fixture.Deletions);
        fixture.AssertSafeLogs();
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"action\":null}")]
    [InlineData("{\"action\":\"\"}")]
    [InlineData("{\"action\":\" \"}")]
    public async Task WhenUnrelatedAuditHasNoAction_ShouldUseFirstSubmittedAudit(string entry)
    {
        var body = Declaration().Replace("\"audit\":[", $"\"audit\":[{entry},", StringComparison.Ordinal);
        using var fixture = new Fixture((_, _) => Task.FromResult(Response(body)));

        await fixture.Run([Event("01JZ8RXBMTY2K15SJB3PCFN3D5")]);

        var publication = Assert.Single(fixture.Publications);
        var command = JsonSerializer.Deserialize<NotificationCommand>(publication.MessageBody, s_webOptions)!;
        Assert.Equal(PrivateEmail.Trim().ToLowerInvariant(), command.EmailAddress);
        Assert.Equal(PrivateName, command.Personalisation.GetProperty("user").GetString());
        Assert.Equal(new DateTimeOffset(2026, 9, 20, 11, 12, 13, TimeSpan.Zero), command.ActionOccurredAtUtc);
        Assert.Equal(1, fixture.Deletions);
        fixture.AssertSafeLogs();
    }

    [Fact]
    public async Task WhenDeclarationIsDeleted_ShouldPublishSameSubmissionFromEventSnapshot()
    {
        using var existing = new Fixture((_, _) => Task.FromResult(Response(Declaration())));
        await existing.Run([Event("01JZ8RXBMTY2K15SJB3PCFN3D5")]);
        using var deleted = new Fixture((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        var analyticsEvent = JsonNode.Parse(Event("01JZ8RXBMTY2K15SJB3PCFN3D5"))!;
        analyticsEvent["after"] = JsonNode.Parse(Declaration());

        await deleted.Run([analyticsEvent.ToJsonString()]);

        Assert.Equal(Assert.Single(existing.Publications).MessageBody, Assert.Single(deleted.Publications).MessageBody);
        Assert.Equal(1, deleted.Deletions);
        Assert.Single(deleted.Reads);
        Assert.Single(
            deleted.Logs.Entries,
            entry =>
                entry.Level == LogLevel.Warning
                && entry.Text.Contains("declaration-snapshot-fallback", StringComparison.Ordinal)
        );
        Assert.DoesNotContain(
            existing.Logs.Entries,
            entry => entry.Text.Contains("declaration-snapshot-fallback", StringComparison.Ordinal)
        );
        deleted.AssertSafeLogs();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenLiveDeclarationExists_ShouldRemainAuthoritative(bool malformedSnapshot)
    {
        var snapshot = JsonNode.Parse(Declaration())!;
        snapshot["audit"]![0]!["user"]!["email"] = "snapshot@example.test";
        if (malformedSnapshot)
            snapshot["version"] = "invalid";
        var analyticsEvent = JsonNode.Parse(Event("01JZ8RXBMTY2K15SJB3PCFN3D5"))!;
        analyticsEvent["after"] = snapshot;
        using var fixture = new Fixture((_, _) => Task.FromResult(Response(Declaration())));

        await fixture.Run([analyticsEvent.ToJsonString()]);

        var command = JsonSerializer.Deserialize<NotificationCommand>(
            Assert.Single(fixture.Publications).MessageBody,
            s_webOptions
        )!;
        Assert.Equal("original-private@example.test", command.EmailAddress);
        Assert.Equal(1, fixture.Deletions);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{")]
    public async Task WhenSuccessfulReadIsMalformed_ShouldNotUseSnapshot(string response)
    {
        var analyticsEvent = JsonNode.Parse(Event("01JZ8RXBMTY2K15SJB3PCFN3D5"))!;
        analyticsEvent["after"] = JsonNode.Parse(Declaration());
        using var fixture = new Fixture((_, _) => Task.FromResult(Response(response)));

        await fixture.Run([analyticsEvent.ToJsonString()]);

        Assert.Empty(fixture.Publications);
        Assert.Equal(0, fixture.Deletions);
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("organisation")]
    [InlineData("older-version")]
    [InlineData("newer-version")]
    [InlineData("malformed-version")]
    [InlineData("audit")]
    [InlineData("user")]
    [InlineData("timestamp")]
    public async Task WhenDeletedDeclarationSnapshotIsUnusable_ShouldRetainWithoutPublication(string fault)
    {
        var snapshot = JsonNode.Parse(Declaration())!;
        switch (fault)
        {
            case "identity":
                snapshot["id"] = "65f1f6570bb08052a8a27b02";
                break;
            case "organisation":
                snapshot["organisation"]!["id"] = "not-a-guid";
                break;
            case "older-version":
                snapshot["version"] = 0;
                break;
            case "newer-version":
                snapshot["version"] = 2;
                break;
            case "malformed-version":
                snapshot["version"] = "one";
                break;
            case "audit":
                snapshot["audit"] = new JsonArray();
                break;
            case "user":
                snapshot["audit"]![0]!["user"]!["email"] = "";
                break;
            case "timestamp":
                snapshot["audit"]![0]!["timestamp"] = "not-a-timestamp";
                break;
        }
        var analyticsEvent = JsonNode.Parse(Event("01JZ8RXBMTY2K15SJB3PCFN3D5"))!;
        analyticsEvent["after"] = snapshot;
        using var fixture = new Fixture((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));

        await fixture.Run([analyticsEvent.ToJsonString()]);

        Assert.Empty(fixture.Publications);
        Assert.Equal(0, fixture.Deletions);
        fixture.AssertSafeLogs();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task WhenReadFailsWithUsableSnapshot_ShouldNotFallBack(HttpStatusCode status)
    {
        var analyticsEvent = JsonNode.Parse(Event("01JZ8RXBMTY2K15SJB3PCFN3D5"))!;
        analyticsEvent["after"] = JsonNode.Parse(Declaration());
        using var fixture = new Fixture((_, _) => Task.FromResult(new HttpResponseMessage(status)));

        await fixture.Run([analyticsEvent.ToJsonString()]);

        Assert.Empty(fixture.Publications);
        Assert.Equal(0, fixture.Deletions);
    }

    [Theory]
    [InlineData("not-found", "declaration-not-found")]
    [InlineData("older", "older-declaration-version")]
    [InlineData("missing-version", "invalid-declaration-data")]
    [InlineData("null-version", "invalid-declaration-data")]
    [InlineData("identity", "identity-mismatch")]
    [InlineData("organisation", "identity-mismatch")]
    [InlineData("audit", "invalid-declaration-data")]
    [InlineData("user", "invalid-declaration-data")]
    [InlineData("timestamp", "invalid-declaration-data")]
    [InlineData("malformed-json", "invalid-declaration-data")]
    [InlineData("missing-year", "invalid-declaration-data")]
    [InlineData("wrong-year-type", "invalid-declaration-data")]
    [InlineData("invalid-organisation", "invalid-declaration-data")]
    [InlineData("zero-year", "invalid-declaration-data")]
    [InlineData("negative-year", "invalid-declaration-data")]
    public async Task WhenDeclarationCannotBeUsed_ShouldRetainEventAndWarn(string fault, string reason)
    {
        var body = Declaration();
        body = fault switch
        {
            "missing-version" => body.Replace("\"version\": 1,", "", StringComparison.Ordinal),
            "null-version" => body.Replace("\"version\": 1", "\"version\": null", StringComparison.Ordinal),
            "older" => body.Replace("\"version\": 1", "\"version\": 0", StringComparison.Ordinal),
            "identity" => body.Replace(DeclarationId, "65f1f6570bb08052a8a27b02", StringComparison.Ordinal),
            "organisation" => body.Replace(
                OrganisationId,
                "fbf1ba88-cd09-42f6-aad5-0ddfc29e5766",
                StringComparison.Ordinal
            ),
            "audit" => body.Replace("Submitted", "Created", StringComparison.Ordinal),
            "user" => body.Replace(PrivateName, "", StringComparison.Ordinal),
            "timestamp" => body.Replace("2026-09-20T11:12:13Z", "2026-09-20T11:12:13", StringComparison.Ordinal),
            "malformed-json" => "{",
            "missing-year" => body.Replace("\"obligationYear\":2025,", "", StringComparison.Ordinal),
            "wrong-year-type" => body.Replace(
                "\"obligationYear\":2025",
                "\"obligationYear\":\"2025\"",
                StringComparison.Ordinal
            ),
            "invalid-organisation" => body.Replace(OrganisationId, "not-a-guid", StringComparison.Ordinal),
            "zero-year" => body.Replace("\"obligationYear\":2025", "\"obligationYear\":0", StringComparison.Ordinal),
            "negative-year" => body.Replace(
                "\"obligationYear\":2025",
                "\"obligationYear\":-1",
                StringComparison.Ordinal
            ),
            _ => body,
        };
        using var fixture = new Fixture(
            (_, _) =>
                Task.FromResult(
                    fault == "not-found" ? new HttpResponseMessage(HttpStatusCode.NotFound) : Response(body)
                )
        );
        await fixture.Run([Event("01JZ8RXBMTY2K15SJB3PCFN3D5")]);

        Assert.Empty(fixture.Publications);
        Assert.Equal(0, fixture.Deletions);
        Assert.Contains(
            fixture.Logs.Entries,
            entry =>
                entry.Level == LogLevel.Warning
                && entry.Text.Contains(reason, StringComparison.Ordinal)
                && entry.Text.Contains("not acknowledged", StringComparison.Ordinal)
        );
        fixture.AssertSafeLogs();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task WhenDeclarationServiceRejectsRead_ShouldRetainEventWithoutPublishing(HttpStatusCode status)
    {
        using var fixture = new Fixture(
            (_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(UnsafeDependency) })
        );
        await fixture.Run([Event("01JZ8RXBMTY2K15SJB3PCFN3D5")]);

        Assert.Single(fixture.Reads);
        Assert.Empty(fixture.Publications);
        Assert.Equal(0, fixture.Deletions);
        Assert.Contains(fixture.Logs.Entries, entry => entry.Level == LogLevel.Warning);
        fixture.AssertSafeLogs();
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("cancelled")]
    [InlineData("polly-timeout")]
    public async Task WhenReadTimesOutOrDependencyCancels_ShouldBackOffAndPublishNextUsableEvent(string failure)
    {
        var count = 0;
        using var fixture = new Fixture(
            async (_, token) =>
            {
                if (Interlocked.Increment(ref count) == 1)
                {
                    if (failure == "polly-timeout")
                        throw new TimeoutRejectedException(UnsafeDependency);
                    if (failure == "timeout")
                        await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    throw new OperationCanceledException(UnsafeDependency);
                }

                return Response(Declaration());
            }
        );
        await fixture.Run([Event("01JZ8RXBMTY2K15SJB3PCFN3D5"), Event("01JZ8RXBMTY2K15SJB3PCFN3D6")]);

        Assert.Single(fixture.Publications);
        Assert.Equal(1, fixture.Deletions);
        Assert.True(
            fixture.ReceiveTimes[1] - fixture.ReceiveTimes[0] >= TimeSpan.FromSeconds(failure == "timeout" ? 1.8 : 0.9)
        );
        Assert.Contains(
            fixture.Logs.Entries,
            entry =>
                entry.Level == LogLevel.Warning && entry.Text.Contains("dependency-timeout", StringComparison.Ordinal)
        );
        fixture.AssertSafeLogs();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenPublicationOrAcknowledgementIsUnconfirmed_ShouldReportAccurateRetryState(bool acknowledgement)
    {
        using var fixture = new Fixture(
            (_, _) => Task.FromResult(Response(Declaration())),
            failPublish: !acknowledgement,
            failDelete: acknowledgement
        );
        await fixture.Run([Event("01JZ8RXBMTY2K15SJB3PCFN3D5")]);

        Assert.Single(fixture.Publications);
        Assert.Equal(acknowledgement ? 1 : 0, fixture.Deletions);
        Assert.Equal(
            acknowledgement,
            fixture.Logs.Entries.Any(entry =>
                entry.Text.Contains("Published notification command", StringComparison.Ordinal)
            )
        );
        Assert.Contains(
            fixture.Logs.Entries,
            entry =>
                entry.Level == LogLevel.Warning
                && entry.Text.Contains(
                    acknowledgement
                        ? "command publication confirmed; acknowledgement uncertain"
                        : "unconfirmed-publication",
                    StringComparison.Ordinal
                )
        );
        fixture.AssertSafeLogs();
    }

    [Fact]
    public async Task WhenDifferentEventIdsReplaySubmission_ShouldPublishIdenticalCommandAndLane()
    {
        using var fixture = new Fixture((_, _) => Task.FromResult(Response(Declaration())));
        await fixture.Run([Event("01JZ8RXBMTY2K15SJB3PCFN3D5"), Event("01JZ8RXBMTY2K15SJB3PCFN3D6")]);

        Assert.Equal(2, fixture.Publications.Count);
        Assert.Equal(fixture.Publications[0].MessageBody, fixture.Publications[1].MessageBody);
        Assert.Equal(fixture.Publications[0].MessageDeduplicationId, fixture.Publications[1].MessageDeduplicationId);
        Assert.Equal(fixture.Publications[0].MessageGroupId, fixture.Publications[1].MessageGroupId);
        Assert.Equal(2, fixture.Deletions);
    }

    [Fact]
    public async Task WhenIdentifiersContainPrivateWords_ShouldRetainEventWithoutLoggingThem()
    {
        const string privateIdentifier = "DISTINCTIVEPRIVATENAMEABCDE";
        using var fixture = new Fixture((_, _) => Task.FromResult(Response(Declaration())));
        await fixture.Run([
            Event(privateIdentifier).Replace($"cdec_{DeclarationId}", privateIdentifier, StringComparison.Ordinal),
        ]);

        Assert.Empty(fixture.Reads);
        Assert.Empty(fixture.Publications);
        Assert.Equal(0, fixture.Deletions);
        Assert.All(
            fixture.Logs.Entries,
            entry => Assert.DoesNotContain(privateIdentifier, entry.Text, StringComparison.Ordinal)
        );
        Assert.Contains(
            fixture.Logs.Entries,
            entry =>
                entry.Level == LogLevel.Warning && entry.Text.Contains("invalid-event-data", StringComparison.Ordinal)
        );
    }

    [Theory]
    [InlineData("schema", false)]
    [InlineData("malformed-schema", false)]
    [InlineData("version", false)]
    [InlineData("organisation", false)]
    [InlineData("schema", true)]
    [InlineData("version", true)]
    [InlineData("organisation", true)]
    public async Task WhenMatchingAnalyticsEventIsInvalid_ShouldRetainWithoutReadingOrPublishing(
        string fault,
        bool cancellation
    )
    {
        var body = Event("01JZ8RXBMTY2K15SJB3PCFN3D5");
        if (cancellation)
            body = body.Replace("submission.created", "submission.amended", StringComparison.Ordinal)
                .Replace(
                    "\"after\":{\"organisation\":",
                    "\"before\":{\"status\":\"Submitted\"},\"after\":{\"status\":\"Cancelled\",\"organisation\":",
                    StringComparison.Ordinal
                );
        body = fault switch
        {
            "schema" => body.Replace(
                "compliance_declaration_v1.3",
                "compliance_declaration_v0.0",
                StringComparison.Ordinal
            ),
            "malformed-schema" => body.Replace(
                "compliance_declaration_v1.3",
                "compliance_declaration_v0",
                StringComparison.Ordinal
            ),
            "version" => body.Replace("\"version\":1", "\"version\":0", StringComparison.Ordinal),
            "organisation" => body.Replace(OrganisationId, Guid.Empty.ToString("D"), StringComparison.Ordinal),
            _ => body,
        };
        using var fixture = new Fixture((_, _) => Task.FromResult(Response(Declaration())));

        await fixture.Run([body]);

        Assert.Empty(fixture.Reads);
        Assert.Empty(fixture.Publications);
        Assert.Equal(0, fixture.Deletions);
        Assert.Contains(
            fixture.Logs.Entries,
            entry =>
                entry.Level == LogLevel.Warning
                && entry.Text.Contains("invalid-event-data", StringComparison.Ordinal)
                && entry.Text.Contains("not acknowledged", StringComparison.Ordinal)
        );
        fixture.AssertSafeLogs();
    }

    private static HttpResponseMessage Response(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static string Event(string eventId) =>
        $$$"""
            {"eventId":"{{{eventId}}}","entityId":"cdec_{{{DeclarationId}}}","entity":"compliance_declaration","eventType":"submission.created","operation":"create","version":1,"schemaVersion":"compliance_declaration_v1.3","occurredAt":"2026-09-20T11:12:13Z","recordedAt":"2026-09-20T11:12:14Z","sequence":1,"after":{"organisation":{"id":"{{{OrganisationId}}}","businessCountry":"GB-WLS","registrationType":"ComplianceScheme","regulator":"Current changed regulator","regulatorEmail":"current-regulator@example.test"}},"actor":"2be16023-4b2b-4e6c-960a-0bc1a1162a84"}
            """;

    private static string Declaration(
        string registration = "DirectProducer",
        string country = "GB-ENG",
        int version = 1,
        string status = "Cancelled"
    ) =>
        $$$"""
            {
              "id":"{{{DeclarationId}}}", "version": {{{version}}}, "status":"{{{status}}}", "obligationYear":2025,
              "organisation":{"id":"{{{OrganisationId}}}","businessCountry":"{{{country}}}","registrationType":"{{{registration}}}","regulator":"Stored Regulator","regulatorEmail":"stored-regulator@example.test"},
              "submitterName":"changed current submitter", "updated":"2026-10-01T12:00:00Z",
              "audit":[
                {"action":"Submitted","timestamp":"2026-09-20T11:12:13Z","user":{"id":"5bd7385b-042e-464a-bba8-b9dcc0dce677","email":"{{{PrivateEmail}}}","name":"{{{PrivateName}}}","locale":"cy"}},
                {"action":"Submitted","timestamp":"2026-09-21T12:00:00Z","user":{"id":"09fec9fa-b22d-4571-869e-68c5e0effe3e","email":"second-submit@example.test","name":"second submitter","locale":"en"}},
                {"action":"Cancelled","timestamp":"2026-10-01T12:00:00Z","user":{"id":"2be16023-4b2b-4e6c-960a-0bc1a1162a84","email":"later-actor@example.test","name":"later actor","locale":"en"}}
              ]
            }
            """;

    private sealed class Fixture : IDisposable
    {
        private readonly HttpClient _http;
        private readonly IAmazonSQS _sqs = Substitute.For<IAmazonSQS>();
        private readonly SubmittedEmailProducer _producer;
        private readonly NotificationCommandPublisher _publisher;
        private readonly bool _failDelete;
        public List<SendMessageRequest> Publications { get; } = [];
        public List<(string Path, string? Authorization)> Reads { get; } = [];
        public List<TimeSpan> ReceiveTimes { get; } = [];
        public int Deletions { get; private set; }
        public Logs Logs { get; } = new();
        public NotificationCommandDigest Digest { get; }

        public Fixture(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> read,
            bool failPublish = false,
            bool failDelete = false
        )
        {
            _failDelete = failDelete;
            var definitions = new NotificationDefinitionsOptions();
            var templates = definitions.Definitions[NotificationDefinitionsOptions.DeclarationSubmitted].Templates;
            templates["DirectEnglishTemplateId"] = DirectEnglish;
            templates["DirectWelshTemplateId"] = DirectWelsh;
            templates["SchemeEnglishTemplateId"] = SchemeEnglish;
            templates["SchemeWelshTemplateId"] = SchemeWelsh;
            var submission = Options.Create(definitions);
            _http = new HttpClient(
                new Handler(
                    (request, token) =>
                    {
                        Reads.Add((request.RequestUri!.AbsolutePath, request.Headers.Authorization?.ToString()));

                        return read(request, token);
                    }
                )
            )
            {
                BaseAddress = new Uri("https://declarations.test/"),
                Timeout = Timeout.InfiniteTimeSpan,
            };
            _producer = new SubmittedEmailProducer(
                new WasteObligationsClient(
                    _http,
                    Options.Create(new WasteObligationsOptions { Username = "test-client", Password = "test-secret" })
                ),
                submission,
                Logs
            );
            var delivery = Options.Create(
                new NotificationCommandDeliveryOptions
                {
                    QueueUrl = "http://sqs.test/commands.fifo",
                    EvidenceDigestSecret = "test-evidence-secret",
                    RecipientLaneSecret = "test-recipient-secret",
                }
            );
            Digest = new NotificationCommandDigest(delivery);
            _publisher = new NotificationCommandPublisher(_sqs, delivery, Digest);
            _sqs.SendMessageAsync(Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    Publications.Add(call.Arg<SendMessageRequest>());
                    if (failPublish)
                        throw new HttpRequestException(UnsafeDependency);

                    return Task.FromResult(new SendMessageResponse { HttpStatusCode = HttpStatusCode.OK });
                });
            _sqs.DeleteMessageAsync("http://sqs.test/analytics", Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    Deletions++;
                    if (_failDelete)
                        throw new HttpRequestException(UnsafeDependency);

                    return Task.FromResult(new DeleteMessageResponse());
                });
        }

        public async Task Run(string[] events)
        {
            var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var received = 0;
            var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            _sqs.ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
                .Returns(async call =>
                {
                    Assert.Equal(1, call.Arg<ReceiveMessageRequest>().MaxNumberOfMessages);
                    Assert.Equal(150, call.Arg<ReceiveMessageRequest>().VisibilityTimeout);
                    ReceiveTimes.Add(System.Diagnostics.Stopwatch.GetElapsedTime(startedAt));
                    var index = received++;
                    if (index < events.Length)
                        return new ReceiveMessageResponse
                        {
                            Messages =
                            [
                                new Message
                                {
                                    Body = events[index],
                                    ReceiptHandle = $"receipt-{index}",
                                    MessageId = $"sqs-{index}",
                                },
                            ],
                        };
                    complete.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, call.Arg<CancellationToken>());

                    return new ReceiveMessageResponse();
                });
            var startup = new ApplicationStartup();
            startup.MarkStarted();
            using var consumer = new AnalyticsEventConsumer(
                _sqs,
                Options.Create(
                    new AnalyticsEventConsumerOptions
                    {
                        QueueUrl = "http://sqs.test/analytics",
                        ProcessingEnabled = true,
                        WaitTimeSeconds = 1,
                        ReadTimeoutSeconds = 1,
                        PollIntervalSeconds = 1,
                    }
                ),
                Logs,
                startup,
                [
                    _producer,
                    new CancellationEmailProducer(
                        _producer,
                        Substitute.For<IWasteOrganisationsClient>(),
                        Substitute.For<IAccountBackendClient>(),
                        Options.Create(new NotificationDefinitionsOptions()),
                        Microsoft.Extensions.Logging.Abstractions.NullLogger<CancellationEmailProducer>.Instance
                    ),
                ],
                _publisher,
                Digest
            );
            await consumer.StartAsync(TestContext.Current.CancellationToken);
            await complete.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await consumer.StopAsync(TestContext.Current.CancellationToken);
        }

        public void AssertSafeLogs()
        {
            foreach (
                var value in new[]
                {
                    PrivateEmail.Trim(),
                    PrivateEmail.Trim().ToLowerInvariant(),
                    PrivateName,
                    UnsafeDependency,
                    "stored-regulator@example.test",
                    "test-secret",
                    $"compliance-declaration:{DeclarationId}:submitted:v1",
                    "later-actor@example.test",
                }
            )
                Assert.All(Logs.Entries, entry => Assert.DoesNotContain(value, entry.Text, StringComparison.Ordinal));
            Assert.All(Logs.Entries, entry => Assert.Null(entry.Exception));
        }

        public void Dispose() => _http.Dispose();
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => send(request, cancellationToken);
    }

    private sealed class Logs : ILogger<AnalyticsEventConsumer>, ILogger<SubmittedEmailProducer>
    {
        public ConcurrentQueue<(LogLevel Level, string Text, Exception? Exception)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => Entries.Enqueue((logLevel, formatter(state, exception), exception));
    }
}
