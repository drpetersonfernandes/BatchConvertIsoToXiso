namespace BatchConvertIsoToXiso.Models.XisoDefinitions;

[Flags]
public enum XisoFsFileAttributes : byte
{
    ReadOnly = 0x01,
    Hidden = 0x02,
    System = 0x04,
    Directory = 0x10,
    Archive = 0x20,
    Normal = 0x80
}