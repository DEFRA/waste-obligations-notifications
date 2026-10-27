namespace Defra.WasteObligations.Consumer.Notifications;

public static class DeclarationNotificationTemplates
{
    public static class Submitted
    {
        public const string DirectEnglishTemplateId = nameof(DirectEnglishTemplateId);
        public const string DirectWelshTemplateId = nameof(DirectWelshTemplateId);
        public const string SchemeEnglishTemplateId = nameof(SchemeEnglishTemplateId);
        public const string SchemeWelshTemplateId = nameof(SchemeWelshTemplateId);
    }

    public static class Cancelled
    {
        public const string NotSignedByCorrectPersonEn = nameof(NotSignedByCorrectPersonEn);
        public const string NotSignedByCorrectPersonCy = nameof(NotSignedByCorrectPersonCy);
        public const string RecyclingObligationsChangedEn = nameof(RecyclingObligationsChangedEn);
        public const string RecyclingObligationsChangedCy = nameof(RecyclingObligationsChangedCy);
        public const string CanMeetRecyclingObligationsEn = nameof(CanMeetRecyclingObligationsEn);
        public const string CanMeetRecyclingObligationsCy = nameof(CanMeetRecyclingObligationsCy);
        public const string ProducerRequestedEn = nameof(ProducerRequestedEn);
        public const string ProducerRequestedCy = nameof(ProducerRequestedCy);
    }

    public static class NotSubmittedReminder
    {
        public const string EnglishTemplateId = nameof(EnglishTemplateId);
        public const string WelshTemplateId = nameof(WelshTemplateId);
    }
}
