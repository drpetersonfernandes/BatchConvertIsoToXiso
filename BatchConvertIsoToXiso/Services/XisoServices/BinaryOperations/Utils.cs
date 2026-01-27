using System.IO;

namespace BatchConvertIsoToXiso.Services.XisoServices.BinaryOperations;

/// <summary>
/// A utility class that provides helper methods for handling file operations
/// such as reading and writing data and processing sectors. This class is
/// intended for use in XISO-related operations involving binary file streams.
/// </summary>
internal static class Utils
{
    public const long SectorSize = 2048;

    // Read uint16 from filestream
    public static ushort ReadUShort(FileStream fs)
    {
        var buffer = new byte[2];
        if (fs.Read(buffer, 0, 2) != 2)
            throw new EndOfStreamException("[ERROR] Failed to read UShort");

        return BitConverter.ToUInt16(buffer, 0);
    }

    // Read uint32 from filestream
    public static uint ReadUInt(FileStream fs)
    {
        var buffer = new byte[4];
        if (fs.Read(buffer, 0, 4) != 4)
            throw new EndOfStreamException("[ERROR] Failed to read UInt32");

        return BitConverter.ToUInt32(buffer, 0);
    }

    // Ensure proper writing to byte array
    public static bool WriteBytes(FileStream fs, byte[] outBa, long offset)
    {
        long numBytes = 0;
        if (offset >= 0)
            fs.Seek(offset, SeekOrigin.Begin);
        while (numBytes < outBa.Length)
        {
            var bytesRead = fs.Read(outBa, 0, (int)(outBa.Length - numBytes));
            if (bytesRead == 0)
                break;

            numBytes += bytesRead;
        }

        return numBytes == outBa.Length;
    }

    // Ensure proper writing to filestream
    public static bool WriteBytes(FileStream inFs, FileStream outFs, long offset, long length, byte[] buf)
    {
        long numBytes = 0;
        if (offset >= 0)
            inFs.Seek(offset, SeekOrigin.Begin);
        while (numBytes < length)
        {
            var bytesRead = inFs.Read(buf, 0, (int)Math.Min(buf.Length, length - numBytes));
            if (bytesRead == 0)
                break;

            outFs.Write(buf, 0, bytesRead);
            numBytes += bytesRead;
        }

        return numBytes == length;
    }

    // Write zeroes to filestream
    public static void WriteZeroes(FileStream outFs, long offset, long length, byte[] buf)
    {
        Array.Clear(buf, 0, buf.Length);
        long numBytes = 0;
        if (offset >= 0)
            outFs.Seek(offset, SeekOrigin.Begin);
        while (numBytes < length)
        {
            var bytesToWrite = (int)Math.Min(buf.Length, length - numBytes);
            outFs.Write(buf, 0, bytesToWrite);
            numBytes += bytesToWrite;
        }
    }
}