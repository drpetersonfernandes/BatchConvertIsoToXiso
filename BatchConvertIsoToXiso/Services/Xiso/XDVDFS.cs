using System.IO;

namespace BatchConvertIsoToXiso.Services.Xiso;

internal static class Xdvdfs
{
    private const long XisoHeaderOffset = 0x10000;
    private static readonly byte[] Filler = "ABCDABCDABCDABCD"u8.ToArray();
    public static readonly byte[] Magic = "XBOX_DVD_LAYOUT_TOOL_SIG"u8.ToArray();

    // Traverse file tree to get all valid data sectors in XISO
    private static void GetValidSectors(FileStream isoFs, long isoOffset, List<uint> validSectors, long rootOffset, uint rootSize, long childOffset, bool quiet)
    {
        while (true)
        {
            if (childOffset >= rootSize) return;

            var cur = isoOffset + rootOffset + childOffset;
            var curOffset = cur / Utils.SectorSize;
            var curSize = (rootSize - childOffset + Utils.SectorSize - 1) / Utils.SectorSize;
            for (var i = curOffset; i < curOffset + curSize; i++) validSectors.Add((uint)i);

            isoFs.Seek(cur, SeekOrigin.Begin);

            var leftChildOffset = Utils.ReadUShort(isoFs);
            if (leftChildOffset == 0xFFFF) return;

            var rightChildOffset = Utils.ReadUShort(isoFs);
            var entryOffset = Utils.ReadUInt(isoFs) * Utils.SectorSize;
            var entrySize = Utils.ReadUInt(isoFs);
            var isDirectory = ((byte)isoFs.ReadByte() & 0x10) != 0;

            if (leftChildOffset != 0) GetValidSectors(isoFs, isoOffset, validSectors, rootOffset, rootSize, (long)leftChildOffset * 4, quiet);

            if (isDirectory)
            {
                GetValidSectors(isoFs, isoOffset, validSectors, entryOffset, entrySize, 0, quiet);
            }
            else
            {
                var fileOffset = (isoOffset + entryOffset) / Utils.SectorSize;
                var fileSize = (entrySize + Utils.SectorSize - 1) / Utils.SectorSize;
                for (var i = fileOffset; i < fileOffset + fileSize; i++) validSectors.Add((uint)i);
            }

            if (rightChildOffset != 0)
            {
                childOffset = (long)rightChildOffset * 4;
                continue;
            }

            break;
        }
    }

    // Get list of valid XISO ranges
    public static List<(uint Start, uint End)> GetXisoRanges(FileStream isoFs, long offset, bool quiet)
    {
        var validSectors = new List<uint>();
        var headerOffset = offset + XisoHeaderOffset;
        var headerOffsetSector = (headerOffset) / Utils.SectorSize;
        validSectors.Add((uint)headerOffsetSector);
        validSectors.Add((uint)headerOffsetSector + 1);

        isoFs.Seek(headerOffset + 20, SeekOrigin.Begin);
        var rootOffset = Utils.ReadUInt(isoFs);
        var rootSize = Utils.ReadUInt(isoFs);
        GetValidSectors(isoFs, offset, validSectors, rootOffset * Utils.SectorSize, rootSize, 0, quiet);

        var ranges = new List<(uint, uint)>();
        var sortedSectors = validSectors.Distinct().OrderBy(x => x).ToList();
        var start = sortedSectors[0];
        var prev = sortedSectors[0];
        for (var i = 1; i < sortedSectors.Count; i++)
        {
            var current = sortedSectors[i];
            if (current == prev + 1)
            {
                prev = current;
            }
            else
            {
                ranges.Add((start, prev));
                start = current;
                prev = current;
            }
        }

        ranges.Add((start, prev));

        return ranges;
    }

    // Heuristic to determine XGD3 system update file offset in video partition
    public static long SuOffset(FileStream videoFs)
    {
        var updateOffset = videoFs.Length;
        var videoBuf = new byte[16];
        while (updateOffset >= Utils.SectorSize)
        {
            videoFs.Seek(updateOffset - Utils.SectorSize, SeekOrigin.Begin);
            var bytesRead = 0;
            while (bytesRead < videoBuf.Length)
            {
                var n = videoFs.Read(videoBuf, bytesRead, videoBuf.Length - bytesRead);
                if (n == 0)
                    break;

                bytesRead += n;
            }

            if (Filler.AsSpan().SequenceEqual(videoBuf))
                break;

            updateOffset -= Utils.SectorSize;
        }

        return updateOffset;
    }
}
