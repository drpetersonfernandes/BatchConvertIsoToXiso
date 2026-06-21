using BatchConvertIsoToXiso.Interfaces;
using BatchConvertIsoToXiso.Services;
using Moq;
using Xunit;

namespace BatchConvertIsoToXiso.Tests.Services;

public class TempFolderCleanupHelperTests
{
    [Fact]
    public async Task TryDeleteDirectoryWithRetryAsync_NonExistentDirectory_DoesNotThrow()
    {
        var nonExistentPath = Path.Combine(Path.GetTempPath(), $"NonExistent_Test_{Guid.NewGuid()}");
        var mockLogger = new Mock<ILogger>();

        var exception = await Record.ExceptionAsync(() =>
            TempFolderCleanupHelper.TryDeleteDirectoryWithRetryAsync(nonExistentPath, 3, 100, mockLogger.Object));

        Assert.Null(exception);
        mockLogger.Verify(static x => x.LogMessage(It.Is<string>(static s => s.Contains("WARNING") || s.Contains("Failed"))), Times.Never);
    }

    [Fact]
    public async Task TryDeleteDirectoryWithRetryAsync_ExistingEmptyDirectory_GetsDeleted()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"BatchConvertIsoToXiso_Test_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        var mockLogger = new Mock<ILogger>();

        try
        {
            Assert.True(Directory.Exists(tempDir));

            await TempFolderCleanupHelper.TryDeleteDirectoryWithRetryAsync(tempDir, 3, 100, mockLogger.Object);

            Assert.False(Directory.Exists(tempDir));
            mockLogger.Verify(static x => x.LogMessage(It.Is<string>(static s => s.Contains("Successfully deleted"))), Times.Once);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task TryDeleteDirectoryWithRetryAsync_NullLogger_DoesNotThrow()
    {
        var nonExistentPath = Path.Combine(Path.GetTempPath(), $"NonExistent_Test_{Guid.NewGuid()}");

        var exception = await Record.ExceptionAsync(() =>
            TempFolderCleanupHelper.TryDeleteDirectoryWithRetryAsync(nonExistentPath, 3, 100, null));

        Assert.Null(exception);
    }

    [Fact]
    public async Task TryDeleteDirectoryWithRetryAsync_ExistingDirectory_CallsLogSuccess()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"BatchConvertIsoToXiso_Test_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        var mockLogger = new Mock<ILogger>();

        try
        {
            await TempFolderCleanupHelper.TryDeleteDirectoryWithRetryAsync(tempDir, 3, 100, mockLogger.Object);

            mockLogger.Verify(static x => x.LogMessage(It.Is<string>(static s => s.Contains("Successfully deleted temp folder:"))), Times.Once);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }
}
