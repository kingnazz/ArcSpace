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
        if (Content is Grid root && _updatesButton is null)
        {
            _updatesButton = new Button
            {
                Content = "Updates",
                ToolTip = "Update settings (Ctrl+U)",
                Height = 26,
                MinWidth = 68,
                Padding = new Thickness(9, 2, 9, 2),
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 285, 0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = (Brush)FindResource("TextSecondaryBrush"),
                Cursor = Cursors.Hand
            };
            _updatesButton.Click += (_, _) => OpenUpdateSettings();
            Grid.SetRow(_updatesButton, 2);
            Panel.SetZIndex(_updatesButton, 20);
            root.Children.Add(_updatesButton);
        }
    }

    private void InstallTechnicianShortcuts()
    {
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        ScanButton.ToolTip = "Scan or rescan (F5)";
        StopButton.ToolTip = "Cancel the current scan and keep partial results (Esc)";
        ChooseFolderButton.ToolTip = "Choose folder (Ctrl+O)";
        FolderTree.ToolTip = "Enter: open folder · Ctrl+C: copy path";
        LargestFilesGrid.ToolTip = "Enter: show in Explorer · Ctrl+C: copy path";
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
                _updatesButton.Foreground = (Brush)FindResource("AccentBrush");
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
