using System.IO;

namespace BatchConvertIsoToXiso.Services.XisoServices;

public class VolumeDescriptor
{
    private const int VolumeDescriptorSector = 32;
    private const long GamePartitionOffset = 2048L * 32 * 6192;
    private static readonly byte[] MagicId = "MICROSOFT*XBOX*MEDIA"u8.ToArray();

    public uint RootDirTableSector { get; private set; }

    private VolumeDescriptor(IsoSt isoSt, uint sector, long byteOffset)
    {
        isoSt.ExecuteLocked(reader =>
        {
            var sectorStart = byteOffset + (long)sector * IsoSt.SectorSize;
            if (sectorStart + 0x800 > reader.BaseStream.Length) throw new EndOfStreamException();

            reader.BaseStream.Seek(sectorStart, SeekOrigin.Begin);
            var id1 = reader.ReadBytes(0x14);

            RootDirTableSector = reader.ReadUInt32();

            // Validate Magic ID 1
            if (!id1.SequenceEqual(MagicId)) throw new InvalidDataException();

            // Validate Magic ID 2
            reader.BaseStream.Seek(sectorStart + 0x7EC, SeekOrigin.Begin);
            var id2 = reader.ReadBytes(0x14);
            if (!id2.SequenceEqual(MagicId)) throw new InvalidDataException();
        });
    }

    public static VolumeDescriptor ReadFrom(IsoSt isoSt)
    {
        // 1. Try Sector 32, Offset 0 (Standard)
        try
        {
            var vol = new VolumeDescriptor(isoSt, VolumeDescriptorSector, 0);
            isoSt.VolumeOffset = 0;
            return vol;
        }
        catch
        {
            // ignored
        }

        // 2. Try Game Partition Offset (Dual Layer)
        try
        {
            var vol = new VolumeDescriptor(isoSt, VolumeDescriptorSector, GamePartitionOffset);
            isoSt.VolumeOffset = GamePartitionOffset;
            return vol;
        }
        catch
        {
            // ignored
        }

        // 3. Try Sector 0 (Rebuilt XISO)
        try
        {
            var vol = new VolumeDescriptor(isoSt, 0, 0);
            isoSt.VolumeOffset = 0;
            return vol;
        }
        catch
        {
            // ignored
        }

        throw new InvalidDataException("Valid XDVDFS volume descriptor not found.");
    }
}
