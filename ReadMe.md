# Batch ISO to XISO Converter

[![Platform](https://img.shields.io/badge/Platform-Windows-lightgrey.svg)](https://www.microsoft.com/windows)
[![.NET 10.0](https://img.shields.io/badge/.NET-10.0-blue.svg)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![Platform](https://img.shields.io/badge/platform-Windows%20x64%20%7C%20ARM64-blue)](https://github.com/drpetersonfernandes/BatchConvertIsoToXiso/releases)
[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](LICENSE.txt)
[![GitHub release](https://img.shields.io/github/v/release/drpetersonfernandes/BatchConvertIsoToXiso)](https://github.com/drpetersonfernandes/BatchConvertIsoToXiso/releases)

**Batch ISO to XISO** is a high-performance Windows WPF utility designed for the Xbox preservation and emulation community. It provides a streamlined way to convert standard Xbox and Xbox 360 ISOs into the optimized, trimmed **XISO** format, verify their structural integrity, and explore their contents.

Built with a **native C# XDVDFS engine**, this tool eliminates the need for external command-line tools for core ISO operations, offering superior speed and modern features like real-time disk write monitoring.

![Convert Screenshot](screenshot.png)
![Test Screenshot](screenshot2.png)
![Explorer Screenshot](screenshot3.png)

---

## 🚀 Key Features

### 1. Batch Conversion
*   **Smart Trimming**: Rebuilds ISOs into the XISO format using a native traversal engine, removing gigabytes of unnecessary system padding.
*   **Archive Support**: Directly process `.zip`, `.7z`, and `.rar` files. Powered by `SharpCompress` for high-performance extraction.
*   **CUE/BIN Support**: Integrated `bchunk` support to convert old-school disc images to ISO before processing.
*   **System Update Removal**: Option to skip the `$SystemUpdate` folder to save additional space.

### 2. Integrity Testing
*   **Structural Validation**: Deep traversal of the XDVDFS file tree to ensure the filesystem is valid and readable.
*   **Deep Surface Scan**: Optional sequential sector reading to detect physical data corruption or "bad sectors".
*   **Batch Organization**: Automatically move "Passed" or "Failed" images into dedicated subfolders.

### 3. XISO Explorer
*   **Native Browsing**: Open any Xbox ISO to browse files and directories without extraction.
*   **Metadata View**: View file sizes, attributes, and directory structures directly within the UI.

### 4. Advanced Monitoring
*   **Real-time Stats**: Track success/fail counts, elapsed time, and processed file lists.
*   **Disk Monitor**: Live monitoring of write speeds and drive activity to identify hardware bottlenecks.

---

## 🏗️ Architecture Refactor

The application has been recently refactored to follow modern software engineering principles, ensuring a robust and maintainable codebase:

### Dependency Injection (DI)
The project now utilizes `Microsoft.Extensions.DependencyInjection` for comprehensive service management. All core logic is decoupled from the UI, allowing for easier testing and modular updates.

### Service-Oriented Design
The codebase is organized into specialized services, each with a clear responsibility:

| Interface | Service Implementation | Responsibility |
| :--- | :--- | :--- |
| `IOrchestratorService` | `OrchestratorService` | Coordinates the high-level workflow of batch operations. |
| `INativeIsoIntegrityService` | `NativeIsoIntegrityService` | Handles XDVDFS filesystem validation and surface scanning. |
| (Native Class) | `XisoWriter` | Core engine for rewriting and trimming ISO files. |
| `IFileExtractor` | `FileExtractorService` | Manages archive extraction with robust error handling. |
| `IFileMover` | `FileMoverService` | Handles safe file relocation with retry logic and disk monitoring. |
| `IDiskMonitorService` | `DiskMonitorService` | Provides real-time metrics on disk I/O performance. |
| `IBugReportService` | `BugReportService` | Automated error reporting and diagnostic collection. |
| `ILogger` | `LoggerService` | Centralized asynchronous logging across the application. |
| `IUpdateChecker` | `UpdateChecker` | Manages version checks via the GitHub API. |
| `IMessageBoxService` | `MessageBoxService` | Abstracted UI interactions for better testability. |

### Clean Code-Behind
The `MainWindow` has been refactored into logical **partial classes** (`ConversionAndTesting`, `UIHelpers`, `XIsoExplorerLogic`, etc.), significantly improving readability and preventing the "God Object" anti-pattern.

---

## 📂 Supported Formats

| Task | Supported Input Formats |
| :--- | :--- |
| **Conversion** | `.iso`, `.zip`, `.7z`, `.rar`, `.cue` / `.bin` |
| **Testing** | `.iso` (Direct files) |
| **Explorer** | `.iso` (Xbox/Xbox 360 XDVDFS) |

---

## 🛠️ Requirements

*   **Operating System**: Windows 10 (version 1809) or later / Windows 11.
*   **Runtime**: [.NET 10.0 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0).
*   **Architecture**: x64 (64-bit) is required for archive extraction features.

---

## 🛡️ Safety & Reliability

*   **Cloud-Aware**: Automatically detects and handles files stored in the cloud (e.g., OneDrive), prompting for hydration instead of failing.
*   **Atomic Operations**: Converted files are verified before any original files are deleted.
*   **Automatic Cleanup**: The application features a `TempFolderCleanupHelper` that ensures orphaned temporary files are removed on startup or after crashes.
*   **Robust Error Handling**: Comprehensive exception handling with automatic bug reporting and graceful degradation.

---

## 📜 Acknowledgements

*   **XboxKit**: Used as a reference for the core XDVDFS logic. [GitHub Repository](https://github.com/Deterous/XboxKit).
*   **bchunk**: Used for CUE/BIN to ISO conversion.
*   **SharpCompress**: Used for high-performance archive extraction.
*   **Pure Logic Code**: Developed and maintained by [Pure Logic Code](https://www.purelogiccode.com).

---

⭐ **If you find this tool useful, please give us a Star on GitHub!** ⭐
