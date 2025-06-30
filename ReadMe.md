# Batch Convert ISO to XISO

A GUI application for **extract-xiso** that provides a simple Windows WPF interface for batch converting Xbox ISO files to the optimized XISO format and testing their integrity. This application supports both **Xbox 360 ISOs** and **original Xbox ISOs**. It also supports extracting and converting ISO files contained within ZIP, 7Z, and RAR archives using the SevenZipExtractor library.

⭐ **If you find this tool useful, please give us a Star on GitHub!** ⭐

![Batch Convert ISO to XISO](screenshot.png)

## Overview

Batch Convert ISO to XISO is a Windows application that provides a user-friendly interface for:
1.  Converting multiple Xbox ISO files to the XISO format.
2.  Testing the integrity of Xbox ISO files.

It leverages the functionality of the `extract-xiso` command-line tool for both conversion and testing.
For conversions,
it uses the SevenZipExtractor library to handle ISOs packaged within common archive formats (ZIP, 7Z, RAR),
providing a streamlined batch processing experience.
The application features real-time progress tracking, detailed summary statistics, and disk write speed monitoring.

## Features

*   **Batch ISO to XISO Conversion**: Convert multiple ISO files to XISO format in a single operation.
*   **Batch ISO Integrity Testing**: Test the integrity of multiple ISO files.
*   **Xbox Compatibility**: Supports both **Xbox 360 ISOs** and **original Xbox ISOs**.
*   **ISO/XISO Support**:
    *   Converts standard Xbox ISO images to the optimized XISO format using `extract-xiso -r`.
    *   Tests ISO integrity by attempting a full extraction using `extract-xiso -x`.
    *   Can also process existing XISO files (which `extract-xiso -r` will typically skip if already optimized during conversion).
*   **Archive Support (for Conversion only)**: Automatically extracts ISO files from `.zip`, `.7z`, and `.rar` archives found in the input folder for conversion using the SevenZipExtractor library.
*   **Progress Tracking & Summary**:
    *   Real-time log messages detailing the status of each file.
    *   Overall progress bar.
    *   Summary statistics: Total Files, Success, Failed, Skipped, Processing Time.
    *   Real-time disk write speed monitoring.
*   **File Management Options**:
    *   **Conversion**: Option to delete source files (standalone ISOs or archives if all contents processed successfully) after successful conversion. **Use with caution!**
    *   **Testing**: Options to move successfully tested ISOs to the output folder and/or move failed ISOs to a "Failed" subfolder in the input folder.
*   **Global Error Reporting**: Includes a feature to automatically send silent bug reports to the developer with comprehensive error details to aid in improving the application.
*   **User-Friendly Interface**: Simple and intuitive Windows interface with Menu Bar and About window.

## Supported File Formats

*   **Input for Conversion**:
    *   `.iso` files: Standard Xbox and Xbox 360 ISO images.
    *   `.zip`, `.7z`, `.rar` archives: Archives containing `.iso` files (extraction is recursive).
*   **Input for Testing**:
    *   `.iso` files: Standard Xbox and Xbox 360 ISO images (direct files only, not from archives).

## Requirements

