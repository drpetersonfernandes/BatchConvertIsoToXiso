using System.IO;
using BatchConvertIsoToXiso.Services.XisoServices.BinaryOperations;

namespace BatchConvertIsoToXiso.Services.XisoServices.XDVDFS;

/// <summary>
/// Represents a volume descriptor in an XDVDFS (Xbox Disc Volume Descriptor File System).
/// The class is used to read and parse volume descriptor data from ISO files.
/// It provides details like the root directory table sector, which is essential for file operations on the ISO structure.
/// </summary>
/// <remarks>
/// This class attempts to locate and interpret the volume descriptor using several strategies:
/// 1. The Standard position (Sector 32, Offset 0).
/// 2. The Game Partition Offset, applicable for dual-layer ISOs.
/// 3. A rebuilt XISO structure starting at Sector 0.
/// If none of these strategies succeed, an exception is thrown indicating the absence of a valid volume descriptor.
/// </remarks>
/// <exception cref="InvalidDataException">Thrown when a valid XDVDFS volume descriptor cannot be located.</exception>
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
