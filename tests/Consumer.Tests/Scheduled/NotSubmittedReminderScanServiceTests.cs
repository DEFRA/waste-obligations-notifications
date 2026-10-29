using System.Globalization;
using Defra.WasteObligations.Consumer.Commands;
using Defra.WasteObligations.Consumer.Data.Entities;
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
    private const string LeaseId = "not-submitted-reminder-1:2026";

    [Fact]
    public async Task PrepareRequestedScan_WhenGuardSkips_ShouldReturnReasonWithoutTouchingRun()
    {
        var runs = CreateRuns();
        var service = CreateService(new() { Runs = runs, Now = "2026-11-30T10:00:00Z" });

        var skipReason = await service.PrepareRequestedScan(true, TestContext.Current.CancellationToken);

        Assert.Equal(NotSubmittedReminderCutoverGuard.OutsideSendWindowReason, skipReason);
        Assert.Empty(runs.ReceivedCalls());
    }

    [Fact]
    public async Task PrepareRequestedScan_WhenRunCompleted_ShouldReturnCompletedReason()
    {
        var runs = CreateRuns();
        runs.Read(NotSubmittedReminderScanService.Campaign, 2026, Arg.Any<CancellationToken>())
            .Returns(CreateRun(ScheduledCampaignRunStatus.Completed));
        var service = CreateService(new() { Runs = runs });

        var skipReason = await service.PrepareRequestedScan(false, TestContext.Current.CancellationToken);

        Assert.Equal(NotSubmittedReminderScanService.CompletedReason, skipReason);
    }

    [Fact]
    public async Task PrepareRequestedScan_WhenRunInProgress_ShouldAllowScan()
    {
        var runs = CreateRuns();
        runs.Read(NotSubmittedReminderScanService.Campaign, 2026, Arg.Any<CancellationToken>())
            .Returns(CreateRun(ScheduledCampaignRunStatus.InProgress));
        var service = CreateService(new() { Runs = runs });

        var skipReason = await service.PrepareRequestedScan(false, TestContext.Current.CancellationToken);

        Assert.Null(skipReason);
        await runs.DidNotReceive().Reopen(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PrepareRequestedScan_WhenReopenRequested_ShouldReopenRunAndAllowScan()
    {
        var runs = CreateRuns();
        var service = CreateService(new() { Runs = runs });

        var skipReason = await service.PrepareRequestedScan(true, TestContext.Current.CancellationToken);

        Assert.Null(skipReason);
        await runs.Received(1).Reopen(NotSubmittedReminderScanService.Campaign, 2026, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunFullScan_WhenRunAlreadyCompleted_ShouldReleaseLeaseWithoutCallingObligations()
    {
        var obligations = Substitute.For<IWasteObligationsClient>();
        var leases = CreateLeases();
        var runs = CreateRuns(ScheduledCampaignRunStart.Completed);
        var service = CreateService(
            new()
            {
                Obligations = obligations,
                Leases = leases,
                Runs = runs,
            }
        );

        var result = await service.RunFullScan(TestContext.Current.CancellationToken);

        Assert.Equal(
            new NotSubmittedReminderScanResult(0, 0, 0, 0, NotSubmittedReminderScanService.CompletedReason),
            result
        );
        Assert.Empty(obligations.ReceivedCalls());
        await runs.DidNotReceive()
            .RecordScan(
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<NotSubmittedReminderScanResult>(),
                Arg.Any<CancellationToken>()
            );
        await leases.Received(1).Release(LeaseId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunFullScan_WhenAnotherHostHoldsLease_ShouldSkipWithoutStartingRun()
    {
        var obligations = Substitute.For<IWasteObligationsClient>();
        var runs = CreateRuns();
        var service = CreateService(
            new()
            {
                Obligations = obligations,
                Leases = CreateLeases(acquired: false),
                Runs = runs,
            }
        );

        var result = await service.RunFullScan(TestContext.Current.CancellationToken);

        Assert.Equal(
            new NotSubmittedReminderScanResult(0, 0, 0, 0, NotSubmittedReminderScanService.ScanInProgressReason),
            result
        );
        Assert.Empty(obligations.ReceivedCalls());
        Assert.Empty(runs.ReceivedCalls());
    }

    [Fact]
    public async Task RunFullScan_WhenOutsideSendWindow_ShouldNotAcquireLeaseOrStartRun()
    {
        var leases = CreateLeases();
        var runs = CreateRuns();
        var service = CreateService(
            new()
            {
                Leases = leases,
                Runs = runs,
                Now = "2026-12-08T10:00:00Z",
            }
        );

        var result = await service.RunFullScan(TestContext.Current.CancellationToken);

        Assert.Equal(NotSubmittedReminderCutoverGuard.OutsideSendWindowReason, result.SkipReason);
        Assert.Empty(leases.ReceivedCalls());
        Assert.Empty(runs.ReceivedCalls());
    }

    [Fact]
    public async Task RunFullScan_WhenLeaseRenewalIsRejected_ShouldStopPublishingWithoutRecordingRun()
    {
        var first = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var second = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var obligations = Substitute.For<IWasteObligationsClient>();
        obligations
            .SearchUnsubmittedOrganisations(2026, "DirectProducer", 1, 100, Arg.Any<CancellationToken>())
            .Returns(CreatePage([CreateRow(first), CreateRow(second)]));

        var accounts = Substitute.For<IAccountBackendClient>();
        accounts
            .ReadOrganisationWithPersons(first, Arg.Any<CancellationToken>())
            .Returns(CreateOrganisation("first@example.test"));
        accounts
            .ReadOrganisationWithPersons(second, Arg.Any<CancellationToken>())
            .Returns(callInfo => WaitForCancellation<OrganisationWithPersons?>(callInfo.Arg<CancellationToken>()));

        var leases = CreateLeases();
        leases.TryRenew(LeaseId, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(false);
        var runs = CreateRuns();
        var publisher = Substitute.For<INotificationCommandPublisher>();
        var service = CreateService(
            new()
            {
                Obligations = obligations,
                Accounts = accounts,
                Publisher = publisher,
                Leases = leases,
                Runs = runs,
                LeaseRenewalInterval = TimeSpan.FromMilliseconds(50),
            }
        );

        var result = await service.RunFullScan(TestContext.Current.CancellationToken);

        Assert.Equal(
            new NotSubmittedReminderScanResult(0, 0, 0, 0, NotSubmittedReminderScanService.LeaseLostReason),
            result
        );
        await publisher.Received(1).Publish(Arg.Any<NotificationCommand>(), Arg.Any<CancellationToken>());
        await runs.DidNotReceive()
            .RecordScan(
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<NotSubmittedReminderScanResult>(),
                Arg.Any<CancellationToken>()
            );
        await leases.Received(1).Release(LeaseId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunFullScan_WhenLeaseRenewalIsNotConfirmedBeforeDeadline_ShouldStopScan()
    {
        var organisationId = Guid.Parse("66666666-6666-6666-6666-666666666666");
        var obligations = Substitute.For<IWasteObligationsClient>();
        obligations
            .SearchUnsubmittedOrganisations(2026, "DirectProducer", 1, 100, Arg.Any<CancellationToken>())
            .Returns(CreatePage([CreateRow(organisationId)]));

        var accounts = Substitute.For<IAccountBackendClient>();
        accounts
            .ReadOrganisationWithPersons(organisationId, Arg.Any<CancellationToken>())
            .Returns(callInfo => WaitForCancellation<OrganisationWithPersons?>(callInfo.Arg<CancellationToken>()));

        var leases = CreateLeases();
        leases
            .TryRenew(LeaseId, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => WaitForCancellation<bool>(callInfo.Arg<CancellationToken>()));
        var runs = CreateRuns();
        var publisher = Substitute.For<INotificationCommandPublisher>();
        var service = CreateService(
            new()
            {
                Obligations = obligations,
                Accounts = accounts,
                Publisher = publisher,
                Leases = leases,
                Runs = runs,
                LeaseDuration = TimeSpan.FromMilliseconds(300),
                LeaseRenewalInterval = TimeSpan.FromMilliseconds(100),
            }
        );

        var result = await service.RunFullScan(TestContext.Current.CancellationToken);

        Assert.Equal(NotSubmittedReminderScanService.LeaseLostReason, result.SkipReason);
        await publisher.DidNotReceive().Publish(Arg.Any<NotificationCommand>(), Arg.Any<CancellationToken>());
        await runs.DidNotReceive()
            .RecordScan(
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<NotSubmittedReminderScanResult>(),
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task RunFullScan_WhenHostIsStopping_ShouldNotCountCancellationAsRetryableFailure()
    {
        var organisationId = Guid.Parse("77777777-7777-7777-7777-777777777777");
        var obligations = Substitute.For<IWasteObligationsClient>();
        obligations
            .SearchUnsubmittedOrganisations(2026, "DirectProducer", 1, 100, Arg.Any<CancellationToken>())
            .Returns(CreatePage([CreateRow(organisationId)]));

        using var stopping = new CancellationTokenSource();
        var accounts = Substitute.For<IAccountBackendClient>();
        accounts
            .ReadOrganisationWithPersons(organisationId, Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                await stopping.CancelAsync();
                await Task.Delay(Timeout.Infinite, callInfo.Arg<CancellationToken>());
                return (OrganisationWithPersons?)null;
            });

        var runs = CreateRuns();
        var service = CreateService(
            new()
            {
                Obligations = obligations,
                Accounts = accounts,
                Runs = runs,
            }
        );

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunFullScan(stopping.Token));

        await runs.DidNotReceive()
            .RecordScan(
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<NotSubmittedReminderScanResult>(),
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task RunFullScan_WhenScanFinishes_ShouldHoldConfiguredLeaseAndRecordResult()
    {
        var organisationId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var obligations = Substitute.For<IWasteObligationsClient>();
        obligations
            .SearchUnsubmittedOrganisations(2026, "DirectProducer", 1, 100, Arg.Any<CancellationToken>())
            .Returns(CreatePage([CreateRow(organisationId)]));

        var accounts = Substitute.For<IAccountBackendClient>();
        accounts
            .ReadOrganisationWithPersons(organisationId, Arg.Any<CancellationToken>())
            .Returns(CreateOrganisation("approved@example.test"));

        var leases = CreateLeases();
        var runs = CreateRuns();
        var service = CreateService(
            new()
            {
                Obligations = obligations,
                Accounts = accounts,
                Leases = leases,
                Runs = runs,
            }
        );

        var result = await service.RunFullScan(TestContext.Current.CancellationToken);

        Assert.Equal(new NotSubmittedReminderScanResult(1, 1, 0, 0, null), result);
        await leases.Received(1).TryAcquire(LeaseId, TimeSpan.FromMinutes(45), Arg.Any<CancellationToken>());
        await runs.Received(1).TryStart(NotSubmittedReminderScanService.Campaign, 2026, Arg.Any<CancellationToken>());
        await runs.Received(1)
            .RecordScan(NotSubmittedReminderScanService.Campaign, 2026, result, Arg.Any<CancellationToken>());
        await leases.Received(1).Release(LeaseId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunFullScan_WhenUnsubmittedSearchFails_ShouldReleaseLeaseAndRethrow()
    {
        var obligations = Substitute.For<IWasteObligationsClient>();
        obligations
            .SearchUnsubmittedOrganisations(2026, "DirectProducer", 1, 100, Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("unavailable"));

        var leases = CreateLeases();
        var runs = CreateRuns();
        var service = CreateService(
            new()
            {
                Obligations = obligations,
                Leases = leases,
                Runs = runs,
            }
        );

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            service.RunFullScan(TestContext.Current.CancellationToken)
        );

        await leases.Received(1).Release(LeaseId, Arg.Any<CancellationToken>());
        await runs.DidNotReceive()
            .RecordScan(
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<NotSubmittedReminderScanResult>(),
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task RunFullScan_WhenReleaseAlsoFails_ShouldRethrowOriginalFailure()
    {
        var obligations = Substitute.For<IWasteObligationsClient>();
        obligations
            .SearchUnsubmittedOrganisations(2026, "DirectProducer", 1, 100, Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("unavailable"));

        var leases = CreateLeases();
        leases
            .Release(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("mongo unavailable"));
        var service = CreateService(new() { Obligations = obligations, Leases = leases });

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            service.RunFullScan(TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task GetRunStatus_WhenNoRunRecorded_ShouldReportNotStartedWithSendWindow()
    {
        var service = CreateService(new());

        var status = await service.GetRunStatus(TestContext.Current.CancellationToken);

        Assert.Equal(
            new NotSubmittedReminderRunStatus(
                NotSubmittedReminderScanService.Campaign,
                2026,
                DateTimeOffset.Parse("2026-12-01T10:00:00Z", CultureInfo.InvariantCulture),
                DateTimeOffset.Parse("2026-12-08T10:00:00Z", CultureInfo.InvariantCulture),
                "not-started",
                0,
                null,
                null,
                null,
                0,
                0,
                0,
                0
            ),
            status
        );
    }

    [Fact]
    public async Task GetRunStatus_WhenRunCompleted_ShouldReportRecordedCounts()
    {
        var completedAt = new DateTime(2026, 12, 1, 10, 5, 0, DateTimeKind.Utc);
        var runs = CreateRuns();
        runs.Read(NotSubmittedReminderScanService.Campaign, 2026, Arg.Any<CancellationToken>())
            .Returns(
                CreateRun(ScheduledCampaignRunStatus.Completed) with
                {
                    ScanCount = 2,
                    CompletedAtUtc = completedAt,
                    OrganisationsProcessed = 5,
                    CommandsPublished = 7,
                    OrganisationsSkipped = 1,
                }
            );
        var service = CreateService(new() { Runs = runs });

        var status = await service.GetRunStatus(TestContext.Current.CancellationToken);

        Assert.Equal("completed", status.Status);
        Assert.Equal(2, status.ScanCount);
        Assert.Equal(completedAt, status.CompletedAtUtc);
        Assert.Equal(5, status.OrganisationsProcessed);
        Assert.Equal(7, status.CommandsPublished);
        Assert.Equal(1, status.OrganisationsSkipped);
        Assert.Equal(0, status.RetryableFailures);
    }

    [Fact]
    public async Task RunFullScan_WhenDisabled_ShouldSkipWithoutCallingObligations()
    {
        var obligations = Substitute.For<IWasteObligationsClient>();
        var service = CreateService(new() { Obligations = obligations, Enabled = false });

        var result = await service.RunFullScan(TestContext.Current.CancellationToken);

        Assert.Equal(
            new NotSubmittedReminderScanResult(0, 0, 0, 0, NotSubmittedReminderCutoverGuard.DisabledReason),
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
        var service = CreateService(new() { Obligations = obligations, EmailDeliveryCutoverUtc = null });

        var result = await service.RunFullScan(TestContext.Current.CancellationToken);

        Assert.Equal(
            new NotSubmittedReminderScanResult(0, 0, 0, 0, NotSubmittedReminderCutoverGuard.CutoverMisconfiguredReason),
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
        var service = CreateService(
            new()
            {
                Obligations = obligations,
                Accounts = accounts,
                Publisher = publisher,
            }
        );

        var result = await service.RunFullScan(TestContext.Current.CancellationToken);

        Assert.Equal(new NotSubmittedReminderScanResult(1, 1, 0, 0, null), result);
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
        var service = CreateService(
            new()
            {
                Obligations = obligations,
                Accounts = accounts,
                Publisher = publisher,
            }
        );

        var result = await service.RunFullScan(TestContext.Current.CancellationToken);

        Assert.Equal(new NotSubmittedReminderScanResult(1, 0, 1, 1, null), result);
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

        var service = CreateService(new() { Obligations = obligations, Accounts = accounts });

        var result = await service.RunFullScan(TestContext.Current.CancellationToken);

        Assert.Equal(new NotSubmittedReminderScanResult(1, 0, 1, 0, null), result);
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
        var service = CreateService(
            new()
            {
                Obligations = obligations,
                Accounts = accounts,
                Publisher = publisher,
            }
        );

        var result = await service.RunFullScan(TestContext.Current.CancellationToken);

        Assert.Equal(new NotSubmittedReminderScanResult(1, 1, 0, 0, null), result);
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

        var service = CreateService(
            new()
            {
                Obligations = obligations,
                Accounts = accounts,
                Publisher = publisher,
            }
        );

        var result = await service.RunFullScan(TestContext.Current.CancellationToken);

        Assert.Equal(new NotSubmittedReminderScanResult(2, 1, 1, 1, null), result);
    }

    [Fact]
    public async Task RunFullScan_WhenResultsSpanPages_ShouldProcessEveryOrganisation()
    {
        var first = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var second = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var obligations = Substitute.For<IWasteObligationsClient>();
        obligations
            .SearchUnsubmittedOrganisations(2026, "DirectProducer", Arg.Any<int>(), 1, Arg.Any<CancellationToken>())
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
        var service = CreateService(
            new()
            {
                Obligations = obligations,
                Accounts = accounts,
                Publisher = publisher,
                PageSize = 1,
            }
        );

        var result = await service.RunFullScan(TestContext.Current.CancellationToken);

        Assert.Equal(new NotSubmittedReminderScanResult(2, 2, 0, 0, null), result);
        await publisher.Received(2).Publish(Arg.Any<NotificationCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunFullScan_WhenResultsSpanPages_ShouldReadEveryPageBeforePublishing()
    {
        var first = Guid.Parse("88888888-8888-8888-8888-888888888888");
        var second = Guid.Parse("99999999-9999-9999-9999-999999999999");
        var calls = new List<string>();
        var obligations = Substitute.For<IWasteObligationsClient>();
        obligations
            .SearchUnsubmittedOrganisations(2026, "DirectProducer", Arg.Any<int>(), 1, Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var page = callInfo.ArgAt<int>(2);
                calls.Add($"page-{page}");
                return CreatePage([CreateRow(page == 1 ? first : second)], total: 2, page: page, pageSize: 1);
            });

        var accounts = Substitute.For<IAccountBackendClient>();
        accounts
            .ReadOrganisationWithPersons(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => CreateOrganisation($"{callInfo.Arg<Guid>():N}@example.test"));

        var publisher = Substitute.For<INotificationCommandPublisher>();
        publisher
            .When(substitute => substitute.Publish(Arg.Any<NotificationCommand>(), Arg.Any<CancellationToken>()))
            .Do(_ => calls.Add("publish"));
        var service = CreateService(
            new()
            {
                Obligations = obligations,
                Accounts = accounts,
                Publisher = publisher,
                PageSize = 1,
            }
        );

        await service.RunFullScan(TestContext.Current.CancellationToken);

        Assert.Equal(["page-1", "page-2", "publish", "publish"], calls);
    }

    [Fact]
    public async Task RunFullScan_WhenOrganisationRepeatsAcrossPages_ShouldPublishOnceAndLeaveRunOpen()
    {
        var organisationId = Guid.Parse("abababab-abab-abab-abab-abababababab");
        var obligations = Substitute.For<IWasteObligationsClient>();
        obligations
            .SearchUnsubmittedOrganisations(2026, "DirectProducer", Arg.Any<int>(), 1, Arg.Any<CancellationToken>())
            .Returns(
                CreatePage([CreateRow(organisationId)], total: 2, page: 1, pageSize: 1),
                CreatePage([CreateRow(organisationId)], total: 2, page: 2, pageSize: 1)
            );

        var accounts = Substitute.For<IAccountBackendClient>();
        accounts
            .ReadOrganisationWithPersons(organisationId, Arg.Any<CancellationToken>())
            .Returns(CreateOrganisation("repeated@example.test"));

        var publisher = Substitute.For<INotificationCommandPublisher>();
        var service = CreateService(
            new()
            {
                Obligations = obligations,
                Accounts = accounts,
                Publisher = publisher,
                PageSize = 1,
            }
        );

        var result = await service.RunFullScan(TestContext.Current.CancellationToken);

        Assert.Equal(new NotSubmittedReminderScanResult(1, 1, 0, 1, null), result);
        await accounts.Received(1).ReadOrganisationWithPersons(organisationId, Arg.Any<CancellationToken>());
        await publisher.Received(1).Publish(Arg.Any<NotificationCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunFullScan_WhenLaterPageEmptyBeforeTotalReached_ShouldCountListChangeFailure()
    {
        var organisationId = Guid.Parse("12121212-1212-1212-1212-121212121212");
        var obligations = Substitute.For<IWasteObligationsClient>();
        obligations
            .SearchUnsubmittedOrganisations(2026, "DirectProducer", Arg.Any<int>(), 1, Arg.Any<CancellationToken>())
            .Returns(
                CreatePage([CreateRow(organisationId)], total: 3, page: 1, pageSize: 1),
                CreatePage([], total: 3, page: 2, pageSize: 1)
            );

        var accounts = Substitute.For<IAccountBackendClient>();
        accounts
            .ReadOrganisationWithPersons(organisationId, Arg.Any<CancellationToken>())
            .Returns(CreateOrganisation("partial-page@example.test"));

        var service = CreateService(
            new()
            {
                Obligations = obligations,
                Accounts = accounts,
                PageSize = 1,
            }
        );

        var result = await service.RunFullScan(TestContext.Current.CancellationToken);

        Assert.Equal(new NotSubmittedReminderScanResult(1, 1, 0, 1, null), result);
    }

    [Fact]
    public async Task RunFullScan_WhenTotalChangesBetweenPages_ShouldPublishReadOrganisationsAndLeaveRunOpen()
    {
        var first = Guid.Parse("cdcdcdcd-cdcd-cdcd-cdcd-cdcdcdcdcdcd");
        var second = Guid.Parse("efefefef-efef-efef-efef-efefefefefef");
        var obligations = Substitute.For<IWasteObligationsClient>();
        obligations
            .SearchUnsubmittedOrganisations(2026, "DirectProducer", Arg.Any<int>(), 1, Arg.Any<CancellationToken>())
            .Returns(
                CreatePage([CreateRow(first)], total: 3, page: 1, pageSize: 1),
                CreatePage([CreateRow(second)], total: 2, page: 2, pageSize: 1)
            );

        var accounts = Substitute.For<IAccountBackendClient>();
        accounts
            .ReadOrganisationWithPersons(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => CreateOrganisation($"{callInfo.Arg<Guid>():N}@example.test"));

        var service = CreateService(
            new()
            {
                Obligations = obligations,
                Accounts = accounts,
                PageSize = 1,
            }
        );

        var result = await service.RunFullScan(TestContext.Current.CancellationToken);

        Assert.Equal(new NotSubmittedReminderScanResult(2, 2, 0, 1, null), result);
    }

    [Fact]
    public async Task RunFullScan_WhenObligationYearOverrideAbsent_ShouldDeriveYearFromUkCalendar()
    {
        var obligations = Substitute.For<IWasteObligationsClient>();
        obligations
            .SearchUnsubmittedOrganisations(2026, "DirectProducer", 1, 100, Arg.Any<CancellationToken>())
            .Returns(CreatePage([]));

        var service = CreateService(new() { Obligations = obligations, ObligationYearOverride = null });

        await service.RunFullScan(TestContext.Current.CancellationToken);

        await obligations
            .Received(1)
            .SearchUnsubmittedOrganisations(2026, "DirectProducer", 1, 100, Arg.Any<CancellationToken>());
    }

    private sealed record ScanServiceTestSettings
    {
        public IWasteObligationsClient Obligations { get; init; } = Substitute.For<IWasteObligationsClient>();

        public IAccountBackendClient Accounts { get; init; } = Substitute.For<IAccountBackendClient>();

        public INotificationCommandPublisher Publisher { get; init; } = Substitute.For<INotificationCommandPublisher>();

        public IScheduledCampaignLeaseService? Leases { get; init; }

        public IScheduledCampaignRunStore? Runs { get; init; }

        public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(45);

        public TimeSpan LeaseRenewalInterval { get; init; } = TimeSpan.FromMinutes(15);

        public string Now { get; init; } = "2026-12-01T10:00:00Z";

        public bool Enabled { get; init; } = true;

        public string? EmailDeliveryCutoverUtc { get; init; } = Cutover;

        public int? ObligationYearOverride { get; init; } = 2026;

        public int PageSize { get; init; } = 100;
    }

    private static NotSubmittedReminderScanService CreateService(ScanServiceTestSettings settings)
    {
        var reminder = Options.Create(
            new NotSubmittedReminderOptions
            {
                Enabled = settings.Enabled,
                Month = 12,
                Day = 1,
                Hour = 10,
                SendWindow = TimeSpan.FromDays(7),
                ScanLease = settings.LeaseDuration,
                ScanLeaseRenewalInterval = settings.LeaseRenewalInterval,
                PageSize = settings.PageSize,
                ObligationYearOverride = settings.ObligationYearOverride,
            }
        );
        var delivery = Options.Create(
            new NotificationCommandDeliveryOptions
            {
                QueueUrl = "https://example.test/000000000000/queue.fifo",
                EmailDeliveryCutoverUtc = settings.EmailDeliveryCutoverUtc,
                EvidenceDigestSecret = "secret",
                RecipientLaneSecret = "secret",
            }
        );
        var time = new FakeTimeProvider(
            DateTimeOffset.Parse(settings.Now, System.Globalization.CultureInfo.InvariantCulture)
        );
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
            new ScheduledCampaignLeaseKeeper(
                settings.Leases ?? CreateLeases(),
                time,
                logs.CreateLogger<ScheduledCampaignLeaseKeeper>()
            ),
            settings.Runs ?? CreateRuns(),
            settings.Obligations,
            settings.Accounts,
            settings.Publisher,
            logs.CreateLogger<NotSubmittedReminderScanService>()
        );
    }

    private static IScheduledCampaignLeaseService CreateLeases(bool acquired = true)
    {
        var leases = Substitute.For<IScheduledCampaignLeaseService>();
        leases.TryAcquire(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(acquired);
        leases.TryRenew(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(true);

        return leases;
    }

    private static IScheduledCampaignRunStore CreateRuns(
        ScheduledCampaignRunStart start = ScheduledCampaignRunStart.Started
    )
    {
        var runs = Substitute.For<IScheduledCampaignRunStore>();
        runs.TryStart(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(start);
        runs.Read(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns((ScheduledCampaignRun?)null);

        return runs;
    }

    private static async Task<T> WaitForCancellation<T>(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);

        return default!;
    }

    private static ScheduledCampaignRun CreateRun(ScheduledCampaignRunStatus status) =>
        new()
        {
            Id = $"{NotSubmittedReminderScanService.Campaign}:2026",
            Campaign = NotSubmittedReminderScanService.Campaign,
            ObligationYear = 2026,
            Status = status,
        };

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
