namespace LinTv.Core.Domain
{
    public sealed record SignalRange(double Min, double Average, double Max);

    /// Result of sampling one channel's signal for a few seconds (see SignalMeter).
    /// SecondsToLock is null if it never locked, or if the tuner was already on this frequency
    /// for someone else (Shared), in which case no retune happened.
    public sealed record SignalReport(
        string Channel, int Index, int RfChannel, long FrequencyHz,
        bool Shared, double? SecondsToLock,
        int Samples, double LockedPercent,
        SignalStatus Last,
        SignalRange StrengthPercent, SignalRange SnrDb,
        double ScanStrengthPercent, double ScanSnrDb);
}
