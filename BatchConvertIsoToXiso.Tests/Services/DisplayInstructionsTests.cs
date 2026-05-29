using BatchConvertIsoToXiso.interfaces;
using BatchConvertIsoToXiso.Services;
using Moq;
using Xunit;

namespace BatchConvertIsoToXiso.Tests.Services;

public class DisplayInstructionsTests
{
    [Fact]
    public void DisplayInitialInstructions_WhenNotInitialized_DoesNotThrow()
    {
        var exception = Record.Exception(DisplayInstructions.DisplayInitialInstructions);
        Assert.Null(exception);
    }

    [Fact]
    public void DisplayInitialInstructions_WhenInitialized_LogsWelcomeMessage()
    {
        var mockLogger = new Mock<ILogger>();
        DisplayInstructions.Initialize(mockLogger.Object);

        DisplayInstructions.DisplayInitialInstructions();

        mockLogger.Verify(static x => x.LogMessage("Welcome to 'Batch Convert ISO to XISO'."),
            Times.Once);
    }

    [Fact]
    public void DisplayInitialInstructions_LogsApplicationFunctions()
    {
        var mockLogger = new Mock<ILogger>();
        DisplayInstructions.Initialize(mockLogger.Object);

        DisplayInstructions.DisplayInitialInstructions();

        mockLogger.Verify(static x => x.LogMessage(It.Is<string>(static s => s.StartsWith("This application provides"))),
            Times.Once);
        mockLogger.Verify(static x => x.LogMessage(It.Is<string>(static s => s.Contains("Convert"))),
            Times.AtLeastOnce);
        mockLogger.Verify(static x => x.LogMessage(It.Is<string>(static s => s.Contains("Test Integrity"))),
            Times.Once);
        mockLogger.Verify(static x => x.LogMessage(It.Is<string>(static s => s.Contains("Explorer"))),
            Times.Once);
    }

    [Fact]
    public void DisplayInitialInstructions_LogsPlatformWarning()
    {
        var mockLogger = new Mock<ILogger>();
        DisplayInstructions.Initialize(mockLogger.Object);

        DisplayInstructions.DisplayInitialInstructions();

        mockLogger.Verify(static x => x.LogMessage(It.Is<string>(static s => s.Contains("Xbox") && s.Contains("IMPORTANT"))),
            Times.Once);
    }

    [Fact]
    public void DisplayInitialInstructions_LogsReadyMessage()
    {
        var mockLogger = new Mock<ILogger>();
        DisplayInstructions.Initialize(mockLogger.Object);

        DisplayInstructions.DisplayInitialInstructions();

        mockLogger.Verify(static x => x.LogMessage("--- Ready ---"),
            Times.Once);
    }

    [Fact]
    public void DisplayInitialInstructions_LogsBchunkStatus()
    {
        var mockLogger = new Mock<ILogger>();
        DisplayInstructions.Initialize(mockLogger.Object);

        DisplayInstructions.DisplayInitialInstructions();

        mockLogger.Verify(static x => x.LogMessage(It.Is<string>(static s => s.Contains("bchunk.exe"))),
            Times.Once);
    }

    [Fact]
    public void DisplayInitialInstructions_LogsExtractXisoStatus()
    {
        var mockLogger = new Mock<ILogger>();
        DisplayInstructions.Initialize(mockLogger.Object);

        DisplayInstructions.DisplayInitialInstructions();

        mockLogger.Verify(static x => x.LogMessage(It.Is<string>(static s => s.Contains("extract-xiso.exe"))),
            Times.Once);
    }

    [Fact]
    public void DisplayInitialInstructions_LogsXdvdfsStatus()
    {
        var mockLogger = new Mock<ILogger>();
        DisplayInstructions.Initialize(mockLogger.Object);

        DisplayInstructions.DisplayInitialInstructions();

        mockLogger.Verify(static x => x.LogMessage(It.Is<string>(static s => s.Contains("xdvdfs.exe"))),
            Times.Once);
    }
}
