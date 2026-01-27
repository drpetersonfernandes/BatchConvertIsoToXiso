# Batch Convert ISO to XISO

A high-performance Windows WPF application for batch converting Xbox ISO files to the trimmed XISO format and testing their integrity. This application supports both **Xbox 360 ISOs** and **original Xbox ISOs**. It features a native C# XISO engine for fast, reliable processing without external dependencies for core ISO operations.

⭐ **If you find this tool useful, please give us a Star on GitHub!** ⭐

![Batch Convert ISO to XISO](screenshot.png)

## Overview

Batch Convert ISO to XISO is a Windows application providing a user-friendly interface for:
1.  Converting multiple Xbox ISO files to the XISO format.
2.  Testing the integrity of Xbox ISO files.

It uses a native C# implementation for XISO rewriting and structural verification. For conversions, it uses the SevenZipExtractor library to handle ISOs packaged within common archive formats (ZIP, 7Z, RAR), and `bchunk.exe` for CUE/BIN disc images, providing a streamlined batch processing experience.
The application features real-time progress tracking, detailed summary statistics, and disk write speed monitoring.

## Features

*   **Batch ISO to XISO Conversion**: Convert multiple ISO files to XISO format in a single operation.
*   **Batch ISO Integrity Testing**: Test the integrity of multiple ISO files.
*   **Xbox Compatibility**: Supports both **Xbox 360 ISOs** and **original Xbox ISOs**.
*   **ISO/XISO Support**:
    *   Converts standard Xbox ISO images to the trimmed XISO format.
    *   Tests ISO integrity by verifying the XDVDFS file system structure and data readability.
    *   Can also process existing XISO files to trim unnecessary padding.
*   **Archive Support (for Conversion only)**: Automatically extracts ISO files from `.zip`, `.7z`, and `.rar` archives found in the input folder for conversion using the SevenZipExtractor library.
*   **CUE/BIN Support (for Conversion only)**: Automatically converts `.cue`/`.bin` disc images to `.iso` format using the bundled `bchunk.exe` before processing them into XISOs.
*   **Skip System Update Folder**: Option to skip the `$SystemUpdate` folder during conversion, which can reduce output file size and is often desired for game ISOs.
*   **Progress Tracking & Summary**:
    *   Real-time log messages detailing the status of each file.
    *   Overall progress bar.
    *   Summary statistics: Total Files, Success, Failed, Skipped, Processing Time.
    *   Real-time disk write speed monitoring.
*   **File Management Options**:
    *   **Conversion**: Option to **delete original files after successful conversion** (applies to standalone ISOs or archives if all contained ISOs processed successfully). **Use with caution!**
    *   **Testing**: Options to move successfully tested ISOs to a `_success` subfolder and/or move failed ISOs to a `_failed` subfolder, both created within the source input folder.
*   **Temporary Folder Restriction**: Prevents selection of system temporary folders or their subfolders as input or output directories to avoid conflicts with internal temporary operations.
*   **Global Error Reporting**: Includes a feature to automatically send silent bug reports to the developer with comprehensive error details to aid in improving the application.

## Supported File Formats

*   **Input for Conversion**:
    *   `.iso` files: Standard Xbox and Xbox 360 ISO images.
    *   `.zip`, `.7z`, `.rar` archives: Archives containing `.iso` files (extraction is recursive).
    *   `.cue` / `.bin` files: Common disc image format.
*   **Input for Testing**:
    *   `.iso` files: Standard Xbox and Xbox 360 ISO images (direct files only, not from archives).

## Requirements

