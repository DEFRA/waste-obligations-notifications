using Defra.WasteObligations.Consumer.Scheduled;
using Defra.WasteObligations.Consumer.Services.AccountBackend;
using Xunit;

namespace Defra.WasteObligations.Consumer.Tests.Scheduled;

public sealed class NotSubmittedReminderRecipientResolverTests
{
    [Fact]
    public void Resolve_WhenSameEmail_ShouldDeduplicate()
    {
        var persons = new OrganisationPerson[]
        {
            new(Guid.NewGuid(), "Ann", "Approved", "same@example.test", "Approved Person"),
            new(Guid.NewGuid(), "Del", "Delegated", "same@example.test", "Delegated Person"),
        };

        var recipients = NotSubmittedReminderRecipientResolver.Resolve(persons);

        Assert.Single(recipients);
        Assert.Equal("same@example.test", recipients[0].Email);
    }

    [Fact]
    public void Resolve_WhenDistinctRoles_ShouldReturnTwoRecipients()
    {
        var persons = new OrganisationPerson[]
        {
            new(Guid.NewGuid(), "Ann", "Approved", "approved@example.test", "Approved Person"),
            new(Guid.NewGuid(), "Del", "Delegated", "delegated@example.test", "Delegated Person"),
        };

        var recipients = NotSubmittedReminderRecipientResolver.Resolve(persons);

        Assert.Equal(2, recipients.Length);
    }
}
