using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using BatchConvertIsoToXiso.Models;
using BatchConvertIsoToXiso.Services;
using BatchConvertIsoToXiso.Services.XisoServices.BinaryOperations;
using BatchConvertIsoToXiso.Services.XisoServices.XDVDFS;
using Microsoft.Win32;

namespace BatchConvertIsoToXiso;

public partial class MainWindow
{
    // Drag-drop state tracking
    private Point _dragStartPoint;
    private bool _isDragging;

    private void BrowseExplorerFile_Click(object sender, RoutedEventArgs e)
    {
        var openFileDialog = new OpenFileDialog
        {
            Filter = "Xbox ISO files (*.iso)|*.iso|All files (*.*)|*.*",
            Title = "Select an Xbox ISO to explore"
        };

        if (openFileDialog.ShowDialog() != true) return;

        ExplorerFilePathTextBox.Text = openFileDialog.FileName;
        InitializeExplorer(openFileDialog.FileName);
    }

    private void InitializeExplorer(string isoPath)
    {
        try
        {
            _explorerIsoSt?.Dispose();
            _parentDirectoryStack.Clear();
            _explorerPathNames.Clear();
            _currentDirectoryEntry = null;

            _explorerIsoSt = new IsoSt(isoPath);
            var volume = VolumeDescriptor.ReadFrom(_explorerIsoSt);
            var root = FileEntry.CreateRootEntry(volume.RootDirTableSector);

            LoadDirectory(root, "Root", true);
        }
        catch (Exception ex)
        {
            _messageBoxService.ShowError($"Failed to read XISO: {ex.Message}");
        }
    }

    private void LoadDirectory(FileEntry dirEntry, string folderName, bool isRoot = false)
    {
        if (_explorerIsoSt == null) return;

        try
        {
            var entries = _nativeIsoTester.GetDirectoryEntries(_explorerIsoSt, dirEntry);
            var uiItems = entries.Select(static e => new XisoExplorerItem
            {
                Name = e.FileName,
                IsDirectory = e.IsDirectory,
                SizeFormatted = e.IsDirectory ? "" : Formatter.FormatBytes(e.FileSize),
                Entry = e
            }).OrderByDescending(static i => i.IsDirectory).ThenBy(static i => i.Name).ToList();

            ExplorerListView.ItemsSource = uiItems;

            if (isRoot)
            {
                _parentDirectoryStack.Clear();
                _explorerPathNames.Clear();
                _currentDirectoryEntry = null;
            }
            else
            {
                // Track this directory in the path for display purposes
                _explorerPathNames.Push(folderName);
            }

            // Track the current directory entry for "Up" navigation
            _currentDirectoryEntry = dirEntry;

            UpdateExplorerUiState();
        }
        catch (Exception ex)
        {
            _messageBoxService.ShowError($"Error loading directory: {ex.Message}");
        }
    }

    private void UpdateExplorerUiState()
    {
        ExplorerUpButton.IsEnabled = _parentDirectoryStack.Count > 0;
        var path = "/" + string.Join("/", _explorerPathNames.Reverse());
        ExplorerPathTextBlock.Text = path;
    }

