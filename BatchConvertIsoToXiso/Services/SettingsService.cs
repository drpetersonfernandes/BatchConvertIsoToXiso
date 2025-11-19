using System.Diagnostics;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Serialization;
using BatchConvertIsoToXiso.Models;

namespace BatchConvertIsoToXiso.Services;

public interface ISettingsService
{
    ApplicationSettings LoadSettings();
    void SaveSettings(ApplicationSettings settings);
}

public class SettingsService : ISettingsService
{
    private readonly string _settingsFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.xml");

    public ApplicationSettings LoadSettings()
    {
        if (!File.Exists(_settingsFilePath))
        {
            return new ApplicationSettings();
        }

        try
        {
            var serializer = new XmlSerializer(typeof(ApplicationSettings));

            // Secure XmlReaderSettings
            var xmlReaderSettings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, // Disable DTD entirely
                XmlResolver = null, // Prevent external entity resolution
                IgnoreWhitespace = true, // Optional: cleaner parsing
                MaxCharactersFromEntities = 0, // Prevent entity expansion attacks
                MaxCharactersInDocument = 10 * 1024 * 1024 // Optional: 10MB max document size
            };

            using var fileStream = new FileStream(_settingsFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var xmlReader = XmlReader.Create(fileStream, xmlReaderSettings);

            var result = serializer.Deserialize(xmlReader) as ApplicationSettings;
            return result ?? new ApplicationSettings();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Settings load failed: {ex.Message}");
            return new ApplicationSettings();
        }
    }

    public void SaveSettings(ApplicationSettings settings)
    {
        try
        {
            var serializer = new XmlSerializer(typeof(ApplicationSettings));

            var xmlWriterSettings = new XmlWriterSettings
            {
                Indent = true,
                Encoding = Encoding.UTF8,
                CheckCharacters = true
            };

            using var fileStream = new FileStream(_settingsFilePath, FileMode.Create, FileAccess.Write, FileShare.None);
            using var xmlWriter = XmlWriter.Create(fileStream, xmlWriterSettings);

            serializer.Serialize(xmlWriter, settings);
        }
        catch (Exception ex)
        {
            // Consider logging the error
            Debug.WriteLine($"Settings save failed: {ex.Message}");
        }
    }
}