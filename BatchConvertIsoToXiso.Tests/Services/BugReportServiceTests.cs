using BatchConvertIsoToXiso.Services;
using Xunit;

namespace BatchConvertIsoToXiso.Tests.Services;

public class BugReportServiceTests
{
    [Fact]
    public void BuildFullMessageMessageAlreadyContainsEnvironmentDetailsReturnsUnchanged()
    {
        const string message = "=== Environment Details ===\nSome existing details\nMore info";
        var result = BugReportService.BuildFullMessage(message);
        Assert.Equal(message, result);
    }

    [Fact]
    public void BuildFullMessageCaseInsensitiveReturnsUnchanged()
    {
        const string message = "=== environment details ===\nSome existing details";
        var result = BugReportService.BuildFullMessage(message);
        Assert.Equal(message, result);
    }

    [Fact]
    public void BuildFullMessageSimpleMessageContainsExpectedEnvironmentSections()
    {
        const string message = "Test bug report message";
        var result = BugReportService.BuildFullMessage(message);

        Assert.Contains("=== Environment Details ===", result);
        Assert.Contains("Date:", result);
        Assert.Contains("Application Name:", result);
        Assert.Contains("Application Version:", result);
        Assert.Contains("OS Version:", result);
        Assert.Contains("Architecture:", result);
        Assert.Contains("Bitness:", result);
        Assert.Contains("Windows Version:", result);
        Assert.Contains("Processor Count:", result);
        Assert.Contains("Base Directory:", result);
        Assert.Contains("Temp Path:", result);
        Assert.Contains(message, result);
    }

    [Fact]
    public void BuildFullMessageCreatesValidFormattedOutput()
    {
        const string message = "Something broke!";
        var result = BugReportService.BuildFullMessage(message);

        Assert.StartsWith("=== Environment Details ===", result);
        Assert.NotEmpty(result);
    }

    [Fact]
    public void ConstructorInitializesWithValidParameters()
    {
        using var httpClient = new HttpClient();
        var service = new BugReportService(httpClient, "https://api.example.com", "test-key", "TestApp");
        Assert.NotNull(service);
    }

    [Fact]
    public void ConstructorWithDisposeDoesNotThrow()
    {
        using var httpClient = new HttpClient();
        var service = new BugReportService(httpClient, "https://api.example.com", "test-key", "TestApp");
        Assert.NotNull(service);
    }
}
