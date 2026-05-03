using System.Text;
using BatchConvertIsoToXiso.Services;
using Xunit;

namespace BatchConvertIsoToXiso.Tests.Services;

public class ExceptionFormatterTests
{
    [Fact]
    public void AppendExceptionDetailsWithSingleExceptionFormatsCorrectly()
    {
        var sb = new StringBuilder();
        var exception = new InvalidOperationException("Test message");

        ExceptionFormatter.AppendExceptionDetails(sb, exception);

        var result = sb.ToString();
        Assert.Contains("Type: System.InvalidOperationException", result);
        Assert.Contains("Message: Test message", result);
        Assert.Contains("Source:", result);
        Assert.Contains("StackTrace:", result);
    }

    [Fact]
    public void AppendExceptionDetailsWithNestedExceptionsIncludesInnerException()
    {
        var sb = new StringBuilder();
        var inner = new ArgumentException("Inner error");
        var outer = new InvalidOperationException("Outer error", inner);

        ExceptionFormatter.AppendExceptionDetails(sb, outer);

        var result = sb.ToString();
        Assert.Contains("Type: System.InvalidOperationException", result);
        Assert.Contains("Message: Outer error", result);
        Assert.Contains("Inner Exception:", result);
        Assert.Contains("Type: System.ArgumentException", result);
        Assert.Contains("Message: Inner error", result);
    }

    [Fact]
    public void AppendExceptionDetailsWithLevelIncludesIndentation()
    {
        var sb = new StringBuilder();

        // Use reflection to set inner exception since constructor param is easier
        // Actually let's just use the normal nesting
        var outer = new InvalidOperationException("Outer", new ArgumentException("Inner"));

        ExceptionFormatter.AppendExceptionDetails(sb, outer, 1);

        var result = sb.ToString();
        // Outer should be indented with 2 spaces
        Assert.Contains("  Type: System.InvalidOperationException", result);
        // Inner should be indented with 4 spaces
        Assert.Contains("    Type: System.ArgumentException", result);
    }
}
