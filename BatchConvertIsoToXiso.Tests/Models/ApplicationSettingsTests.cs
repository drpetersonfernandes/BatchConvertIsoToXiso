using BatchConvertIsoToXiso.Models;
using Xunit;

namespace BatchConvertIsoToXiso.Tests.Models;

public class ApplicationSettingsTests
{
    [Fact]
    public void DefaultValuesAreEmptyOrFalse()
    {
        var settings = new ApplicationSettings();

        Assert.Equal(string.Empty, settings.ConversionInputFolder);
        Assert.Equal(string.Empty, settings.ConversionOutputFolder);
        Assert.Equal(string.Empty, settings.TestInputFolder);
        Assert.False(settings.DeleteOriginals);
        Assert.False(settings.SearchSubfoldersConversion);
        Assert.False(settings.SkipSystemUpdate);
        Assert.False(settings.CheckOutputIntegrity);
        Assert.False(settings.MoveSuccessFiles);
        Assert.False(settings.MoveFailedFiles);
        Assert.False(settings.SearchSubfoldersTest);
    }

    [Fact]
    public void PropertiesCanBeSet()
    {
        var settings = new ApplicationSettings
        {
            ConversionInputFolder = "C:\\Input",
            ConversionOutputFolder = "C:\\Output",
            TestInputFolder = "C:\\Test",
            DeleteOriginals = true,
            SearchSubfoldersConversion = true,
            SkipSystemUpdate = true,
            CheckOutputIntegrity = true,
            MoveSuccessFiles = true,
            MoveFailedFiles = true,
            SearchSubfoldersTest = true
        };

        Assert.Equal("C:\\Input", settings.ConversionInputFolder);
        Assert.Equal("C:\\Output", settings.ConversionOutputFolder);
        Assert.Equal("C:\\Test", settings.TestInputFolder);
        Assert.True(settings.DeleteOriginals);
        Assert.True(settings.SearchSubfoldersConversion);
        Assert.True(settings.SkipSystemUpdate);
        Assert.True(settings.CheckOutputIntegrity);
        Assert.True(settings.MoveSuccessFiles);
        Assert.True(settings.MoveFailedFiles);
        Assert.True(settings.SearchSubfoldersTest);
    }
}