*   Windows 7 or later
*   [.NET 10.0 Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (or newer)
*   **Bundled Tools**: This application includes `bchunk.exe` (for CUE/BIN conversion) and `7z_x64.dll` (for archive extraction) in its release package. No separate downloads for these tools are required.

## Installation

1.  Download the latest release from the [Releases page](https://github.com/drpetersonfernandes/BatchConvertIsoToXiso/releases).
2.  Extract the contents of the downloaded zip file to a folder of your choice.
3.  Run `BatchConvertIsoToXiso.exe`.

## Usage

1.  **Launch the Application**.
2.  **Select Input Folder**: Click the "Browse" button next to "Source Files Folder" (for conversion) or "ISO Files Folder" (for testing). Choose the folder containing your ISO files. For conversion, this folder can also contain archives (ZIP, 7Z, RAR) with ISOs or CUE/BIN files. For testing, only direct `.iso` files in this folder will be processed.
3.  **Select Output Folder (for Conversion)**: Click the "Browse" button next to "Output XISO Folder". This is where converted XISO files will be saved. (Note: For the "Test ISO Integrity" tab, successfully or failed tested ISOs are moved to subfolders *within the source input folder*, not to this output folder.)
4.  **Configure Options**:
    *   **For Conversion**:
        *   Check "Delete original files after successful conversion" if you want standalone ISOs or archive files (if all contained ISOs processed successfully) to be deleted. **Use with caution!**
        *   Check `Skip $SystemUpdate folder during conversion` if you want to omit this folder from the XISO output. This is often useful for game ISOs to save space and avoid unnecessary data.
        *   Check `Search for files in subfolders` to include files in subdirectories of your input folder.
    *   **For Testing**:
        *   Check/uncheck "Move successfully tested ISOs to a `_success` subfolder within the source folder".
        *   Check/uncheck "Move failed tested ISOs to a `_failed` subfolder within the source folder".
        *   Check `Search for files in subfolders` to include files in subdirectories of your input folder.
5.  **Choose Action**:
    *   Click **"Start Conversion"** to convert ISOs to XISO format.
    *   Click **"Start Test"** to test the integrity of ISO files.
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
*   Converting standard ISOs (often created from disc dumps) to XISO ensures they are in the correct format for use with modded Xbox consoles, emulators like Xemu, or other Xbox development tools. This process rebuilds the ISO to the correct XISO structure, removing unnecessary padding and trimming the file layout.
*   Testing ISOs involves traversing the XDVDFS file system and verifying that every file entry is readable and the directory tree is valid.

## Why Use XISO / Test ISOs?

*   **Compatibility**: XISO is the standard format expected by modded Xbox consoles and emulators, ensuring games load correctly on both original Xbox and Xbox 360 systems.
*   **Trimming (Conversion)**: The conversion process trims the file structure and removes padding, potentially reducing file size. Skipping the `$SystemUpdate` folder further helps in reducing size.
*   **Data Integrity (Conversion & Testing)**: The tool verifies the structure of the ISO and rebuilds it correctly (conversion) or checks for readability and potential corruption (testing).

## Troubleshooting

*   **`bchunk.exe` Missing**: Ensure `bchunk.exe` is present in the same directory as the application. It is bundled with the release; if missing, re-extract the application. `bchunk.exe` is required for converting CUE/BIN files.
*   **`7z_x64.dll` Missing**: If you encounter errors related to archive extraction (ZIP, 7Z, RAR) and the log mentions `7z_x64.dll` not being found, ensure that `7z_x64.dll` is present in the same directory as the application executable. This library is essential for archive processing.
*   **Temporary Folder Restriction**: The application prevents selecting the system's temporary folder or its subfolders as input or output directories. This is to avoid conflicts with the application's internal temporary file operations. Please choose a different location for your source and destination files.
*   **Archive Extraction Failed (e.g., "File is corrupted")**: If you encounter errors like "File is corrupted. Data error has occurred." when processing `.zip`, `.7z`, or `.rar` files, it indicates that the archive itself is damaged or incomplete. This application cannot repair corrupted archives. Please verify the integrity of your source archive files.
*   **"Not enough space on the disk"**: This error occurs when the drive where files are being processed (input, output, or temporary extraction folders) runs out of space. Ensure you have sufficient free disk space, especially when converting or testing many large files, as temporary files can consume significant space during the process.
*   **Invalid ISO**: If the tool reports that an ISO is "not a valid xbox iso image", the file may be corrupted, from a different console (like PlayStation), or not a true Xbox/Xbox 360 ISO. Review the application log for specific messages.
*   **Permissions**: Make sure you have appropriate read permissions for the input folder and write permissions for the output folder and temporary extraction folders.
*   **Review Logs**: Always review the application's log window for detailed error messages during any operation.
*   **Automatic Error Reports**: Automatic error reports will be sent to the developer if unexpected issues occur, helping to improve the application.

## Acknowledgements

*   Uses **bchunk** for CUE/BIN to ISO conversion. [Find more information or source here (on GitHub)](https://github.com/psx-tools/bchunk).
*   Uses **SevenZipSharp** for archive extraction. [Find more information or source here (on GitHub)](https://github.com/squid-box/SevenZipSharp).
*   Developed by [Pure Logic Code](https://www.purelogiccode.com).

---

⭐ **Don't forget to Star this repository if you find it useful!** ⭐

Thank you for using **Batch Convert ISO to XISO**! For more information and support, visit [purelogiccode.com](https://www.purelogiccode.com).