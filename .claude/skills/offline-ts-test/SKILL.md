---
name: offline-ts-test
description: Verify LinTv's MPEG-TS / PSI / PSIP code (LinTv.Core/Mpeg) without a tuner by feeding synthetic packets through it with a .NET 10 file-based app. Use after changing the framer, section assembler, VCT/MGT/EIT/ETT/STT parsers, PsipGuideCollector or ProgramDemuxer.
---

# Offline MPEG/PSIP test

There's no test project, and the tuner only exists on the server. Instead, write a throwaway **file-based app** in the scratchpad directory, never in the repo. It builds real sections (valid CRCs), packetizes them, pushes them through the code under test in awkward chunk sizes, and prints actual vs. expected values.

## Header (both lines are required)

```csharp
#:project D:/Source/repos/LinTV/LinTv.Core/LinTv.Core.csproj
#:property PublishAot=false
```

File-based apps default to `PublishAot=true`. That pulls in the ILCompiler package, which the machine's NuGet package source mapping blocks (error `NU1100`).

Run it with `dotnet run test.cs`.

## Helpers

```csharp
using LinTv.Core.Mpeg;

// Long-form section: ext = table_id_extension (TSID, program_number or source_id), version 0-31.
static byte[] Section(byte tableId, ushort ext, byte version, byte sec, byte last, byte[] body)
{
    int len = 5 + body.Length + 4;
    var s = new List<byte> { tableId, (byte)(0xB0 | (len >> 8)), (byte)len, (byte)(ext >> 8), (byte)ext,
                             (byte)(0xC1 | (version << 1)), sec, last };
    s.AddRange(body);
    uint crc = Crc32Mpeg.Compute(s.ToArray());
    s.AddRange([(byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc]);
    return s.ToArray();
}

// Packetize one section on a PID (pointer_field 0, continuity per PID, 0xFF stuffing).
var cc = new Dictionary<ushort, int>();
List<byte> Packetize(ushort pid, byte[] section)
{
    var data = new byte[] { 0 }.Concat(section).ToArray();
    var packets = new List<byte>();
    for (int off = 0; off < data.Length; off += 184)
    {
        var p = Enumerable.Repeat((byte)0xFF, 188).ToArray();
        int c = cc.GetValueOrDefault(pid); cc[pid] = (c + 1) & 0xF;
        p[0] = 0x47; p[1] = (byte)((off == 0 ? 0x40 : 0) | (pid >> 8)); p[2] = unchecked((byte)pid); p[3] = (byte)(0x10 | c);
        data.AsSpan(off, Math.Min(184, data.Length - off)).CopyTo(p.AsSpan(4));
        packets.AddRange(p);
    }
    return packets;
}
```

Casting a PID constant to `byte` needs `unchecked(...)`, because constant overflow is a compile error.

## What to cover

- **Chunking:** chunk sizes that aren't multiples of 188 (e.g. 97, 1000), and leading garbage that includes a stray `0x47`.
- **Multi-packet sections:** pad a descriptor to push a section past 184 bytes.
- **Rejection:** a corrupted byte must fail the CRC and produce no section. Also cover a duplicate or out-of-order continuity counter.
- **Order:** tables arriving before their parent (EIT before MGT, ES before PMT) must be ignored.
- **Version changes:** a new PAT/PMT version, and for the demuxer, a PMT that moves to a new PID.

Print `want:` next to each actual value so a regression is obvious. Report the results to the user; don't commit the script.
