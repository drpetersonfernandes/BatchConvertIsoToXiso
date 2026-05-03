using BatchConvertIsoToXiso.interfaces;
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
}
