using System.ComponentModel;
using System.Diagnostics;

namespace BatchConvertIsoToXiso.Services;

public static class ProcessTerminatorHelper
{
    /// <summary>
    /// Robustly terminates a process with graceful shutdown fallback
    /// </summary>
    public static bool TerminateProcess(Process process, string processName, ILogger logger)
    {
        try
        {
            // Check if process is null or already disposed
            if (process == null)
            {
                logger.LogMessage($"Process {processName} is null, nothing to terminate.");
                return true;
            }

            // Safely check if process has exited
            try
            {
                if (process.HasExited)
                {
                    logger.LogMessage($"Process {processName} has already exited.");
                    return true;
                }
            }
            catch (InvalidOperationException)
            {
                logger.LogMessage($"Process {processName} is not associated with a running process or has been disposed.");
                return true;
            }
        }
        catch (InvalidOperationException)
        {
            // Process was never started or already disposed
            logger.LogMessage($"Process {processName} was not running or already disposed.");
            return true;
        }

        try
        {
            logger.LogMessage($"Attempting graceful termination of {processName}...");

            // Try graceful shutdown first
            if (process.CloseMainWindow())
            {
                if (process.WaitForExit(3000))
                {
                    logger.LogMessage($"Process {processName} exited gracefully.");
                    return true;
                }
            }

            logger.LogMessage($"Graceful termination failed for {processName}, forcing kill...");

            // Force kill with entire process tree
            process.Kill(true);

            // Wait for process to fully exit and release handles
            if (process.WaitForExit(5000))
            {
                logger.LogMessage($"Process {processName} was killed successfully.");
                return true;
            }

            logger.LogMessage($"WARNING: Process {processName} did not exit within timeout after kill.");
            return false;
        }
        catch (InvalidOperationException ex)
        {
            logger.LogMessage($"Process {processName} already exited during termination: {ex.Message}");
            return true;
        }
        catch (Win32Exception ex)
        {
            logger.LogMessage($"Access denied terminating {processName}: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            logger.LogMessage($"Unexpected error terminating {processName}: {ex.Message}");
            return false;
        }
    }
}