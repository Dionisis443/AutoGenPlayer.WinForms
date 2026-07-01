namespace AutoGen_Player.Core;

public sealed class PlayerDiagnosticEventArgs : EventArgs
{
    public PlayerDiagnosticEventArgs(string operation, TimeSpan elapsed, string? details = null)
    {
        Operation = operation;
        Elapsed = elapsed;
        Details = details ?? string.Empty;
    }

    public string Operation { get; }
    public TimeSpan Elapsed { get; }
    public string Details { get; }

    public override string ToString()
    {
        return string.IsNullOrWhiteSpace(Details)
            ? $"{Operation}: {Elapsed.TotalMilliseconds:0.0} ms"
            : $"{Operation}: {Elapsed.TotalMilliseconds:0.0} ms | {Details}";
    }
}
