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

    /// <summary>
    /// Known XDVDFS partition offsets for different Xbox game disc formats.
    /// Shared between VolumeDescriptor and Xdvdfs for consistency.
    /// </summary>
    public static readonly long[] GamePartitionOffsets =
    [
        0,              // XISO (already trimmed or standard XISO without video partition)
        0x02080000,     // XGD3 (~32.5MB)
        0x0FD90000,     // XGD2 (~253.5MB)
        0x18300000,     // XGD1 (~387MB)
        0x89D80000      // Additional variant (~2.2GB)
    ];

    private static readonly byte[] MagicId = "MICROSOFT*XBOX*MEDIA"u8.ToArray();

    public uint RootDirTableSector { get; private set; }
    public uint RootDirSize { get; private set; }

    private VolumeDescriptor(IsoSt isoSt, uint sector, long byteOffset)
    {
        isoSt.ExecuteLocked(reader =>
        {
            var sectorStart = byteOffset + sector * Utils.SectorSize;
            if (sectorStart + 0x800 > reader.BaseStream.Length) throw new EndOfStreamException();

            // Validate Magic ID 1 first (before reading any data)
            reader.BaseStream.Seek(sectorStart, SeekOrigin.Begin);
            var id1 = reader.ReadBytes(0x14);
            if (!id1.SequenceEqual(MagicId)) throw new InvalidDataException();

            // Validate Magic ID 2
            reader.BaseStream.Seek(sectorStart + 0x7EC, SeekOrigin.Begin);
            var id2 = reader.ReadBytes(0x14);
            if (!id2.SequenceEqual(MagicId)) throw new InvalidDataException();

            // Now read root directory info (sector + size)
            reader.BaseStream.Seek(sectorStart + 0x14, SeekOrigin.Begin);
            RootDirTableSector = reader.ReadUInt32();
            RootDirSize = reader.ReadUInt32();
        });
    }

    public static VolumeDescriptor ReadFrom(IsoSt isoSt)
    {
        // 1. Try all known Game Partition Offsets at Sector 32 (standard XDVDFS location)
        //    This includes offset 0 for standard XISOs and various XGD offsets for dual-layer discs
        foreach (var offset in GamePartitionOffsets)
        {
            try
            {
                var vol = new VolumeDescriptor(isoSt, VolumeDescriptorSector, offset);
                isoSt.VolumeOffset = offset;
                return vol;
            }
            catch
            {
                // ignored
            }
        }

        // 2. Try Sector 0 (Rebuilt XISO without video partition header)
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
