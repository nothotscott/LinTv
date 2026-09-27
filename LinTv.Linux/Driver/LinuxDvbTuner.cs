using LinTv.Core.Configuration;
using LinTv.Core.Domain;
using LinTv.Core.Driver;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace LinTv.Linux.Driver
{
    public class LinuxDvbTuner : IDvbTuner, IDisposable
    {
        private const int O_RDWR = 2, O_RDONLY = 0;
        private const int EINTR = 4, EACCES = 13;
        private const ushort AllPids = 0x2000;

        private const int EOVERFLOW = 75, EAGAIN = 11;
        // Linux x86-64/arm64 values (asm-generic/fcntl.h, poll.h).
        private const int O_NONBLOCK = 0x800;
        private const short POLLIN = 0x1;
        /// Longest a read waits before handing control back to the consumer.
        private const int PollSliceMs = 500;
        /// A batch closes at ~48 KB (about 20 ms of a full multiplex) or 50 ms after its first byte,
        /// whichever comes first: ~40-50 chunks/s, with little added latency.
        private const int MinBatchBytes = 48 * 1024, MaxBatchWaitMs = 50;

        // _IOW('o', 82, struct dtv_properties); sizeof(dtv_properties) == 16 on 64-bit
        private const nuint FE_SET_PROPERTY = 0x40106f52;
        // _IOR('o', 69, fe_status_t), _IOR('o', 71, __u16), _IOR('o', 72, __u16)
        private const nuint FE_READ_STATUS = 0x80046f45, FE_READ_SIGNAL_STRENGTH = 0x80026f47, FE_READ_SNR = 0x80026f48;
        private const uint FE_HAS_LOCK = 0x10;

        // _IOW('o', 44, struct dmx_pes_filter_params); linux/dvb/dmx.h
        private const nuint DMX_SET_PES_FILTER = 0x40146f2c;
        private const uint DMX_IN_FRONTEND = 0, DMX_OUT_TS_TAP = 2, DMX_PES_OTHER = 20, DMX_IMMEDIATE_START = 4;

        // linux/dvb/frontend.h: DTV_* commands, fe_delivery_system, fe_modulation
        private const uint DTV_TUNE = 1, DTV_CLEAR = 2, DTV_FREQUENCY = 3, DTV_MODULATION = 4,
            DTV_INVERSION = 6, DTV_DELIVERY_SYSTEM = 17;
        private const uint SYS_ATSC = 11, VSB_8 = 7, INVERSION_AUTO = 2;

        /// struct dtv_property (packed). Only the u32 member of the union is used;
        /// Size covers the largest union member (the 56-byte buffer struct on 64-bit).
        [StructLayout(LayoutKind.Explicit, Size = 76)]
        private struct DtvProperty
        {
            [FieldOffset(0)] public uint Cmd;
            [FieldOffset(16)] public uint Data;
            [FieldOffset(72)] public int Result;
        }

        [StructLayout(LayoutKind.Sequential)]
        private unsafe struct DtvProperties
        {
            public uint Num;
            public DtvProperty* Props;
        }

        /// struct dmx_pes_filter_params: u16 pid, then four u32s aligned at offset 4 (20 bytes).
        [StructLayout(LayoutKind.Sequential)]
        private struct DmxPesFilterParams
        {
            public ushort Pid;
            public uint Input;
            public uint Output;
            public uint PesType;
            public uint Flags;
        }

        private readonly Lock _frontendLock = new();
        private int _frontendFd = -1;

        /// A healthy locked multiplex delivers ~2.4 MB/s, so a gap this long means the tuner or
        /// signal has stalled. The dvr0 read blocks rather than failing, so it'd otherwise be silent.
        private static readonly TimeSpan StallWarningInterval = TimeSpan.FromSeconds(5);

        public int Adapter { get; init; }

        public ILogger Logger { private get; set; }

        public LinuxDvbTuner(int adapter, ILogger<LinuxDvbTuner> logger)
        {
            Adapter = adapter;
            Logger = logger;
        }

        [DllImport("libc", SetLastError = true)] private static extern int open(string path, int flags);
        [DllImport("libc", SetLastError = true)] private static extern int ioctl(int fd, nuint request, IntPtr arg);
        [DllImport("libc", SetLastError = true)] private static extern int close(int fd);
        [DllImport("libc", SetLastError = true)] private static extern unsafe int poll(PollFd* fds, nuint nfds, int timeout);
        [DllImport("libc", SetLastError = true)] private static extern unsafe nint read(int fd, byte* buf, nuint count);

        private string Dev(string node) => $"/dev/dvb/adapter{Adapter}/{node}";

        public unsafe Task TuneAsync(long frequencyHz, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequencyHz);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(frequencyHz, uint.MaxValue);

            // DTV_CLEAR resets the frontend's cached params; DTV_TUNE commits the batch.
            // The ioctl returns once the tune is queued -- lock is reported via FE_READ_STATUS.
            var props = stackalloc DtvProperty[6];
            props[0] = new DtvProperty { Cmd = DTV_CLEAR };
            props[1] = new DtvProperty { Cmd = DTV_DELIVERY_SYSTEM, Data = SYS_ATSC };
            props[2] = new DtvProperty { Cmd = DTV_MODULATION, Data = VSB_8 };
            props[3] = new DtvProperty { Cmd = DTV_FREQUENCY, Data = (uint)frequencyHz };
            props[4] = new DtvProperty { Cmd = DTV_INVERSION, Data = INVERSION_AUTO };
            props[5] = new DtvProperty { Cmd = DTV_TUNE };
            var request = new DtvProperties { Num = 6, Props = props };

            Logger.LogDebug("Tuning {FrequencyHz} Hz (ATSC 8VSB)", frequencyHz);
            lock (_frontendLock)
            {
                int rc;
                do rc = ioctl(OpenFrontend(), FE_SET_PROPERTY, (IntPtr)(&request));
                while (rc < 0 && Marshal.GetLastPInvokeError() == EINTR);

                if (rc < 0)
                    throw new IOException(
                        $"FE_SET_PROPERTY failed tuning {frequencyHz} Hz on {Dev("frontend0")}: {Marshal.GetLastPInvokeErrorMessage()}");
            }

            return Task.CompletedTask;
        }

        /// The frontend fd stays open for the tuner's lifetime: closing it lets the
        /// driver power the tuner down, dropping the tune.
        private int OpenFrontend()
        {
            if (_frontendFd >= 0) return _frontendFd;

            if (!Environment.Is64BitProcess)
                throw new PlatformNotSupportedException("dtv_property marshalling assumes a 64-bit process");

            var path = Dev("frontend0");
            int fd = open(path, O_RDWR);
            if (fd < 0)
            {
                var hint = Marshal.GetLastPInvokeError() == EACCES
                    ? " (is this user in the 'video' group? Log in again after adding it.)"
                    : "";
                throw new IOException($"Unable to open {path}: {Marshal.GetLastPInvokeErrorMessage()}{hint}");
            }

            Logger.LogDebug("Opened {Path} (fd {Fd})", path, fd);
            return _frontendFd = fd;
        }

        /// Returns as soon as the frontend locks. On timeout, returns the last reading (unlocked),
        /// whose strength/SNR show whether there was any signal at all.
        public async Task<SignalStatus> WaitForLockAsync(TimeSpan timeout, CancellationToken ct)
        {
            var elapsed = Stopwatch.StartNew();
            var status = new SignalStatus(false, 0, 0);
            while (elapsed.Elapsed < timeout)
            {
                status = ReadSignalStatus();
                if (status.Locked)
                {
                    Logger.LogDebug("Lock after {ElapsedMs} ms (strength {Strength:F0}%, SNR {Snr:F1} dB)",
                        elapsed.ElapsedMilliseconds, status.StrengthPercent, status.SnrDb);
                    return status;
                }
                await Task.Delay(100, ct);
            }

            Logger.LogDebug("No lock after {ElapsedMs} ms (strength {Strength:F0}%, SNR {Snr:F1} dB)",
                elapsed.ElapsedMilliseconds, status.StrengthPercent, status.SnrDb);
            return status;
        }

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadTransportStreamAsync(
            [EnumeratorCancellation] CancellationToken ct)
        {
            // The filter lives as long as the demux fd, so hold it open for the whole stream.
            // With it set, dvr0 is a plain byte stream of TS packets.
            int demuxFd = OpenPassThroughFilter(AllPids);
            var progress = new ReadProgress();
            var elapsed = Stopwatch.StartNew();
            Logger.LogDebug("TS read started (demux fd {Fd})", demuxFd);

            // Non-blocking + poll(): a blocking read() on dvr0 never returns when the signal dies,
            // so cancellation (client gone, lease revoked) would never be seen and the fds, and
            // with them the tuner, would be held forever.
            int dvrFd = open(Dev("dvr0"), O_RDONLY | O_NONBLOCK);
            if (dvrFd < 0)
            {
                var error = Marshal.GetLastPInvokeErrorMessage();
                close(demuxFd);
                throw new IOException($"Unable to open {Dev("dvr0")}: {error} (only one reader at a time; a stuck stream can be ended with POST /stream/disconnect)");
            }

            // Fires on a timer thread, so it reports even if the consumer stops pulling.
            using var watchdog = new Timer(_ => WarnIfStalled(progress), null, StallWarningInterval, StallWarningInterval);
            try
            {
                var buffer = new byte[188 * 348]; // ~64 KB, packet-aligned
                while (!ct.IsCancellationRequested)
                {
                    // poll()/read() block their thread briefly, so keep them off the caller's. One
                    // hop per batch, not per read: on a slow single core, per-read overhead is
                    // what made readers fall behind.
                    var batch = await Task.Run(() => FillBatch(dvrFd, buffer), CancellationToken.None);

                    if (batch.Overflows > 0)
                    {
                        // The kernel ring buffer overran because we read too slowly. Packets
                        // were dropped, but the stream carries on.
                        progress.Overflows += batch.Overflows;
                        Logger.LogTrace("dvr0 overflow: {Total} so far, packets dropped", progress.Overflows);
                    }
                    if (batch.Errno != 0)
                        throw new IOException($"read from {Dev("dvr0")} failed: {Marshal.GetPInvokeErrorMessage(batch.Errno)}");

                    if (batch.Filled > 0)
                    {
                        progress.Bytes += batch.Filled;
                        progress.Chunks++;
                        Volatile.Write(ref progress.LastDataTicks, Environment.TickCount64);
                        yield return buffer.AsMemory(0, batch.Filled).ToArray(); // copy: consumers outlive the buffer
                    }
                    else if (!batch.EndOfStream)
                    {
                        // Idle tick: gives the consumer a chance to check its own deadlines.
                        yield return ReadOnlyMemory<byte>.Empty;
                    }

                    if (batch.EndOfStream)
                    {
                        Logger.LogWarning("dvr0 returned end-of-stream");
                        yield break;
                    }
                }
            }
            finally
            {
                close(dvrFd);
                close(demuxFd);
                Logger.LogDebug("TS read stopped after {Seconds:F1}s: {MegaBytes:F1} MB in {Chunks} chunks (avg {AvgKb:F0} KB), {Overflows} overflows",
                    elapsed.Elapsed.TotalSeconds, progress.Bytes / 1_000_000.0, progress.Chunks,
                    progress.Chunks > 0 ? progress.Bytes / 1024.0 / progress.Chunks : 0, progress.Overflows);
            }
        }

        private readonly record struct Batch(int Filled, int Overflows, int Errno, bool EndOfStream);

        /// Fills buffer with one batch. It waits up to PollSliceMs for the first data, then keeps
        /// reading until MinBatchBytes have arrived, MaxBatchWaitMs have passed since the first
        /// byte, or the buffer is full. poll() wakes on any data, so without batching each chunk
        /// would be a few KB and the per-chunk costs (wake-ups, fan-out, demux, HTTP writes) are
        /// paid hundreds of times a second. Filled = 0 without EndOfStream means an idle slice.
        private static Batch FillBatch(int fd, byte[] buffer)
        {
            int filled = 0, overflows = 0;
            long deadline = 0;

            while (filled < buffer.Length)
            {
                int timeout;
                if (filled == 0)
                {
                    timeout = PollSliceMs;
                }
                else
                {
                    if (filled >= MinBatchBytes) break;
                    timeout = (int)Math.Max(0, deadline - Environment.TickCount64);
                    if (timeout == 0) break;
                }

                if (!WaitReadable(fd, timeout)) break;

                var (read, errno) = ReadInto(fd, buffer, filled);
                if (read < 0)
                {
                    if (errno is EAGAIN or EINTR) continue;
                    if (errno == EOVERFLOW) { overflows++; continue; }
                    return new Batch(filled, overflows, errno, EndOfStream: false);
                }
                if (read == 0) return new Batch(filled, overflows, 0, EndOfStream: true);

                if (filled == 0) deadline = Environment.TickCount64 + MaxBatchWaitMs;
                filled += (int)read;
            }

            return new Batch(filled, overflows, 0, EndOfStream: false);
        }

        /// True if fd has data within timeoutMs; false on timeout or signal interruption.
        private static unsafe bool WaitReadable(int fd, int timeoutMs)
        {
            var pfd = new PollFd { Fd = fd, Events = POLLIN };
            int rc = poll(&pfd, 1, timeoutMs);
            if (rc < 0)
            {
                int errno = Marshal.GetLastPInvokeError();
                if (errno == EINTR) return false;
                throw new IOException($"poll on dvr0 failed: {Marshal.GetPInvokeErrorMessage(errno)}");
            }
            // POLLERR (e.g. overflow) also counts: the following read reports it as EOVERFLOW.
            return rc > 0;
        }

        /// Returns errno alongside the result, captured before anything else can overwrite it.
        private static unsafe (nint Read, int Errno) ReadInto(int fd, byte[] buffer, int offset)
        {
            fixed (byte* p = buffer)
            {
                nint n = read(fd, p + offset, (nuint)(buffer.Length - offset));
                return (n, n < 0 ? Marshal.GetLastPInvokeError() : 0);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PollFd
        {
            public int Fd;
            public short Events;
            public short Revents;
        }

        private void WarnIfStalled(ReadProgress progress)
        {
            var idle = TimeSpan.FromMilliseconds(Environment.TickCount64 - Volatile.Read(ref progress.LastDataTicks));
            if (idle < StallWarningInterval) return;

            var signal = ReadSignalStatus();
            Logger.LogWarning(
                "No data from dvr0 for {Seconds:F0}s -- tuner stalled or signal lost? (locked {Locked}, strength {Strength:F0}%, SNR {Snr:F1} dB)",
                idle.TotalSeconds, signal.Locked, signal.StrengthPercent, signal.SnrDb);
        }

        private sealed class ReadProgress
        {
            public long LastDataTicks = Environment.TickCount64;
            public long Bytes;
            public long Chunks;
            public int Overflows;
        }

        public unsafe SignalStatus ReadSignalStatus()
        {
            lock (_frontendLock)
            {
                if (_frontendFd < 0) return new SignalStatus(false, 0, 0);

                uint status = 0;
                if (ioctl(_frontendFd, FE_READ_STATUS, (IntPtr)(&status)) < 0)
                    throw new IOException($"FE_READ_STATUS failed: {Marshal.GetLastPInvokeErrorMessage()}");

                // Legacy DVBv3 stats: units are driver-specific. The HVR-1800's s5h1409 demod
                // reports strength as 0..65535 and SNR in tenths of a dB. They're best-effort,
                // so a failed read just reports 0.
                ushort strength = 0, snr = 0;
                if (ioctl(_frontendFd, FE_READ_SIGNAL_STRENGTH, (IntPtr)(&strength)) < 0) strength = 0;
                if (ioctl(_frontendFd, FE_READ_SNR, (IntPtr)(&snr)) < 0) snr = 0;

                return new SignalStatus((status & FE_HAS_LOCK) != 0, strength * 100.0 / ushort.MaxValue, snr / 10.0);
            }
        }

        private unsafe int OpenPassThroughFilter(ushort pid)
        {
            var path = Dev("demux0");
            int fd = open(path, O_RDWR);
            if (fd < 0)
                throw new IOException($"Unable to open {path}: {Marshal.GetLastPInvokeErrorMessage()}");

            var filter = new DmxPesFilterParams
            {
                Pid = pid,
                Input = DMX_IN_FRONTEND,
                Output = DMX_OUT_TS_TAP,
                PesType = DMX_PES_OTHER,
                Flags = DMX_IMMEDIATE_START
            };

            if (ioctl(fd, DMX_SET_PES_FILTER, (IntPtr)(&filter)) < 0)
            {
                var error = Marshal.GetLastPInvokeErrorMessage();
                close(fd);
                throw new IOException($"DMX_SET_PES_FILTER failed on {path}: {error}");
            }

            return fd;
        }

        public void Dispose()
        {
            lock (_frontendLock)
            {
                if (_frontendFd < 0) return;
                close(_frontendFd);
                _frontendFd = -1;
            }
            GC.SuppressFinalize(this);
        }
    }
}
