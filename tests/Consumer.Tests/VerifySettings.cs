using System.Runtime.CompilerServices;

namespace Defra.WasteObligations.Consumer.Tests;

public static class VerifySettings
{
    [ModuleInitializer]
    public static void Initialize()
    {
        VerifierSettings.UseStrictJson();
        VerifierSettings.DontIgnoreEmptyCollections();
    }
}
