namespace LinTv.Core.Exceptions
{
    /// The multiplex's PAT doesn't list the program: the station renumbered or moved (rescan),
    /// or the signal is too weak to deliver the PAT.
    public sealed class ProgramNotFoundException(ushort programNumber, string message) : Exception(message)
    {
        public ushort ProgramNumber { get; } = programNumber;
    }
}
