namespace Defra.WasteObligations.Consumer.Submission;

public sealed class SubmissionProcessingException(string reason) : Exception("Submission processing failed.")
{
    public string Reason { get; } = reason;
}
