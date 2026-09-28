namespace LinTv.Core.Services
{
    /// The one way to read the tuned multiplex. The device allows a single dvr0 reader, so every
    /// consumer (viewers, scanners) subscribes here and shares one read of the current tune.
    /// Hold a TunerLease for as long as you read.
    public interface IProgramStreamBroadcaster
    {
        /// The raw multiplex (all programs, PSIP), shared with every other reader. Chunks may be
        /// empty: idle ticks at least every ~500 ms when no data arrives, so callers can check
        /// their deadlines.
        IAsyncEnumerable<ReadOnlyMemory<byte>> ReadMultiplexAsync(CancellationToken ct);

        /// One program as a standalone single-program TS (see ProgramDemuxer). Each yielded chunk
        /// is only valid until the next iteration.
        /// Throws ProgramNotFoundException, before yielding anything, if the PAT doesn't list the
        /// program in time. Ends quietly if no output arrives for StreamStallTimeoutSeconds
        /// (signal lost).
        IAsyncEnumerable<ReadOnlyMemory<byte>> StreamProgramAsync(ushort programNumber, CancellationToken ct);
    }
}
