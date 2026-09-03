using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ArcSpace.Services;

namespace ArcSpace;

public partial class MainWindow
{
    private readonly AppUpdateSettings _updateSettings = AppUpdateSettings.Load();
    private readonly GitHubUpdateService _updateService = new();
    private Button? _updatesButton;

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        InstallUpdateControls();
        InstallTechnicianShortcuts();
        SyncVisibleVersionLabel();

        if (_updateSettings.CheckForUpdatesOnLaunch)
        {
            _ = CheckForUpdatesOnLaunchAsync();
        }
    }

    private void InstallUpdateControls()
    {
        if (_updatesButton is not null)
        {
            return;
        }

        _updatesButton = UpdatesButton;
        _updatesButton.Click += (_, _) => OpenUpdateSettings();
    }

    private void InstallTechnicianShortcuts()
    {
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        ScanButton.ToolTip = "Scan or rescan (F5)";
        StopButton.ToolTip = "Cancel the current scan and keep partial results (Esc)";
        ChooseFolderButton.ToolTip = "Choose folder (Ctrl+O)";
        FolderTree.ToolTip = "Enter: open folder · Ctrl+C: copy path";
        LargestFilesGrid.ToolTip = "Enter: show in Explorer · Ctrl+C: copy path";
        SpaceMap.ToolTip = "Double-click a tile to open that folder in Explorer";
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.U && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            OpenUpdateSettings();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.O && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && ScanButton.IsEnabled)
        {
            ChooseFolder_Click(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F5 && ScanButton.IsEnabled)
        {
            _ = StartScanAsync();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && StopButton.IsEnabled)
        {
            RequestScanCancellation();
            e.Handled = true;
        }
    }

    private void SyncVisibleVersionLabel()
    {
        var version = typeof(MainWindow).Assembly.GetName().Version;
        if (version is null)
        {
            return;
        }

        VersionText.Text = $"   ArcSpace v{version.Major}.{version.Minor}.{version.Build}";
    }

    private void OpenUpdateSettings()
    {
        var dialog = new UpdateSettingsWindow(_updateSettings, _updateService)
        {
            Owner = this
        };
        dialog.ShowDialog();
    }

    private async Task CheckForUpdatesOnLaunchAsync()
    {
        try
        {
            var update = await _updateService.CheckForUpdateAsync();
            if (update is null)
            {
                return;
            }

            Title = $"ArcSpace  •  {update.TagName} available";
            if (_updatesButton is not null)
            {
                _updatesButton.Content = $"Update {update.TagName}";
                _updatesButton.Foreground = (Brush)FindResource("AccentHoverBrush");
            }

            if (!_updateSettings.AutoInstallUpdates)
            {
                return;
            }

            StatusText.Text = $"Downloading ArcSpace {update.TagName} update…";
            await _updateService.DownloadAndStageUpdateAsync(update);
            StatusText.Text = $"ArcSpace {update.TagName} ready  ·  close ArcSpace to install";

            if (_updatesButton is not null)
            {
                _updatesButton.Content = "Update ready";
            }
        }
        catch
        {
            // Update checks must never interfere with disk analysis or technician workflows.
        }
    }
}
