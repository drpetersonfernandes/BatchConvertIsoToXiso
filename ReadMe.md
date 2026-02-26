# Batch ISO to XISO Converter

[![Platform](https://img.shields.io/badge/Platform-Windows-lightgrey.svg)](https://www.microsoft.com/windows)
[![.NET 10.0](https://img.shields.io/badge/.NET-10.0-blue.svg)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![Platform](https://img.shields.io/badge/platform-Windows%20x64%20%7C%20ARM64-blue)](https://github.com/drpetersonfernandes/BatchConvertIsoToXiso/releases)
[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](LICENSE.txt)
[![GitHub release](https://img.shields.io/github/v/release/drpetersonfernandes/BatchConvertIsoToXiso)](https://github.com/drpetersonfernandes/BatchConvertIsoToXiso/releases)

A high-performance Windows WPF utility for the Xbox preservation and emulation community. Convert, verify, and explore Xbox and Xbox 360 ISO files with dual-engine support: a native C# XDVDFS engine and external tool integration.

![Application Screenshots](screenshot.png)

---

## 📋 Table of Contents

- [Overview](#overview)
- [Key Features](#key-features)
- [Installation](#installation)
- [Usage](#usage)
- [Supported Formats](#supported-formats)
- [Architecture](#architecture)
- [System Requirements](#system-requirements)
- [Safety & Reliability](#safety--reliability)
- [Screenshots](#screenshots)
- [Acknowledgements](#acknowledgements)
- [License](#license)

---

## Overview

**Batch ISO to XISO Converter** streamlines the process of converting standard Xbox and Xbox 360 ISOs into the optimized, trimmed **XISO** format. Built with a flexible dual-engine architecture, the tool combines a **native C# XDVDFS engine** with optional **external tool integration**, delivering superior performance and modern features like real-time disk write monitoring.

Whether you're managing a large collection of Xbox game backups or verifying the integrity of your dumps, this application provides a user-friendly interface with powerful batch processing capabilities.

---

## Key Features

### 🔄 Batch Conversion
- **Dual Engine Support**: Choose between the native C# XDVDFS engine or extract-xiso.exe for conversion
- **Smart Trimming**: Rebuilds ISOs into XISO format, removing unnecessary system padding and saving gigabytes of storage
- **Archive Support**: Process `.zip`, `.7z`, and `.rar` files directly with high-performance extraction via SharpCompress
- **CUE/BIN Support**: Integrated `bchunk` support for converting classic disc images to ISO format
- **System Update Removal**: Option to skip the `$SystemUpdate` folder for additional space savings

### ✅ Integrity Testing
- **Structural Validation**: Deep traversal of the XDVDFS file tree to ensure filesystem validity
- **Deep Surface Scan**: Optional sequential sector reading to detect physical data corruption or bad sectors
- **Batch Organization**: Automatically organize "Passed" or "Failed" images into dedicated subfolders

### 🔍 XISO Explorer
- **Native Browsing**: Open any Xbox ISO to browse files and directories without extraction
- **Metadata View**: View file sizes, attributes, and directory structures directly in the UI

### 📊 Advanced Monitoring
- **Real-time Statistics**: Track success/fail counts, elapsed time, and processed files
- **Disk Monitor**: Live monitoring of write speeds and drive activity to identify hardware bottlenecks
- **Cloud-Aware**: Automatic detection and handling of cloud-stored files (e.g., OneDrive)

---

## Installation

### Prerequisites
- **Operating System**: Windows 10 (version 1809) or later / Windows 11
- **Runtime**: [.NET 10.0 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)
- **Architecture**: x64 (64-bit) required

### Steps
1. Download the latest release from the [Releases](https://github.com/drpetersonfernandes/BatchConvertIsoToXiso/releases) page
2. Extract the ZIP file to your desired location
3. Run `BatchConvertIsoToXiso.exe`

No installation required – the application is fully portable.

---

## Usage

### Converting ISOs
1. Launch the application
2. Click **"Select Input Folder"** and choose the directory containing your ISO files
3. Click **"Select Output Folder"** to specify where converted files will be saved
4. Configure options:
    - **Remove System Update**: Skip `$SystemUpdate` folder to save space
    - **Replace Originals**: Replace input files with converted versions
    - **Test After Conversion**: Automatically verify converted ISOs
    - **Use extract-xiso**: Toggle between native engine and external tool
5. Click **"Convert"** to start the batch process

### Testing ISO Integrity
1. Switch to the **"Test ISO"** tab
2. Select your input folder containing ISO files
3. Enable **"Move Passed Files"** and/or **"Move Failed Files"** to organize results
4. Click **"Test ISOs"** to begin validation

### Exploring XISO Contents
1. Switch to the **"XISO Explorer"** tab
2. Click **"Open ISO"** and select an Xbox ISO file
3. Browse the file tree to view contents without extraction

---

## Supported Formats

| Operation      | Supported Formats                              |
|:---------------|:-----------------------------------------------|
| **Conversion** | `.iso`, `.zip`, `.7z`, `.rar`, `.cue` / `.bin` |
| **Testing**    | `.iso` (Direct files)                          |
| **Explorer**   | `.iso` (Xbox/Xbox 360 XDVDFS)                  |

---

## Architecture

The application follows modern software engineering principles with a clean, maintainable architecture based on **Dependency Injection** and **Service-Oriented Design**.

### Dependency Injection
Utilizes `Microsoft.Extensions.DependencyInjection` for comprehensive service management. All core logic is decoupled from the UI, enabling easier testing and modular updates.

### Service Layer

| Interface                                                                                      | Implementation                                                                                                           | Responsibility                                      |
|:-----------------------------------------------------------------------------------------------|:-------------------------------------------------------------------------------------------------------------------------|:----------------------------------------------------|
| [`IOrchestratorService`](BatchConvertIsoToXiso/Interfaces/IOrchestratorService.cs)             | [`OrchestratorService`](BatchConvertIsoToXiso/Services/OrchestratorService.cs)                                           | Coordinates batch operation workflows               |
| [`INativeIsoIntegrityService`](BatchConvertIsoToXiso/Interfaces/INativeIsoIntegrityService.cs) | [`NativeIsoIntegrityService`](BatchConvertIsoToXiso/Services/XisoServices/BinaryOperations/NativeIsoIntegrityService.cs) | XDVDFS filesystem validation and surface scanning   |
| [`IExtractXisoService`](BatchConvertIsoToXiso/Interfaces/IExtractXisoService.cs)               | [`ExtractXisoService`](BatchConvertIsoToXiso/Services/ExtractXisoService.cs)                                             | External extract-xiso.exe integration               |
| [`IExternalToolService`](BatchConvertIsoToXiso/Interfaces/IExternalToolService.cs)             | [`ExternalToolService`](BatchConvertIsoToXiso/Services/ExternalToolService.cs)                                           | External tool orchestration (bchunk)                |
| [`XisoWriter`](BatchConvertIsoToXiso/Services/XisoServices/XisoWriter.cs)                      | Native Class                                                                                                             | Core C# engine for rewriting and trimming ISO files |
| [`IFileExtractor`](BatchConvertIsoToXiso/Interfaces/IFileExtractor.cs)                         | [`ExtractFiles`](BatchConvertIsoToXiso/Services/ExtractFiles.cs)                                                         | Archive extraction with error handling              |
| [`IFileMover`](BatchConvertIsoToXiso/Interfaces/IFileMover.cs)                                 | [`MoveFiles`](BatchConvertIsoToXiso/Services/MoveFiles.cs)                                                               | Safe file relocation with retry logic               |
| [`IDiskMonitorService`](BatchConvertIsoToXiso/Interfaces/IDiskMonitorService.cs)               | [`DiskMonitorService`](BatchConvertIsoToXiso/Services/DiskMonitorService.cs)                                             | Real-time disk I/O metrics                          |
| [`IBugReportService`](BatchConvertIsoToXiso/Interfaces/IBugReportService.cs)                   | [`BugReportService`](BatchConvertIsoToXiso/Services/BugReportService.cs)                                                 | Automated error reporting                           |
| [`ILogger`](BatchConvertIsoToXiso/Interfaces/ILogger.cs)                                       | [`Logger`](BatchConvertIsoToXiso/Services/Logger.cs)                                                                     | Centralized asynchronous logging                    |
| [`IUpdateChecker`](BatchConvertIsoToXiso/Interfaces/IUpdateChecker.cs)                         | [`UpdateChecker`](BatchConvertIsoToXiso/Services/UpdateChecker.cs)                                                       | Version checks via GitHub API                       |
| [`IMessageBoxService`](BatchConvertIsoToXiso/Interfaces/IMessageBoxService.cs)                 | [`MessageBoxService`](BatchConvertIsoToXiso/Services/MessageBoxService.cs)                                               | Abstracted UI interactions                          |

### XISO Services Namespace
The [`Services/XisoServices/`](BatchConvertIsoToXiso/Services/XisoServices/) directory contains the core XDVDFS implementation:

| File                                                                                                                                         | Purpose                                     |
|:---------------------------------------------------------------------------------------------------------------------------------------------|:--------------------------------------------|
| [`XisoWriter.cs`](BatchConvertIsoToXiso/Services/XisoServices/XisoWriter.cs)                                                                 | Native C# XISO creation and trimming engine |
| [`BinaryOperations/NativeIsoIntegrityService.cs`](BatchConvertIsoToXiso/Services/XisoServices/BinaryOperations/NativeIsoIntegrityService.cs) | ISO integrity testing and surface scanning  |
| [`BinaryOperations/FileEntry.cs`](BatchConvertIsoToXiso/Services/XisoServices/BinaryOperations/FileEntry.cs)                                 | XDVDFS file entry parsing                   |
| [`BinaryOperations/IsoSt.cs`](BatchConvertIsoToXiso/Services/XisoServices/BinaryOperations/IsoSt.cs)                                         | ISO stream operations                       |
| [`BinaryOperations/Utils.cs`](BatchConvertIsoToXiso/Services/XisoServices/BinaryOperations/Utils.cs)                                         | Binary utility functions                    |
| [`XDVDFS/XDVDFS.cs`](BatchConvertIsoToXiso/Services/XisoServices/XDVDFS/XDVDFS.cs)                                                           | Core XDVDFS filesystem implementation       |
| [`XDVDFS/VolumeDescriptor.cs`](BatchConvertIsoToXiso/Services/XisoServices/XDVDFS/VolumeDescriptor.cs)                                       | XDVDFS volume descriptor parsing            |

### Clean Code Structure
The `MainWindow` is organized into logical partial classes for maintainability:
- [`MainWindow.ConversionAndTesting.cs`](BatchConvertIsoToXiso/MainWindow.ConversionAndTesting.cs) - Conversion and testing operations
- [`MainWindow.UIHelpersAndWindowEvents.cs`](BatchConvertIsoToXiso/MainWindow.UIHelpersAndWindowEvents.cs) - UI management and event handling
- [`MainWindow.XIsoExplorerLogic.cs`](BatchConvertIsoToXiso/MainWindow.XIsoExplorerLogic.cs) - XISO browser functionality
- [`MainWindow.CheckForUpdatesAsync.cs`](BatchConvertIsoToXiso/MainWindow.CheckForUpdatesAsync.cs) - Update checking
- [`MainWindow.ReportBugAsync.cs`](BatchConvertIsoToXiso/MainWindow.ReportBugAsync.cs) - Bug reporting

---

## System Requirements

| Component    | Minimum Requirement                 |
|:-------------|:------------------------------------|
| OS           | Windows 10 (1809) or Windows 11     |
| .NET Runtime | .NET 10.0 Desktop Runtime           |
| Processor    | x64 architecture                    |
| RAM          | 4 GB recommended                    |
| Storage      | Varies based on ISO collection size |

---

## Safety & Reliability

- **Atomic Operations**: Converted files are verified before originals are deleted
- **Automatic Cleanup**: [`TempFolderCleanupHelper`](BatchConvertIsoToXiso/Services/TempFolderCleanupHelper.cs) removes orphaned temporary files on startup or after crashes
- **Robust Error Handling**: Comprehensive exception handling with automatic bug reporting
- **Retry Logic**: Automatic retries for file operations with cloud-synced files
- **Process Isolation**: External tools run in isolated processes with cancellation support

---

## Screenshots

![Convert Tab](screenshot.png)
*Batch conversion interface with real-time progress monitoring*

![Test Tab](screenshot2.png)
*ISO integrity testing with batch organization*

![Explorer Tab](screenshot3.png)
*XISO file browser without extraction*

---

## Acknowledgements

- **[XboxKit](https://github.com/Deterous/XboxKit)** - Reference for core XDVDFS logic
- **[extract-xiso](https://github.com/XboxDev/extract-xiso)** - External XISO conversion tool
- **bchunk** - CUE/BIN to ISO conversion
- **[SharpCompress](https://github.com/adamhathcock/sharpcompress)** - High-performance archive extraction
- **Pure Logic Code** - Development and maintenance

---

## License

This project is licensed under the GNU General Public License v3.0 – see the [LICENSE.txt](LICENSE.txt) file for details.

---

<p>
  ⭐ <strong>If you find this tool useful, please give us a Star on GitHub!</strong> ⭐
</p>

<p>
  <a href="https://www.purelogiccode.com">Pure Logic Code</a>
</p>
