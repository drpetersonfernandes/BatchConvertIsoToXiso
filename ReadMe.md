# Batch ISO to XISO Converter

[![.NET 10.0](https://img.shields.io/badge/.NET-10.0-blue.svg)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![Platform](https://img.shields.io/badge/Platform-Windows-lightgrey.svg)](https://www.microsoft.com/windows)
[![License](https://img.shields.io/badge/License-GPLv3-blue.svg)](LICENSE)

**Batch ISO to XISO** is a high-performance Windows WPF utility designed for the Xbox preservation and emulation community. It provides a streamlined way to convert standard Xbox and Xbox 360 ISOs into the optimized, trimmed **XISO** format, verify their structural integrity, and explore their contents.

Built with a **native C# XDVDFS engine**, this tool eliminates the need for legacy external command-line tools for core ISO operations, offering superior speed and modern features like real-time disk write monitoring.

![Main Screenshot](screenshot.png)

---

## 🚀 Key Features

### 1. Batch Conversion
*   **Smart Trimming**: Rebuilds ISOs into the XISO format, removing gigabytes of unnecessary system padding.
*   **Archive Support**: Directly process `.zip`, `.7z`, and `.rar` files. The tool extracts, converts, and cleans up automatically.
*   **CUE/BIN Support**: Integrated `bchunk` support to convert old-school disc images to ISO before processing.
*   **System Update Removal**: Option to skip the `$SystemUpdate` folder to save additional space.

### 2. Integrity Testing
*   **Structural Validation**: Traverses the XDVDFS file tree to ensure the filesystem is valid and readable.
*   **Deep Surface Scan**: Optional sequential sector reading to detect physical data corruption or "bad sectors" in the image.
*   **Batch Organization**: Automatically move "Passed" or "Failed" images into dedicated subfolders.

### 3. XISO Explorer
*   **Native Browsing**: Open any Xbox ISO to browse files and directories without extracting them.
*   **Metadata View**: View file sizes, attributes, and directory structures directly within the UI.

### 4. Advanced Monitoring
*   **Real-time Stats**: Track success/fail counts, elapsed time, and processed file lists.
*   **Disk Monitor**: Live monitoring of write speeds and drive activity to identify hardware bottlenecks.

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

## 📖 How to Use

### Conversion
1.  Select your **Source Folder** (contains your ISOs or archives).
2.  Select an **Output Folder** (where the trimmed XISOs will be saved).
3.  *(Optional)* Enable **Delete original files** if you wish to replace your library with XISOs (use with caution!).
4.  Click **Start Conversion**.

### Integrity Testing
1.  Switch to the **Test Integrity** tab.
2.  Select the folder containing your ISOs.
3.  Choose whether to perform a **Deep Surface Scan** (thorough but slower).
4.  Click **Start Integrity Test**.

### Explorer
1.  Switch to the **Explorer** tab.
2.  Browse for a specific `.iso` file.
3.  Double-click folders to navigate the internal Xbox filesystem.

---

## 🛡️ Safety & Reliability

*   **Temp Folder Protection**: To prevent data loss, the app restricts users from selecting system temporary directories as source or destination folders.
*   **Cloud-Aware**: Detects if files are stored in the cloud (e.g., OneDrive) and prompts for hydration/download instead of crashing.
*   **Atomic Operations**: Converted files are verified before the original is deleted (if that option is enabled).

---

## 📜 Acknowledgements

*   **bchunk**: Used for CUE/BIN to ISO conversion.
*   **SevenZipSharp**: Used for high-performance archive extraction.
*   **Pure Logic Code**: Developed and maintained by [Pure Logic Code](https://www.purelogiccode.com).

---

⭐ **If you find this tool useful, please give us a Star on GitHub!** ⭐