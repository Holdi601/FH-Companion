namespace ForzaHaptics.Tester.Rivals;

/// <summary>
/// The one owner of the telemetry stream, as the overlay needs to see it.
/// </summary>
/// <remarks>
/// There is exactly one socket in this application and exactly one place to set its
/// port -- the box on the telemetry tab. The overlay does not keep a port of its
/// own, because two settings for one socket can disagree, and the one that loses is
/// always the one the user just changed.
///
/// Forza sends its stream to a single endpoint and a UDP port takes a single
/// listener (measured: WinError 10013 one way, 10048 the other), so this is not a
/// tidiness point -- it is the reason the haptics and the overlay can run at once.
/// </remarks>
internal interface ITelemetryHost
{
    /// <summary>The port the listener is on, or would use when started.</summary>
    int TelemetryPort { get; }

    /// <summary>Whether packets are arriving right now.</summary>
    bool TelemetryRunning { get; }

    /// <summary>Start the listener if it is not already running.</summary>
    void EnsureTelemetryStarted();
}
