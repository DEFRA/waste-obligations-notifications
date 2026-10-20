using System.Diagnostics;

namespace Defra.WasteObligations.Consumer.Administration;

internal static class CommandDlqDeadline
{
    public static TimeSpan RemainingDelay(long started, TimeSpan budget)
    {
        var delay = budget - Stopwatch.GetElapsedTime(started);

        return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
    }
}
