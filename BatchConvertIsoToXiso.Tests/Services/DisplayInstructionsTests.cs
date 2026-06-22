using BatchConvertIsoToXiso.Interfaces;
using BatchConvertIsoToXiso.Services;
using Moq;
using Xunit;

namespace BatchConvertIsoToXiso.Tests.Services;

public class DisplayInstructionsTests
{
    [Fact]
    public void DisplayInitialInstructionsWhenNotInitializedDoesNotThrow()
    {
        var exception = Record.Exception(DisplayInstructions.DisplayInitialInstructions);
        Assert.Null(exception);
    }

    [Fact]
    public void DisplayInitialInstructionsWhenInitializedLogsWelcomeMessage()
    {
        var mockLogger = new Mock<ILogger>();
        DisplayInstructions.Initialize(mockLogger.Object);

        DisplayInstructions.DisplayInitialInstructions();

        mockLogger.Verify(static x => x.LogMessage("Welcome to 'Batch Convert ISO to XISO'."),
            Times.Once);
    }

    [Fact]
    public void DisplayInitialInstructionsLogsApplicationFunctions()
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
    public void DisplayInitialInstructionsLogsPlatformWarning()
    {
        var mockLogger = new Mock<ILogger>();
        DisplayInstructions.Initialize(mockLogger.Object);

        DisplayInstructions.DisplayInitialInstructions();

        mockLogger.Verify(static x => x.LogMessage(It.Is<string>(static s => s.Contains("Xbox") && s.Contains("IMPORTANT"))),
            Times.Once);
    }

    [Fact]
    public void DisplayInitialInstructionsLogsReadyMessage()
    {
        var mockLogger = new Mock<ILogger>();
        DisplayInstructions.Initialize(mockLogger.Object);

        DisplayInstructions.DisplayInitialInstructions();

        mockLogger.Verify(static x => x.LogMessage("--- Ready ---"),
            Times.Once);
    }

    [Fact]
    public void DisplayInitialInstructionsLogsBchunkStatus()
    {
        var mockLogger = new Mock<ILogger>();
        DisplayInstructions.Initialize(mockLogger.Object);

        DisplayInstructions.DisplayInitialInstructions();

        mockLogger.Verify(static x => x.LogMessage(It.Is<string>(static s => s.Contains("bchunk.exe"))),
            Times.Once);
    }

    [Fact]
    public void DisplayInitialInstructionsLogsExtractXisoStatus()
    {
        var mockLogger = new Mock<ILogger>();
        DisplayInstructions.Initialize(mockLogger.Object);

        DisplayInstructions.DisplayInitialInstructions();

        mockLogger.Verify(static x => x.LogMessage(It.Is<string>(static s => s.Contains("extract-xiso.exe"))),
            Times.Once);
    }

    [Fact]
    public void DisplayInitialInstructionsLogsXdvdfsStatus()
    {
        var mockLogger = new Mock<ILogger>();
        DisplayInstructions.Initialize(mockLogger.Object);

        DisplayInstructions.DisplayInitialInstructions();

        mockLogger.Verify(static x => x.LogMessage(It.Is<string>(static s => s.Contains("xdvdfs.exe"))),
            Times.Once);
    }
}
