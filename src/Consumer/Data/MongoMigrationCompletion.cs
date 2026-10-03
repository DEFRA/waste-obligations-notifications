namespace Defra.WasteObligations.Consumer.Data;

// Records only critical startup prerequisites; optional migration progress is independent.
public sealed class MongoMigrationCompletion
{
    private volatile bool _completed;

    public bool IsCompleted => _completed;

    public void MarkCompleted() => _completed = true;
}
