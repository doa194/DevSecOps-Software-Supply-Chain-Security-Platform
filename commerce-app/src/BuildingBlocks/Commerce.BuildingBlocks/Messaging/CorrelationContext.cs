// Carries the correlation id of the current request or message through async code, so
// outbox rows, audit records, logs and published messages can be tied back together.
using System.Diagnostics;

namespace Commerce.BuildingBlocks.Messaging;

public interface ICorrelationContext
{
    string? CorrelationId { get; }
}

public sealed class CorrelationContext : ICorrelationContext
{
    private static readonly AsyncLocal<string?> Current = new();

    // Falls back to the trace id so every operation has a correlation id even when the
    // caller did not send one.
    public string? CorrelationId => Current.Value ?? Activity.Current?.TraceId.ToString();

    public static IDisposable Begin(string? correlationId)
    {
        var previous = Current.Value;
        Current.Value = correlationId;
        return new Restore(previous);
    }

    private sealed class Restore(string? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
