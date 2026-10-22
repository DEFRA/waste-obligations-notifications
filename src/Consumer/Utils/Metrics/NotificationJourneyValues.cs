namespace Defra.WasteObligations.Consumer.Utils.Metrics;

public static class NotificationJourneyValues
{
    public static class Consumer
    {
        public const string Analytics = "analytics";
        public const string Delivery = "delivery";
    }

    public static class Source
    {
        public const string Analytics = "analytics";
    }

    public static class Outcome
    {
        public const string Ignored = "ignored";
        public const string NoNotifications = "no-notifications";
        public const string CommandsPublished = "commands-published";
    }

    public static class Stage
    {
        public const string Receive = "receive";
        public const string Parse = "parse";
        public const string ValidateEvent = "validate-event";
        public const string ReadDeclaration = "read-declaration";
        public const string PublishCommand = "publish-command";
        public const string Acknowledge = "acknowledge";
        public const string Suppression = "suppression";
        public const string Claim = "claim";
        public const string Send = "send";
        public const string RecordAcceptance = "record-acceptance";
        public const string ProcessingBudget = "processing-budget";
    }
}
