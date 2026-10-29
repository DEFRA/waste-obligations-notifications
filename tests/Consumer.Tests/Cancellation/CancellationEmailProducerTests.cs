using System.Text.Json;
using Defra.WasteObligations.Consumer.Cancellation;
using Defra.WasteObligations.Consumer.Notifications;
using Defra.WasteObligations.Consumer.Services.AccountBackend;
using Defra.WasteObligations.Consumer.Services.WasteObligations;
using Defra.WasteObligations.Consumer.Services.WasteOrganisations;
using Defra.WasteObligations.Consumer.Submission;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using LiveOrganisation = Defra.WasteObligations.Consumer.Services.WasteOrganisations.Organisation;

namespace Defra.WasteObligations.Consumer.Tests.Cancellation;

public sealed class CancellationEmailProducerTests
{
    private static readonly string[] SortedRecipientAddresses = ["a@example.test", "submitter@example.test"];

    [Theory]
    [InlineData(
        "Not signed by correct person",
        "GB-ENG",
        "1502cfa2-9758-4410-b189-d1f95a7c774f",
        "declaration-cancelled-not-signed-by-correct-person-en"
    )]
    [InlineData(
        "Not signed by correct person",
        "GB-WLS",
        "7cab1c5e-9edf-4139-b3fd-3db4bcf3041d",
        "declaration-cancelled-not-signed-by-correct-person-cy"
    )]
    [InlineData(
        "Recycling obligations changed",
        "GB-ENG",
        "857808d4-d159-421e-92b6-bf9d59711a9e",
        "declaration-cancelled-recycling-obligations-changed-en"
    )]
    [InlineData(
        "Recycling obligations changed",
        "GB-WLS",
        "0a966633-b7ad-42dd-b286-af0cb2405ed2",
        "declaration-cancelled-recycling-obligations-changed-cy"
    )]
    [InlineData(
        "Producer can meet recycling obligations",
        "GB-ENG",
        "86345150-cb25-4b59-94c3-578bed7e82d4",
        "declaration-cancelled-can-meet-recycling-obligations-en"
    )]
    [InlineData(
        "Producer can meet recycling obligations",
        "GB-WLS",
        "308e7224-0ab1-4f54-8e8d-1558c9c57a77",
        "declaration-cancelled-can-meet-recycling-obligations-cy"
    )]
    [InlineData(
        "Compliance scheme can meet recycling obligations",
        "GB-ENG",
        "86345150-cb25-4b59-94c3-578bed7e82d4",
        "declaration-cancelled-can-meet-recycling-obligations-en"
    )]
    [InlineData(
        "Compliance scheme can meet recycling obligations",
        "GB-WLS",
        "308e7224-0ab1-4f54-8e8d-1558c9c57a77",
        "declaration-cancelled-can-meet-recycling-obligations-cy"
    )]
    [InlineData(
        "Producer requested to cancel",
        "GB-ENG",
        "3e03c93f-955c-4db3-936c-bfa3f7725a5f",
        "declaration-cancelled-producer-requested-en"
    )]
    [InlineData(
        "Producer requested to cancel",
        "GB-WLS",
        "e419a544-e1b3-4ea5-b8eb-a074e63aea1a",
        "declaration-cancelled-producer-requested-cy"
    )]
    [InlineData(
        "Compliance scheme requested to cancel",
        "GB-ENG",
        "3e03c93f-955c-4db3-936c-bfa3f7725a5f",
        "declaration-cancelled-producer-requested-en"
    )]
    [InlineData(
        "Compliance scheme requested to cancel",
        "GB-WLS",
        "e419a544-e1b3-4ea5-b8eb-a074e63aea1a",
        "declaration-cancelled-producer-requested-cy"
    )]
    public async Task WhenKnownReasonIsRead_ShouldUseConfiguredDefaultAndOriginalAction(
        string reason,
        string country,
        string template,
        string notificationType
    )
    {
        var fixture = new Fixture(reason, country);

        var commands = await fixture.Create();

        Assert.Equal(2, commands.Count);
        Assert.Equal(
            JsonSerializer.Serialize(fixture.Submitted.Create(fixture.Declaration)),
            JsonSerializer.Serialize(commands[0])
        );
        Assert.Equal("compliance-declaration:65f1f6570bb08052a8a27b01:submitted:v1", commands[0].IdempotencyKey);
        Assert.All(
            commands,
            command =>
            {
                Assert.Equal("compliance-declaration", command.Source?.Type);
                Assert.Equal("65f1f6570bb08052a8a27b01", command.Source?.Id);
            }
        );
        var command = commands[1];
        Assert.Equal(template, command.TemplateId);
        Assert.Equal(notificationType, command.NotificationType);
        Assert.Equal("submitter@example.test", command.EmailAddress);
        Assert.Equal(
            DateTimeOffset.Parse("2026-10-01T12:00:00.123Z", System.Globalization.CultureInfo.InvariantCulture),
            command.ActionOccurredAtUtc
        );
        Assert.True(command.IdempotencyKey.Length <= 128);
        Assert.DoesNotContain("submitter", command.IdempotencyKey);
        Assert.Equal(2027, command.Personalisation.GetProperty("year").GetInt32());
        Assert.Equal("Natural Resources Wales", command.Personalisation.GetProperty("regulator").GetString());
        Assert.Equal("stored@example.test", command.Personalisation.GetProperty("regulatorEmail").GetString());
        Assert.Equal(" First ", command.Personalisation.GetProperty("firstName").GetString());
        Assert.Equal(" Last ", command.Personalisation.GetProperty("lastName").GetString());
        Assert.Equal("certificate", command.Personalisation.GetProperty("certOrStatement").GetString());
        Assert.Equal("tystysgrif", command.Personalisation.GetProperty("certOrStatement_cy").GetString());
        if (country == "GB-WLS")
            Assert.Equal("Cyfoeth Naturiol Cymru", command.Personalisation.GetProperty("regulator_cy").GetString());
        else
            Assert.False(command.Personalisation.TryGetProperty("regulator_cy", out _));
        await fixture
            .Reader.Received(1)
            .ReadDeclaration(Fixture.OrganisationId, Fixture.DeclarationId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WhenOnlyCancellationTimeChanges_ShouldUseDistinctDurableIdentity()
    {
        var fixture = new Fixture();
        var original = (await fixture.Create())[1];

        Assert.Equal(
            "compliance-declaration:65f1f6570bb08052a8a27b01:cancelled:v1:c7567b8761d74234c689a1fa651d27736456262add7e749cad744dcde588b3fa",
            original.IdempotencyKey
        );

        var audit = fixture.Declaration.Audit!.ToArray();
        audit[1] = audit[1] with { Timestamp = "2026-10-02T12:00:00.123Z" };
        fixture.Declaration = fixture.Declaration with { Audit = audit };

        var later = (await fixture.Create())[1];

        Assert.Equal(original.EmailAddress, later.EmailAddress);
        Assert.Equal(original.Personalisation.GetRawText(), later.Personalisation.GetRawText());
        Assert.NotEqual(original.IdempotencyKey, later.IdempotencyKey);
    }

    [Theory]
    [InlineData("Producer requested to cancel ")]
    [InlineData("producer requested to cancel")]
    [InlineData("Unknown reason")]
    public async Task WhenReasonIsUnknown_ShouldFailWithoutLiveReadsOrPredecessor(string reason)
    {
        var fixture = new Fixture(reason);
        fixture.Declaration = fixture.Declaration with { Audit = [fixture.Declaration.Audit![1]] };

        var exception = await Assert.ThrowsAsync<SubmissionProcessingException>(fixture.Create);
        Assert.Equal("unknown-cancellation-reason", exception.Reason);

        await fixture.Live.DidNotReceive().Read(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await fixture
            .Accounts.DidNotReceive()
            .ReadOrganisationWithPersons(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("missing-audit")]
    [InlineData("missing-reason")]
    [InlineData("offset-free-time")]
    [InlineData("invalid-year")]
    public async Task WhenRequiredCancellationDataIsMalformed_ShouldFailBeforeLiveEnrichment(string scenario)
    {
        var fixture = new Fixture();
        var cancelled = fixture.Declaration.Audit![1];
        fixture.Declaration = scenario switch
        {
            "missing-audit" => fixture.Declaration with { Audit = [fixture.Declaration.Audit[0]] },
            "missing-reason" => fixture.Declaration with
            {
                Audit = [fixture.Declaration.Audit[0], cancelled with { Reason = null }],
            },
            "offset-free-time" => fixture.Declaration with
            {
                Audit = [fixture.Declaration.Audit[0], cancelled with { Timestamp = "2026-10-01T12:00:00" }],
            },
            _ => fixture.Declaration with { ObligationYear = 0 },
        };

        var failure = await Assert.ThrowsAsync<SubmissionProcessingException>(fixture.Create);

        Assert.Equal("invalid-declaration-data", failure.Reason);
        await fixture.Live.DidNotReceive().Read(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WhenPersistedCountryIsWelshButLiveCountryIsEnglish_ShouldUseEnglishCancellationAndWelshPredecessor()
    {
        var fixture = new Fixture(country: "GB-ENG");
        fixture.Declaration = fixture.Declaration with
        {
            Organisation = fixture.Declaration.Organisation! with { BusinessCountry = "GB-WLS" },
        };

        var commands = await fixture.Create();

        Assert.Equal(2, commands.Count);
        Assert.Equal("b3223b0b-a467-40c1-9150-f78b76d11fd8", commands[0].TemplateId);
        Assert.Equal("3e03c93f-955c-4db3-936c-bfa3f7725a5f", commands[1].TemplateId);
        Assert.False(commands[1].Personalisation.TryGetProperty("regulator_cy", out _));
    }

    [Fact]
    public async Task WhenLiveOrganisationIsNotFound_ShouldFailBeforeAccountLookup()
    {
        var fixture = new Fixture();
        fixture
            .Live.Read(Fixture.OrganisationId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<LiveOrganisation?>(null));

        var error = await Assert.ThrowsAsync<SubmissionProcessingException>(fixture.Create);

        Assert.Equal("organisation-not-found", error.Reason);
        await fixture
            .Accounts.DidNotReceive()
            .ReadOrganisationWithPersons(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await fixture
            .Accounts.DidNotReceive()
            .SearchOrganisationsByCompaniesHouseNumbers(
                Arg.Any<IReadOnlyCollection<string>>(),
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task WhenLiveIdentityDiffers_ShouldFailBeforeAccountLookup()
    {
        var fixture = new Fixture();
        fixture
            .Live.Read(Fixture.OrganisationId, Arg.Any<CancellationToken>())
            .Returns(new LiveOrganisation(Guid.NewGuid(), "GB-WLS", "12345678"));

        var error = await Assert.ThrowsAsync<SubmissionProcessingException>(fixture.Create);

        Assert.Equal("identity-mismatch", error.Reason);
        await fixture
            .Accounts.DidNotReceive()
            .ReadOrganisationWithPersons(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await fixture
            .Accounts.DidNotReceive()
            .SearchOrganisationsByCompaniesHouseNumbers(
                Arg.Any<IReadOnlyCollection<string>>(),
                Arg.Any<CancellationToken>()
            );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenNoPersonsAreEligible_ShouldReturnEmptyWithoutReconstructingInvalidSubmittedAudit(bool absent)
    {
        var fixture = new Fixture();
        fixture.Declaration = fixture.Declaration with { Audit = [fixture.Declaration.Audit![1]] };
        fixture.Persons = absent
            ? null
            : new OrganisationWithPersons([
                new OrganisationPerson(null, " ", "Last", "primary@example.test", "Approved Person"),
            ]);

        Assert.Empty(await fixture.Create());
    }

    [Fact]
    public async Task WhenOnlyPrimaryContactIsEligible_ShouldKeepTheOriginalSubmittedPredecessor()
    {
        var fixture = new Fixture();
        fixture.Persons = new OrganisationWithPersons([
            new OrganisationPerson(null, " Primary ", " Contact ", " primary@example.test ", "Approved Person"),
        ]);

        var commands = await fixture.Create();

        Assert.Equal(2, commands.Count);
        Assert.Equal("submitter@example.test", commands[0].EmailAddress);
        Assert.Equal("primary@example.test", commands[1].EmailAddress);
        Assert.Equal(" Primary ", commands[1].Personalisation.GetProperty("firstName").GetString());
        Assert.Equal(" Contact ", commands[1].Personalisation.GetProperty("lastName").GetString());
    }

    [Fact]
    public async Task WhenPrimaryIsEligibleButSubmittedAuditIsMissing_ShouldBlockAllCommands()
    {
        var fixture = new Fixture();
        fixture.Declaration = fixture.Declaration with { Audit = [fixture.Declaration.Audit![1]] };
        fixture.Persons = new OrganisationWithPersons([
            new OrganisationPerson(null, "First", "Last", "primary@example.test", "Approved Person"),
        ]);

        await Assert.ThrowsAsync<SubmissionProcessingException>(fixture.Create);
    }

    [Fact]
    public async Task WhenFirstApprovedPersonIsIncomplete_ShouldNotFallThroughToLaterContact()
    {
        var fixture = new Fixture();
        fixture.Persons = new OrganisationWithPersons([
            new OrganisationPerson(null, "First", "Last", "primary@example.test", "approved person"),
            new OrganisationPerson(null, " ", "Last", "primary@example.test", "Approved Person"),
            new OrganisationPerson(null, "First", "Last", "later@example.test", "Approved Person"),
        ]);

        Assert.Empty(await fixture.Create());
        fixture.AssertNoSendLog("no-eligible-recipient");
    }

    [Fact]
    public async Task WhenRecipientMatchesByIdAndPrimaryDuplicatesSavedEmail_ShouldKeepSubmitterNames()
    {
        var fixture = new Fixture();
        fixture.Persons = new OrganisationWithPersons([
            new OrganisationPerson(Fixture.SubmitterId, " Saved ", " Name ", "changed@example.test", "Other"),
            new OrganisationPerson(null, "Primary", "Person", " SUBMITTER@EXAMPLE.TEST ", "Approved Person"),
        ]);

        var command = (await fixture.Create())[1];

        Assert.Equal("submitter@example.test", command.EmailAddress);
        Assert.Equal(" Saved ", command.Personalisation.GetProperty("firstName").GetString());
        Assert.Equal(" Name ", command.Personalisation.GetProperty("lastName").GetString());
    }

    [Fact]
    public async Task WhenSubmitterMatchesOnlyByEmailWithDifferentCase_ShouldKeepSavedRecipientAndAccountNames()
    {
        var fixture = new Fixture();
        fixture.Persons = new OrganisationWithPersons([
            new OrganisationPerson(null, " Email ", " Match ", "sUbMiTtEr@EXAMPLE.TEST", "Other"),
        ]);

        var commands = await fixture.Create();

        Assert.Equal(2, commands.Count);
        Assert.Equal("submitter@example.test", commands[1].EmailAddress);
        Assert.Equal(" Email ", commands[1].Personalisation.GetProperty("firstName").GetString());
        Assert.Equal(" Match ", commands[1].Personalisation.GetProperty("lastName").GetString());
    }

    [Fact]
    public async Task WhenAddressCaseWouldChangeOrdinalOrder_ShouldSortIgnoringCaseBeforeNormalising()
    {
        var fixture = new Fixture();
        var audit = fixture.Declaration.Audit!.ToArray();
        audit[0] = audit[0] with { User = audit[0].User! with { Email = " a@example.test " } };
        fixture.Declaration = fixture.Declaration with { Audit = audit };
        fixture.Persons = new OrganisationWithPersons([
            Fixture.Submitter(),
            new OrganisationPerson(null, "Primary", "Person", " Z@example.test ", "Approved Person"),
        ]);

        var commands = await fixture.Create();

        Assert.Equal(3, commands.Count);
        Assert.Equal("a@example.test", commands[1].EmailAddress);
        Assert.Equal("z@example.test", commands[2].EmailAddress);
    }

    [Fact]
    public async Task WhenDistinctContactsExist_ShouldSortTrimmedAddressesAndKeepStableKeysAcrossLiveChanges()
    {
        var fixture = new Fixture();
        fixture.Persons = new OrganisationWithPersons([
            Fixture.Submitter(),
            new OrganisationPerson(null, "Primary", "Person", " A@example.test ", "Approved Person"),
        ]);
        var first = await fixture.Create();
        Assert.Equal(SortedRecipientAddresses, first.Skip(1).Select(command => command.EmailAddress));
        fixture.Persons = new OrganisationWithPersons([
            Fixture.Submitter() with
            {
                FirstName = "Changed",
            },
            new OrganisationPerson(null, "New", "Person", "new@example.test", "Approved Person"),
        ]);

        var second = await fixture.Create();

        Assert.Equal(first[2].IdempotencyKey, second[2].IdempotencyKey);
        Assert.NotEqual(first[2].Personalisation.GetRawText(), second[2].Personalisation.GetRawText());
        Assert.NotEqual(first[1].IdempotencyKey, second[1].IdempotencyKey);
        Assert.Equal(JsonSerializer.Serialize(first[0]), JsonSerializer.Serialize(second[0]));
    }

    [Theory]
    [InlineData("valid-and-blank", true)]
    [InlineData("none", false)]
    [InlineData("multiple", false)]
    [InlineData("invalid", false)]
    [InlineData("case-mismatch", false)]
    [InlineData("not-scheme", false)]
    [InlineData("missing-number", false)]
    public async Task WhenResolvingScheme_ShouldFilterBeforeUniquenessAndGuidValidation(string scenario, bool eligible)
    {
        var fixture = new Fixture();
        fixture.Declaration = fixture.Declaration with
        {
            Organisation = fixture.Declaration.Organisation! with { RegistrationType = "ComplianceScheme" },
        };
        fixture
            .Live.Read(Fixture.OrganisationId, Arg.Any<CancellationToken>())
            .Returns(
                new LiveOrganisation(Fixture.OrganisationId, "GB-WLS", scenario == "missing-number" ? null : "AB123456")
            );
        var accountId = Guid.NewGuid();
        IReadOnlyList<AccountOrganisation> results = scenario switch
        {
            "valid-and-blank" =>
            [
                new(accountId.ToString(), "AB123456", true),
                new(" ", "AB123456", true),
                new(Guid.NewGuid().ToString(), "AB123456", false),
            ],
            "multiple" =>
            [
                new(accountId.ToString(), "AB123456", true),
                new(Guid.NewGuid().ToString(), "AB123456", true),
            ],
            "invalid" => [new("invalid", "AB123456", true)],
            "case-mismatch" => [new(accountId.ToString(), "ab123456", true)],
            "not-scheme" => [new(accountId.ToString(), "AB123456", false)],
            _ => [],
        };
        fixture
            .Accounts.SearchOrganisationsByCompaniesHouseNumbers(
                Arg.Any<IReadOnlyCollection<string>>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(results);
        fixture
            .Accounts.ReadOrganisationWithPersons(accountId, Arg.Any<CancellationToken>())
            .Returns(_ => fixture.Persons);

        var commands = await fixture.Create();

        if (eligible)
        {
            Assert.Equal(2, commands.Count);
            Assert.Equal("statement", commands[1].Personalisation.GetProperty("certOrStatement").GetString());
            await fixture.Accounts.Received(1).ReadOrganisationWithPersons(accountId, Arg.Any<CancellationToken>());
        }
        else
        {
            Assert.Empty(commands);
            fixture.AssertNoSendLog("unresolved-compliance-scheme");
            await fixture
                .Accounts.DidNotReceive()
                .ReadOrganisationWithPersons(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        }
    }

    [Fact]
    public async Task WhenAccountDependencyFails_ShouldRetainFailureRatherThanReturnEmpty()
    {
        var fixture = new Fixture();
        fixture
            .Accounts.ReadOrganisationWithPersons(Fixture.OrganisationId, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<OrganisationWithPersons?>(new HttpRequestException("private response")));

        await Assert.ThrowsAsync<HttpRequestException>(fixture.Create);
    }

    [Theory]
    [InlineData("persons", "account-read-unavailable")]
    [InlineData("oauth", "account-read-unavailable")]
    [InlineData("search", "account-search-unavailable")]
    [InlineData("organisation", "organisation-read-unavailable")]
    public async Task WhenDependencyResponseIsInvalid_ShouldIdentifyTheDependency(string boundary, string reason)
    {
        var fixture = new Fixture();
        if (boundary == "search")
        {
            fixture.Declaration = fixture.Declaration with
            {
                Organisation = fixture.Declaration.Organisation! with { RegistrationType = "ComplianceScheme" },
            };
            fixture
                .Accounts.SearchOrganisationsByCompaniesHouseNumbers(
                    Arg.Any<IReadOnlyCollection<string>>(),
                    Arg.Any<CancellationToken>()
                )
                .Returns(
                    Task.FromException<IReadOnlyList<AccountOrganisation>>(
                        new InvalidOperationException("private null search response")
                    )
                );
        }
        else if (boundary == "organisation")
        {
            fixture
                .Live.Read(Fixture.OrganisationId, Arg.Any<CancellationToken>())
                .Returns(
                    Task.FromException<LiveOrganisation?>(new JsonException("private malformed organisation response"))
                );
        }
        else
        {
            Exception failure =
                boundary == "oauth"
                    ? new InvalidOperationException("private invalid token response")
                    : new JsonException("private malformed persons response");
            fixture
                .Accounts.ReadOrganisationWithPersons(Fixture.OrganisationId, Arg.Any<CancellationToken>())
                .Returns(Task.FromException<OrganisationWithPersons?>(failure));
        }

        var exception = await Assert.ThrowsAsync<SubmissionProcessingException>(fixture.Create);

        Assert.Equal(reason, exception.Reason);
        Assert.DoesNotContain("private", exception.Message);
    }

    private sealed class Fixture
    {
        public const string DeclarationId = "65f1f6570bb08052a8a27b01";
        public static readonly Guid OrganisationId = Guid.Parse("fbf1ba88-cd09-42f6-aad5-0ddfc29e5765");
        public static readonly Guid SubmitterId = Guid.Parse("5bd7385b-042e-464a-bba8-b9dcc0dce677");
        public IWasteObligationsClient Reader { get; } = Substitute.For<IWasteObligationsClient>();
        public IWasteOrganisationsClient Live { get; } = Substitute.For<IWasteOrganisationsClient>();
        public IAccountBackendClient Accounts { get; } = Substitute.For<IAccountBackendClient>();
        public SubmittedEmailProducer Submitted { get; }
        public Declaration Declaration { get; set; }
        public OrganisationWithPersons? Persons { get; set; } = new([Submitter()]);
        private readonly CancellationEmailProducer _producer;
        private readonly ILogger<CancellationEmailProducer> _logger = Substitute.For<
            ILogger<CancellationEmailProducer>
        >();

        public Fixture(string reason = "Producer requested to cancel", string country = "GB-ENG")
        {
            _logger.IsEnabled(LogLevel.Information).Returns(true);
            Declaration = new Declaration(
                DeclarationId,
                3,
                2026,
                new DeclarationOrganisation(
                    OrganisationId.ToString(),
                    "GB-ENG",
                    "DirectProducer",
                    "NRW",
                    "stored@example.test"
                ),
                [
                    new DeclarationAudit(
                        "Submitted",
                        "2026-09-20T11:12:13Z",
                        new DeclarationUser("Original Submitter", " Submitter@Example.test ", SubmitterId.ToString())
                    ),
                    new DeclarationAudit("Cancelled", "2026-10-01T12:00:00.123999Z", null, reason),
                ]
            );
            Reader
                .ReadDeclaration(OrganisationId, DeclarationId, Arg.Any<CancellationToken>())
                .Returns(_ => Declaration);
            Live.Read(OrganisationId, Arg.Any<CancellationToken>())
                .Returns(new LiveOrganisation(OrganisationId, country, "AB123456"));
            Accounts.ReadOrganisationWithPersons(OrganisationId, Arg.Any<CancellationToken>()).Returns(_ => Persons);
            Submitted = new SubmittedEmailProducer(
                Reader,
                Options.Create(new NotificationDefinitionsOptions()),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<SubmittedEmailProducer>.Instance
            );
            _producer = new CancellationEmailProducer(
                Submitted,
                Live,
                Accounts,
                Options.Create(new NotificationDefinitionsOptions()),
                _logger
            );
        }

        public void AssertNoSendLog(string reason)
        {
            var call = Assert.Single(_logger.ReceivedCalls(), call => call.GetMethodInfo().Name == "Log");
            var arguments = call.GetArguments();
            Assert.Equal(LogLevel.Information, arguments[0]);
            Assert.Null(arguments[3]);
            var state = Assert.IsType<IEnumerable<KeyValuePair<string, object>>>(arguments[2], exactMatch: false);
            Assert.Collection(
                state,
                entry =>
                {
                    Assert.Equal("ReasonCode", entry.Key);
                    Assert.Equal(reason, entry.Value);
                },
                entry =>
                {
                    Assert.Equal("{OriginalFormat}", entry.Key);
                    Assert.Equal("Cancellation produced no notifications ({ReasonCode})", entry.Value);
                }
            );
        }

        public static OrganisationPerson Submitter() =>
            new(SubmitterId, " First ", " Last ", "SUBMITTER@example.test", "Other");

        public Task<IReadOnlyList<Defra.WasteObligations.Consumer.Commands.NotificationCommand>> Create() =>
            _producer.Create(OrganisationId, DeclarationId, 2, null, TestContext.Current.CancellationToken);
    }
}
