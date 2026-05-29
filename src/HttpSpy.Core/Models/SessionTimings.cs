namespace HttpSpy.Core.Models;

/// <summary>
/// Per-phase timing breakdown of a transaction, modelled after the HAR timings
/// object. All values are in milliseconds; -1 means "not applicable".
/// </summary>
public sealed class SessionTimings
{
    public double BlockedMs { get; set; } = -1;
    public double DnsMs { get; set; } = -1;
    public double ConnectMs { get; set; } = -1;
    public double TlsMs { get; set; } = -1;
    public double SendMs { get; set; } = -1;
    public double WaitMs { get; set; } = -1;
    public double ReceiveMs { get; set; } = -1;

    /// <summary>Wall-clock total from first byte sent to last byte received.</summary>
    public double TotalMs { get; set; } = -1;

    public double SumOfPhases()
    {
        double sum = 0;
        foreach (var v in new[] { BlockedMs, DnsMs, ConnectMs, TlsMs, SendMs, WaitMs, ReceiveMs })
            if (v > 0) sum += v;
        return sum;
    }
}
