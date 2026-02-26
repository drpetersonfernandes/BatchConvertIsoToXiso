using System.Windows;
using BatchConvertIsoToXiso.Models;
using BatchConvertIsoToXiso.Services;
using BatchConvertIsoToXiso.Services.XisoServices.BinaryOperations;
using BatchConvertIsoToXiso.Services.XisoServices.XDVDFS;
using Microsoft.Win32;

namespace BatchConvertIsoToXiso;

public partial class MainWindow
{
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
            }
            else
            {
                // Track this directory in the path for display purposes
                _explorerPathNames.Push(folderName);
            }

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

    private void ExplorerListView_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ExplorerListView.SelectedItem is XisoExplorerItem { IsDirectory: true } item)
        {
            // Save current directory entry to stack before navigating deeper
            // The current items are displayed via ExplorerListView.ItemsSource, but we need
            // to save the parent directory entry for the "Up" navigation
            if (ExplorerListView.ItemsSource is IEnumerable<XisoExplorerItem> currentItems)
            {
                var currentDirEntry = currentItems.FirstOrDefault(static i => i is { IsDirectory: true, Name: ".." })?.Entry;
                if (currentDirEntry != null)
                {
                    _parentDirectoryStack.Push(currentDirEntry);
                }
            }

            LoadDirectory(item.Entry, item.Name);
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