*   Windows 7 or later
*   [.NET 9.0 Runtime](https://dotnet.microsoft.com/download/dotnet/9.0) (or the .NET version the release was built with, check release notes)

## Installation

1.  Download the latest release from the [Releases page](https://github.com/drpetersonfernandes/BatchConvertIsoToXiso/releases).
2.  Extract the contents of the downloaded zip file to a folder of your choice.
3.  Run `BatchConvertIsoToXiso.exe`.

## Usage

1.  **Launch the Application**.
2.  **Select Input Folder**: Click the "Browse" button next to "Input Folder". Choose the folder containing your ISO files. For conversion, this folder can also contain archives (ZIP, 7Z, RAR) with ISOs. For testing, only direct `.iso` files in this folder will be processed.
3.  **Select Output Folder**: Click the "Browse" button next to "Output Folder".
    *   For **Conversion**: This is where converted XISO files will be saved.
    *   For **Testing**: This is where successfully tested ISOs will be moved if the "Move successfully tested ISO to Output Folder" option is checked.
4.  **Configure Options**:
    *   **For Conversion**:
        *   Check "Delete files after conversion (Conversion only)" if you want standalone ISOs or archive files (if all contained ISOs processed successfully) to be deleted. **Use with caution!**
    *   **For Testing**:
        *   Under "ISO Test Options:", check/uncheck:
            *   "Move successfully tested ISO to Output Folder".
            *   "Move failed tested ISO to 'Failed' subfolder in Input Folder" (checked by default).
5.  **Choose Action**:
    *   Click **"Start Conversion"** to convert ISOs to XISO format.
    *   Click **"Test ISOs"** to test the integrity of ISO files.
6.  **Monitor Progress**:
    *   The log viewer will show detailed progress and status for each file.
    *   The progress bar indicates overall progress.
    *   Summary statistics (Total, Success, Failed, Skipped, Time, Write Speed) will update in real-time.
7.  **Cancel**: Click the "Cancel" button to stop the current batch process. The application will attempt to finish processing the currently active file/archive before stopping.
8.  **Completion**: A summary message box will appear when the batch process is finished, detailing the results.
9.  **Menu**:
    *   **File > Exit**: Closes the application.
    *   **Help > About**: Opens the About window with version information and links.

## About XISO Format

XISO is the native disk image format used by Xbox consoles. It is essentially an ISO 9660 image with specific padding and structure optimized for Xbox hardware.
*   Converting standard ISOs (often created from disc dumps) to XISO using `extract-xiso -r` ensures they are in the correct format for use with modded Xbox consoles, emulators like Xemu, or other Xbox development tools. This command rebuilds the ISO to the correct XISO structure, often removing unnecessary padding and optimizing the file layout.
*   Testing ISOs using `extract-xiso -x` attempts to extract the full contents of the ISO. A successful extraction is a good indicator of the ISO's integrity.

## Why Use XISO / Test ISOs?

*   **Compatibility**: XISO is the standard format expected by modded Xbox consoles and emulators, ensuring games load correctly on both original Xbox and Xbox 360 systems.
*   **Optimization (Conversion)**: `extract-xiso -r` can optimize the file structure and remove padding, potentially reducing file size (though the primary benefit is format correctness).
*   **Data Integrity (Conversion & Testing)**: Using `extract-xiso` helps verify the structure of the ISO and rebuild it correctly (conversion) or check for readability and potential corruption (testing).

## Troubleshooting

*   Ensure `extract-xiso.exe` are present in the same directory as the application. `extract-xiso.exe` is crucial for both conversion and testing.
*   Make sure you have appropriate read permissions for the input folder and write permissions for the output folder and temporary extraction folders.
*   If ISO testing fails, the ISO might be corrupted or not a valid Xbox/Xbox 360 ISO image. Review the application log for detailed error messages from `extract-xiso`.
*   Review the application log window for detailed error messages during any operation.
*   Automatic error reports will be sent to the developer if unexpected issues occur.

## Acknowledgements

*   This application is a **GUI wrapper for extract-xiso**. We extend our heartfelt thanks and acknowledgment to the **XboxDev team** for creating and maintaining the powerful `extract-xiso` command-line tool that makes this application possible. Without their excellent work, this GUI interface would not exist.
*   Uses **extract-xiso** for the core ISO to XISO conversion and ISO integrity testing. [Find more information or source here (on GitHub)](https://github.com/XboxDev/extract-xiso).
*   Developed by [Pure Logic Code](https://www.purelogiccode.com).

---

⭐ **Don't forget to Star this repository if you find it useful!** ⭐

Thank you for using **Batch Convert ISO to XISO**! For more information and support, visit [purelogiccode.com](https://www.purelogiccode.com).