using System.Text.Json;
using Defra.WasteObligations.Consumer.Services.WasteObligations;

namespace Defra.WasteObligations.Consumer.Cancellation;

public static class CancellationPresentation
{
    public static JsonElement Create(Declaration declaration, bool welsh, CancellationRecipient recipient)
    {
        var organisation = declaration.Organisation!;
        var scheme = organisation.RegistrationType == "ComplianceScheme";
        var regulator = organisation.Regulator?.Trim() switch
        {
            "EA" or "The Environment Agency" => "The Environment Agency",
            "SEPA" or "The Scottish Environment Protection Agency" => "The Scottish Environment Protection Agency",
            "NIEA" or "The Northern Ireland Environment Agency" => "The Northern Ireland Environment Agency",
            "NRW" or "Natural Resources Wales" => "Natural Resources Wales",
            var value => value,
        };
        var values = new Dictionary<string, object?>
        {
            ["year"] = checked(declaration.ObligationYear + 1),
            ["regulator"] = regulator,
            ["regulatorEmail"] = organisation.RegulatorEmail,
            ["firstName"] = recipient.FirstName,
            ["lastName"] = recipient.LastName,
            ["certOrStatement"] = scheme ? "statement" : "certificate",
            ["certOrStatement_cy"] = scheme ? "datganiad" : "tystysgrif",
            ["certOrStatementBullet"] = scheme
                ? "meet your recycling obligations"
                : "acquire enough packaging waste recycling notes (PRNs) or packaging waste export recycling notes (PERNs) to meet your recycling obligations",
            ["certOrStatementBullet_cy"] = scheme
                ? "cyflawni eich rhwymedigaethau ailgylchu"
                : "caffael digon o nodiadau ailgylchu gwastraff pecynwaith (PRNs) neu nodiadau ailgylchu allforio gwastraff pecynwaith (PERNs) i gyflawni eich rhwymedigaethau ailgylchu bob blwyddyn",
            ["certOrStatementBullet2"] = scheme
                ? "meet your revised recycling obligations"
                : "acquire enough packaging waste recycling notes (PRNs) or packaging waste export recycling notes (PERNs) to meet your revised recycling obligations",
            ["certOrStatementBullet2_cy"] = scheme
                ? "cyflawni eich rhwymedigaethau ailgylchu diwygiedig"
                : "caffael digon o nodiadau ailgylchu gwastraff pecynwaith (PRNs) neu nodiadau ailgylchu allforio gwastraff pecynwaith (PERNs) i fodloni eich rhwymedigaethau ailgylchu diwygiedig",
        };
        if (welsh && regulator == "Natural Resources Wales")
            values.Add("regulator_cy", "Cyfoeth Naturiol Cymru");

        return JsonSerializer.SerializeToElement(values);
    }
}
