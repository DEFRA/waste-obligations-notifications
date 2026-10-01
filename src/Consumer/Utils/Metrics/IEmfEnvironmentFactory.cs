using Amazon.CloudWatch.EMF.Environment;

namespace Defra.WasteObligations.Consumer.Utils.Metrics;

public interface IEmfEnvironmentFactory
{
    IEnvironment Create(CancellationToken cancellationToken);
}
