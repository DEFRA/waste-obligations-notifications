namespace Defra.WasteObligations.Consumer.Administration;

public static class CommandDlqRecoveryGuidance
{
    private const string InspectEndpoint = "/admin/notification-commands/dlq/inspect";
    private const string RedriveEndpoint = "/admin/notification-commands/dlq/redrive";
    private const string DiscardEndpoint = "/admin/notification-commands/dlq/discard";
    private const string CurrentEvidence =
        "This is current evidence, not a historical failure cause or live Notify delivery status. ";
    private const string Unconfirmed =
        "The result is unconfirmed: pending evidence cannot distinguish Notify rejection, a lost response, "
        + "a crash before sending or failed acceptance persistence. Check Notify using the original reference. "
        + "If Notify confirms delivery, discard after the lease expires to prevent further attempts; "
        + "if still processing, wait and inspect again. If rejection is confirmed, fix the cause, including "
        + "credentials where applicable, before redrive. An inconclusive lookup leaves a duplicate-email risk; "
        + "absence in Notify is not proof that no request was accepted. ";

    public static string For(string classification) =>
        CurrentEvidence
        + (
            classification switch
            {
                "delivery-accepted" =>
                    "Notify acceptance is recorded; this does not confirm recipient delivery. Check Notify by "
                        + "notification ID/reference and check any replacement's progress. If still processing, wait. "
                        + "If delivered, redrive only to clean up the terminal queue copy without another send. "
                        + "If Notify later reports delivery failed, investigate; the record remains accepted and "
                        + "redrive still performs terminal-copy cleanup only. Explicit resend is unavailable. "
                        + "An empty replacement lookup does not prove queue absence or failed publication.",
                "active-claim" => Unconfirmed
                    + "An unexpired lease owns the attempt, but is not proof that its process is running. "
                    + "Wait and inspect again. Selected redrive is not offered while the lease is active; "
                    + "discard refuses active claims.",
                "expired-claim" => Unconfirmed
                    + "The lease has expired. After investigation, redrive to retry or discard to abandon "
                    + "further attempts. Abandonment does not undo an earlier uncertain request.",
                "unrecorded" =>
                    "No matching delivery record was found. Investigate and fix the processing failure before "
                        + "redrive, or discard if further attempts are no longer required. No record does not prove "
                        + "queue absence or that Notify was never called.",
                "delivery-suppressed" =>
                    "Confirm the handover policy deliberately suppressed Notifications sending; the legacy sender "
                        + "may have sent. Redrive only cleans up the terminal queue copy. Ordinary replay cannot undo "
                        + "suppression, and explicit resend cannot override it.",
                "delivery-abandoned" =>
                    "Confirm the previous operator decision remains appropriate. Discard performs idempotent "
                        + "terminal-copy cleanup. Abandonment does not prove an earlier uncertain request failed. "
                        + "Explicit resend cannot override abandonment.",
                "immutable-conflict" =>
                    "The identity exists with different immutable email details. Escalate to engineering to "
                        + "investigate the producer/evidence mismatch; ordinary replay will not repair it. "
                        + "No selected recovery or removal action is offered for this classification.",
                "invalid-or-unsupported-command" =>
                    "Command inputs cannot be trusted. Escalate to engineering to investigate format, encoding "
                        + "or schema compatibility. Inspect again after repair. No selected recovery or removal "
                        + "action is available.",
                _ => "The stored outcome is unrecognised. Escalate to engineering to investigate compatibility "
                    + "or damaged evidence before permitting another email. No selected recovery or removal "
                    + "action is offered for this classification.",
            }
        );

    public static IReadOnlyList<CommandDlqAction> Actions(string classification) =>
        classification switch
        {
            "delivery-accepted" or "delivery-suppressed" =>
            [
                new("POST", InspectEndpoint, "Inspect again"),
                new("POST", RedriveEndpoint, "Clean up the terminal queue copy without another send"),
            ],
            "active-claim" => [new("POST", InspectEndpoint, "Wait and inspect again")],
            "expired-claim" =>
            [
                new("POST", InspectEndpoint, "Inspect again after investigation"),
                new("POST", RedriveEndpoint, "Retry after investigating the unconfirmed result"),
                new("POST", DiscardEndpoint, "Abandon further attempts and remove the selected copy"),
            ],
            "unrecorded" =>
            [
                new("POST", RedriveEndpoint, "Retry after fixing the processing failure"),
                new("POST", DiscardEndpoint, "Abandon further attempts and remove the selected copy"),
            ],
            "delivery-abandoned" => [new("POST", DiscardEndpoint, "Clean up the abandoned queue copy")],
            _ => [],
        };
}