    private void ExplorerListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ExplorerListView.SelectedItem is not XisoExplorerItem item) return;

        if (item.IsDirectory)
        {
            // Save current directory entry to stack before navigating deeper
            // XDVDFS filesystem doesn't store . or .. entries, so we track the current
            // directory at the class level and push it to the stack before navigating
            if (_currentDirectoryEntry != null)
            {
                _parentDirectoryStack.Push(_currentDirectoryEntry);
            }

            LoadDirectory(item.Entry, item.Name);
        }
        else
        {
            // Open the file with the default application
            OpenFileFromIso(item.Entry, item.Name);
        }
    }

    private void OpenFileFromIso(FileEntry entry, string fileName)
    {
        if (_explorerIsoSt == null) return;

        Task.Run(async () =>
        {
            try
            {
                var tempFolder = Path.Combine(Path.GetTempPath(), "XisoExplorer", Guid.NewGuid().ToString());
                Directory.CreateDirectory(tempFolder);
                var tempPath = Path.Combine(tempFolder, fileName);

                // Extract file to temp location
                await ExtractFileToDiskAsync(entry, tempPath);

                // Open with default application on UI thread
                await Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo(tempPath) { UseShellExecute = true });
                    }
                    catch (Exception ex)
                    {
                        _messageBoxService.ShowError($"Failed to open file: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    _messageBoxService.ShowError($"Failed to extract and open file: {ex.Message}");
                });
            }
        });
    }

    private Task ExtractFileToDiskAsync(FileEntry entry, string outputPath)
    {
        if (_explorerIsoSt == null) throw new InvalidOperationException("ISO stream not available");

        return Task.Run(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? throw new InvalidOperationException("outputPath cannot be null"));

            const int bufferSize = 4 * 1024 * 1024; // 4MB buffer
            var buffer = new byte[bufferSize];

            using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);

            long bytesRemaining = entry.FileSize;
            long currentOffset = 0;

            while (bytesRemaining > 0)
            {
                var toRead = (int)Math.Min(bufferSize, bytesRemaining);
                var read = _explorerIsoSt.Read(entry, buffer.AsSpan(0, toRead), currentOffset);

                if (read == 0)
                {
                    throw new IOException($"Unexpected end of file while extracting: {entry.FileName}");
                }

                fileStream.Write(buffer, 0, read);
                bytesRemaining -= read;
                currentOffset += read;
            }
        });
    }

    private void ExplorerListView_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(null);
    }

    private void ExplorerListView_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _isDragging) return;
        if (_explorerIsoSt == null) return;

        var currentPosition = e.GetPosition(null);
        var diff = _dragStartPoint - currentPosition;

        // Check if mouse has moved enough to start a drag operation
        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        // Get selected file items (not directories)
        var selectedItems = ExplorerListView.SelectedItems
            .Cast<XisoExplorerItem>()
            .Where(static i => !i.IsDirectory)
            .ToList();

        if (selectedItems.Count == 0) return;

        _isDragging = true;

        try
        {
            // Extract files to temp folder for drag operation
            var tempFolder = Path.Combine(Path.GetTempPath(), "XisoExplorer", "DragDrop", Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempFolder);

            var tempFiles = new List<string>();

            foreach (var item in selectedItems)
            {
                var tempPath = Path.Combine(tempFolder, item.Name);
                ExtractFileToDisk(item.Entry, tempPath);
                tempFiles.Add(tempPath);
            }

            // Start drag operation
            var data = new DataObject(DataFormats.FileDrop, tempFiles.ToArray());
            DragDrop.DoDragDrop(ExplorerListView, data, DragDropEffects.Copy);

            // Cleanup temp files after drag operation completes
            try
            {
                Directory.Delete(tempFolder, true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
        catch (Exception ex)
        {
            _messageBoxService.ShowError($"Failed to prepare files for drag operation: {ex.Message}");
        }
        finally
        {
            _isDragging = false;
        }
    }

    private void ExtractFileToDisk(FileEntry entry, string outputPath)
    {
        if (_explorerIsoSt == null) throw new InvalidOperationException("ISO stream not available");

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? throw new InvalidOperationException("outputPath cannot be null"));

        const int bufferSize = 4 * 1024 * 1024; // 4MB buffer
        var buffer = new byte[bufferSize];

        using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);

        long bytesRemaining = entry.FileSize;
        long currentOffset = 0;

        while (bytesRemaining > 0)
        {
            var toRead = (int)Math.Min(bufferSize, bytesRemaining);
            var read = _explorerIsoSt.Read(entry, buffer.AsSpan(0, toRead), currentOffset);

            if (read == 0)
            {
                throw new IOException($"Unexpected end of file while extracting: {entry.FileName}");
            }

            fileStream.Write(buffer, 0, read);
            bytesRemaining -= read;
            currentOffset += read;
        }
    }

    private void ExplorerUpButton_Click(object sender, RoutedEventArgs e)
    {
        if (_explorerIsoSt == null) return;
        if (_parentDirectoryStack.Count == 0) return;

        // Pop the parent directory from the stack and navigate to it
        var parentEntry = _parentDirectoryStack.Pop();
        var parentName = _explorerPathNames.Pop();

        LoadDirectory(parentEntry, parentName);
    }
}
