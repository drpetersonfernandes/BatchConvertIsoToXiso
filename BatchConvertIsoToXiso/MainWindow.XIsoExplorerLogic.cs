using System.Windows;
using BatchConvertIsoToXiso.Models;
using BatchConvertIsoToXiso.Services;
using BatchConvertIsoToXiso.Services.XisoServices;
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
            _explorerHistory.Clear();
            _explorerPathNames.Clear();

            _explorerIsoSt = new IsoSt(isoPath);
            var volume = VolumeDescriptor.ReadFrom(_explorerIsoSt);
            var root = FileEntry.CreateRootEntry(volume.RootDirTableSector);

            LoadDirectory(root, "Root");
        }
        catch (Exception ex)
        {
            _messageBoxService.ShowError($"Failed to read XISO: {ex.Message}");
        }
    }

    private void LoadDirectory(FileEntry dirEntry, string folderName)
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

            if (folderName != "Root")
            {
                _explorerHistory.Push(dirEntry);
                _explorerPathNames.Push(folderName);
            }
            else
            {
                _explorerHistory.Clear();
                _explorerPathNames.Clear();
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
        ExplorerUpButton.IsEnabled = _explorerHistory.Count > 0;
        var path = "/" + string.Join("/", _explorerPathNames.Reverse());
        ExplorerPathTextBlock.Text = path;
    }

    private void ExplorerListView_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ExplorerListView.SelectedItem is XisoExplorerItem { IsDirectory: true } item)
        {
            LoadDirectory(item.Entry, item.Name);
        }
    }

    private void ExplorerUpButton_Click(object sender, RoutedEventArgs e)
    {
        if (_explorerIsoSt == null) return;

        if (_explorerHistory.Count <= 1)
        {
            var volume = VolumeDescriptor.ReadFrom(_explorerIsoSt);
            LoadDirectory(FileEntry.CreateRootEntry(volume.RootDirTableSector), "Root");
        }
        else
        {
            _explorerHistory.Pop();
            _explorerPathNames.Pop();

            var parentEntry = _explorerHistory.Pop();
            var parentName = _explorerPathNames.Pop();

            LoadDirectory(parentEntry, parentName);
        }
    }
}