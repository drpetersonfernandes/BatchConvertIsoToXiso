using System.Xml.Serialization;

namespace BatchConvertIsoToXiso.Models;

[XmlRoot("ApplicationSettings")]
public class ApplicationSettings
{
    [XmlElement("ConversionInputFolder")]
    public string ConversionInputFolder { get; set; } = string.Empty;

    [XmlElement("ConversionOutputFolder")]
    public string ConversionOutputFolder { get; set; } = string.Empty;

    [XmlElement("TestInputFolder")]
    public string TestInputFolder { get; set; } = string.Empty;

    [XmlElement("DeleteOriginals")]
    public bool DeleteOriginals { get; set; }

    [XmlElement("SearchSubfoldersConversion")]
    public bool SearchSubfoldersConversion { get; set; }

    [XmlElement("SkipSystemUpdate")]
    public bool SkipSystemUpdate { get; set; }

    [XmlElement("CheckOutputIntegrity")]
    public bool CheckOutputIntegrity { get; set; }

    [XmlElement("MoveSuccessFiles")]
    public bool MoveSuccessFiles { get; set; }

    [XmlElement("MoveFailedFiles")]
    public bool MoveFailedFiles { get; set; }

    [XmlElement("SearchSubfoldersTest")]
    public bool SearchSubfoldersTest { get; set; }
}