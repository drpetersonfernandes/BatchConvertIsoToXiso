using BatchConvertIsoToXiso.Interfaces;
using BatchConvertIsoToXiso.Services;
using Moq;
using Xunit;

namespace BatchConvertIsoToXiso.Tests.Services;

public class UrlOpenerServiceTests
{
    private readonly Mock<ILogger> _loggerMock = new();
    private readonly Mock<IBugReportService> _bugReportMock = new();

    [Fact]
    public void ConstructorInitializesProperties()
    {
        var service = new UrlOpenerService(_loggerMock.Object, _bugReportMock.Object);
        Assert.NotNull(service);
    }

    [Fact]
    public void OpenUrlWithInvalidUrlThrowsAndLogs()
    {
        var service = new UrlOpenerService(_loggerMock.Object, _bugReportMock.Object);

        var ex = Record.Exception(() => service.OpenUrl("not_a_valid_url"));

        Assert.NotNull(ex);
        _loggerMock.Verify(static l => l.LogMessage(It.Is<string>(static s => s.Contains("Error opening URL"))), Times.Once);
        _bugReportMock.Verify(static b => b.SendBugReportAsync(It.IsAny<string>(), It.IsAny<Exception>()), Times.Once);
    }
}
