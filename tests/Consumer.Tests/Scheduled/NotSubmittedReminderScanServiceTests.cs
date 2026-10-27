using Defra.WasteObligations.Consumer.Commands;
using Defra.WasteObligations.Consumer.Delivery;
using Defra.WasteObligations.Consumer.Notifications;
using Defra.WasteObligations.Consumer.Scheduled;
using Defra.WasteObligations.Consumer.Services.AccountBackend;
using Defra.WasteObligations.Consumer.Services.WasteObligations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Defra.WasteObligations.Consumer.Tests.Scheduled;

public sealed class NotSubmittedReminderScanServiceTests
{
    private const string Cutover = "2026-09-01T00:00:00Z";

    [Fact]
    public void GetSkipReason_ShouldDelegateToCutoverGuard()
    {
        var service = CreateService(
            Substitute.For<IWasteObligationsClient>(),
            Substitute.For<IAccountBackendClient>(),
            Substitute.For<INotificationCommandPublisher>(),
            enabled: false
        );

        Assert.Equal(NotSubmittedReminderCutoverGuard.DisabledReason, service.GetSkipReason());
    }

    [Fact]
    public async Task RunFullScan_WhenDisabled_ShouldSkipWithoutCallingObligations()
    {
        var obligations = Substitute.For<IWasteObligationsClient>();
        var service = CreateService(
            obligations,
            Substitute.For<IAccountBackendClient>(),
            Substitute.For<INotificationCommandPublisher>(),
            enabled: false
        );

        var result = await service.RunFullScan(TestContext.Current.CancellationToken);

        Assert.Equal(
            new NotSubmittedReminderScanResult(0, 0, 0, NotSubmittedReminderCutoverGuard.DisabledReason),
            result
        );
        await obligations
            .DidNotReceive()
            .SearchUnsubmittedOrganisations(
                Arg.Any<int>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task RunFullScan_WhenCutoverMisconfigured_ShouldReturnSkipReason()
    {
        var obligations = Substitute.For<IWasteObligationsClient>();
        var service = CreateService(
            obligations,
            Substitute.For<IAccountBackendClient>(),
            Substitute.For<INotificationCommandPublisher>(),
            cutover: null
        );

        var result = await service.RunFullScan(TestContext.Current.CancellationToken);

        Assert.Equal(
            new NotSubmittedReminderScanResult(0, 0, 0, NotSubmittedReminderCutoverGuard.CutoverMisconfiguredReason),
            result
        );
        await obligations
            .DidNotReceive()
            .SearchUnsubmittedOrganisations(
                Arg.Any<int>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task RunFullScan_WhenOrganisationEligible_ShouldPublishCommands()
    {
        var organisationId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var obligations = Substitute.For<IWasteObligationsClient>();
        obligations
            .SearchUnsubmittedOrganisations(2026, "DirectProducer", 1, 100, Arg.Any<CancellationToken>())
            .Returns(CreatePage([CreateRow(organisationId, "GB-ENG")], total: 1));

        var accounts = Substitute.For<IAccountBackendClient>();
        accounts
            .ReadOrganisationWithPersons(organisationId, Arg.Any<CancellationToken>())
            .Returns(CreateOrganisation("approved@example.test"));

        var publisher = Substitute.For<INotificationCommandPublisher>();
        var service = CreateService(obligations, accounts, publisher);

        var result = await service.RunFullScan(TestContext.Current.CancellationToken);

        Assert.Equal(new NotSubmittedReminderScanResult(1, 1, 0, null), result);
        await publisher.Received(1).Publish(Arg.Any<NotificationCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunFullScan_WhenAccountReadFails_ShouldSkipOrganisation()
    {
        var organisationId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var obligations = Substitute.For<IWasteObligationsClient>();
        obligations
            .SearchUnsubmittedOrganisations(2026, "DirectProducer", 1, 100, Arg.Any<CancellationToken>())
            .Returns(CreatePage([CreateRow(organisationId)]));

        var accounts = Substitute.For<IAccountBackendClient>();
        accounts
            .ReadOrganisationWithPersons(organisationId, Arg.Any<CancellationToken>())
            .Throws(new HttpRequestException("unavailable"));

        var publisher = Substitute.For<INotificationCommandPublisher>();
        var service = CreateService(obligations, accounts, publisher);

        var result = await service.RunFullScan(TestContext.Current.CancellationToken);

        Assert.Equal(new NotSubmittedReminderScanResult(1, 0, 1, null), result);
        await publisher.DidNotReceive().Publish(Arg.Any<NotificationCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunFullScan_WhenNoEligibleRecipients_ShouldSkipOrganisation()
    {
        var organisationId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var obligations = Substitute.For<IWasteObligationsClient>();
        obligations
            .SearchUnsubmittedOrganisations(2026, "DirectProducer", 1, 100, Arg.Any<CancellationToken>())
            .Returns(CreatePage([CreateRow(organisationId)]));

        var accounts = Substitute.For<IAccountBackendClient>();
        accounts
            .ReadOrganisationWithPersons(organisationId, Arg.Any<CancellationToken>())
            .Returns(
                new OrganisationWithPersons([new OrganisationPerson(Guid.NewGuid(), "A", "B", "x@test", "Other")])
            );

        var service = CreateService(obligations, accounts, Substitute.For<INotificationCommandPublisher>());

        var result = await service.RunFullScan(TestContext.Current.CancellationToken);

        Assert.Equal(new NotSubmittedReminderScanResult(1, 0, 1, null), result);
    }

    [Fact]
    public async Task RunFullScan_WhenBusinessCountryUnknown_ShouldStillPublishUsingDefault()
    {
        var organisationId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
        var obligations = Substitute.For<IWasteObligationsClient>();
        obligations
            .SearchUnsubmittedOrganisations(2026, "DirectProducer", 1, 100, Arg.Any<CancellationToken>())
            .Returns(CreatePage([CreateRow(organisationId, businessCountry: null)]));

        var accounts = Substitute.For<IAccountBackendClient>();
        accounts
            .ReadOrganisationWithPersons(organisationId, Arg.Any<CancellationToken>())
            .Returns(CreateOrganisation("approved@example.test"));

        var publisher = Substitute.For<INotificationCommandPublisher>();
        var service = CreateService(obligations, accounts, publisher);

        var result = await service.RunFullScan(TestContext.Current.CancellationToken);

        Assert.Equal(new NotSubmittedReminderScanResult(1, 1, 0, null), result);
        await publisher.Received(1).Publish(Arg.Any<NotificationCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunFullScan_WhenPublishFails_ShouldCountPartialSuccess()
    {
        var first = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
        var second = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        var obligations = Substitute.For<IWasteObligationsClient>();
        obligations
            .SearchUnsubmittedOrganisations(2026, "DirectProducer", 1, 100, Arg.Any<CancellationToken>())
            .Returns(CreatePage([CreateRow(first), CreateRow(second)], total: 2));

        var accounts = Substitute.For<IAccountBackendClient>();
        accounts
            .ReadOrganisationWithPersons(first, Arg.Any<CancellationToken>())
            .Returns(CreateOrganisation("first@example.test"));
        accounts
            .ReadOrganisationWithPersons(second, Arg.Any<CancellationToken>())
            .Returns(CreateOrganisation("second@example.test"));

        var publisher = Substitute.For<INotificationCommandPublisher>();
        publisher
            .Publish(Arg.Any<NotificationCommand>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.CompletedTask, _ => Task.FromException(new InvalidOperationException("publish failed")));

        var service = CreateService(obligations, accounts, publisher);

        var result = await service.RunFullScan(TestContext.Current.CancellationToken);

        Assert.Equal(new NotSubmittedReminderScanResult(2, 1, 1, null), result);
    }

    [Fact]
    public async Task RunFullScan_WhenResultsSpanPages_ShouldProcessEveryOrganisation()
    {
        var first = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var second = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var obligations = Substitute.For<IWasteObligationsClient>();
        obligations
            .SearchUnsubmittedOrganisations(
                2026,
                "DirectProducer",
                Arg.Any<int>(),
                1,
                Arg.Any<CancellationToken>()
            )
            .Returns(
                CreatePage([CreateRow(first)], total: 2, page: 1, pageSize: 1),
                CreatePage([CreateRow(second)], total: 2, page: 2, pageSize: 1)
            );

        var accounts = Substitute.For<IAccountBackendClient>();
        accounts
            .ReadOrganisationWithPersons(first, Arg.Any<CancellationToken>())
            .Returns(CreateOrganisation("first@example.test"));
        accounts
            .ReadOrganisationWithPersons(second, Arg.Any<CancellationToken>())
            .Returns(CreateOrganisation("second@example.test"));

        var publisher = Substitute.For<INotificationCommandPublisher>();
        var service = CreateService(obligations, accounts, publisher, pageSize: 1);

        var result = await service.RunFullScan(TestContext.Current.CancellationToken);

        Assert.Equal(new NotSubmittedReminderScanResult(2, 2, 0, null), result);
        await publisher.Received(2).Publish(Arg.Any<NotificationCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunFullScan_WhenObligationYearOverrideAbsent_ShouldDeriveYearFromUkCalendar()
    {
        var obligations = Substitute.For<IWasteObligationsClient>();
        obligations
            .SearchUnsubmittedOrganisations(2026, "DirectProducer", 1, 100, Arg.Any<CancellationToken>())
            .Returns(CreatePage([]));

        var service = CreateService(
            obligations,
            Substitute.For<IAccountBackendClient>(),
            Substitute.For<INotificationCommandPublisher>(),
            obligationYearOverride: null
        );

        await service.RunFullScan(TestContext.Current.CancellationToken);

        await obligations
            .Received(1)
            .SearchUnsubmittedOrganisations(2026, "DirectProducer", 1, 100, Arg.Any<CancellationToken>());
    }

    private static NotSubmittedReminderScanService CreateService(
        IWasteObligationsClient obligations,
        IAccountBackendClient accounts,
        INotificationCommandPublisher publisher,
        string now = "2026-12-01T10:00:00Z",
        bool enabled = true,
        string? cutover = Cutover,
        int? obligationYearOverride = 2026,
        int pageSize = 100
    )
    {
        var reminder = Options.Create(
            new NotSubmittedReminderOptions
            {
                Enabled = enabled,
                Month = 12,
                Day = 1,
                Hour = 10,
                SendWindow = TimeSpan.FromDays(7),
                PageSize = pageSize,
                ObligationYearOverride = obligationYearOverride,
            }
        );
        var delivery = Options.Create(
            new NotificationCommandDeliveryOptions
            {
                QueueUrl = "https://example.test/000000000000/queue.fifo",
                EmailDeliveryCutoverUtc = cutover,
                EvidenceDigestSecret = "secret",
                RecipientLaneSecret = "secret",
            }
        );
        var time = new FakeTimeProvider(DateTimeOffset.Parse(now, System.Globalization.CultureInfo.InvariantCulture));
        var support = new NotSubmittedReminderScanSupport(
            reminder,
            new NotSubmittedReminderPresentation(Options.Create(new NotificationDefinitionsOptions())),
            new NotSubmittedReminderOccurrence(reminder),
            new NotSubmittedReminderCutoverGuard(
                reminder,
                delivery,
                new NotSubmittedReminderOccurrence(reminder),
                time
            ),
            time
        );
        using var logs = LoggerFactory.Create(logging => logging.SetMinimumLevel(LogLevel.Trace));

        return new NotSubmittedReminderScanService(
            support,
            obligations,
            accounts,
            publisher,
            logs.CreateLogger<NotSubmittedReminderScanService>()
        );
    }

    private static UnsubmittedOrganisationsPage CreatePage(
        IReadOnlyList<UnsubmittedOrganisationRow> rows,
        int? total = null,
        int page = 1,
        int pageSize = 100
    ) => new(rows, total ?? rows.Count, page, pageSize);

    private static UnsubmittedOrganisationRow CreateRow(Guid organisationId, string? businessCountry = "GB-ENG") =>
        new(organisationId, 2026, "DirectProducer", businessCountry, "Synthetic Org", "REF-1");

    private static OrganisationWithPersons CreateOrganisation(string email) =>
        new([new OrganisationPerson(Guid.NewGuid(), "Approved", "Person", email, "Approved Person")]);

    private sealed class FakeTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
