using System.Xml;
using System.Xml.Serialization;
using BatchConvertIsoToXiso.Models;
using Xunit;

namespace BatchConvertIsoToXiso.Tests.Models;

public class ApplicationSettingsSerializationTests
{
    [Fact]
    public void CanSerializeAndDeserialize()
    {
        var original = new ApplicationSettings
        {
            ConversionInputFolder = "C:\\Input",
            ConversionOutputFolder = "C:\\Output",
            TestInputFolder = "C:\\Test",
            DeleteOriginals = true,
            SearchSubfoldersConversion = true,
            SkipSystemUpdate = false,
            CheckOutputIntegrity = true,
            MoveSuccessFiles = true,
            MoveFailedFiles = false,
            SearchSubfoldersTest = true
        };

        var serializer = new XmlSerializer(typeof(ApplicationSettings));
        using var writer = new StringWriter();
        serializer.Serialize(writer, original);
        var xml = writer.ToString();

        Assert.Contains("<ConversionInputFolder>C:\\Input</ConversionInputFolder>", xml);
        Assert.Contains("<DeleteOriginals>true</DeleteOriginals>", xml);

        using var stringReader = new StringReader(xml);
        using var xmlReader = XmlReader.Create(stringReader, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null
        });
        // ReSharper disable once NullableWarningSuppressionIsUsed
        var deserialized = (ApplicationSettings)serializer.Deserialize(xmlReader)!;

        Assert.Equal(original.ConversionInputFolder, deserialized.ConversionInputFolder);
        Assert.Equal(original.ConversionOutputFolder, deserialized.ConversionOutputFolder);
        Assert.Equal(original.TestInputFolder, deserialized.TestInputFolder);
        Assert.Equal(original.DeleteOriginals, deserialized.DeleteOriginals);
        Assert.Equal(original.SearchSubfoldersConversion, deserialized.SearchSubfoldersConversion);
        Assert.Equal(original.SkipSystemUpdate, deserialized.SkipSystemUpdate);
        Assert.Equal(original.CheckOutputIntegrity, deserialized.CheckOutputIntegrity);
        Assert.Equal(original.MoveSuccessFiles, deserialized.MoveSuccessFiles);
        Assert.Equal(original.MoveFailedFiles, deserialized.MoveFailedFiles);
        Assert.Equal(original.SearchSubfoldersTest, deserialized.SearchSubfoldersTest);
    }

    [Fact]
    public void DeserializeWithMissingElementsUsesDefaults()
    {
        const string xml = """
                           <?xml version="1.0" encoding="utf-16"?>
                           <ApplicationSettings xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
                             <ConversionInputFolder>C:\Input</ConversionInputFolder>
                           </ApplicationSettings>
                           """;

        var serializer = new XmlSerializer(typeof(ApplicationSettings));
        using var stringReader = new StringReader(xml);
        using var xmlReader = XmlReader.Create(stringReader, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null
        });
        // ReSharper disable once NullableWarningSuppressionIsUsed
        var deserialized = (ApplicationSettings)serializer.Deserialize(xmlReader)!;

        Assert.Equal("C:\\Input", deserialized.ConversionInputFolder);
        Assert.Equal(string.Empty, deserialized.ConversionOutputFolder);
        Assert.False(deserialized.DeleteOriginals);
    }
}
