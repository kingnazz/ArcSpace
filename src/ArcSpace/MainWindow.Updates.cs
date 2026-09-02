using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using ArcSpace.Services;

namespace ArcSpace;

public partial class MainWindow
{
    private readonly AppUpdateSettings _updateSettings = AppUpdateSettings.Load();
    private readonly GitHubUpdateService _updateService = new();
    private Button? _updatesButton;
    private Button? _chooseFolderButton;

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        InstallUpdateControls();
        InstallTechnicianShortcuts();
        InstallLiveScanRate();
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
        StopButton.ToolTip = "Cancel the current scan (Esc)";

        if (Content is not DependencyObject root)
        {
            return;
        }

        _chooseFolderButton = FindVisualChildren<Button>(root)
            .FirstOrDefault(button => string.Equals(button.Content as string, "Choose folder", StringComparison.Ordinal));

        if (_chooseFolderButton is not null)
        {
            _chooseFolderButton.ToolTip = "Choose folder (Ctrl+O)";
            _chooseFolderButton.SetBinding(
                IsEnabledProperty,
                new Binding(nameof(IsEnabled)) { Source = ScanButton, Mode = BindingMode.OneWay });
        }
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
            _scanCancellation?.Cancel();
            StatusText.Text = "Stopping scan…";
            e.Handled = true;
        }
    }

    private void InstallLiveScanRate()
    {
        _statusTimer.Tick += (_, _) =>
        {
            if (!_scanStopwatch.IsRunning || _scanStopwatch.Elapsed.TotalSeconds < 0.5)
            {
                return;
            }

            var filesPerSecond = _latestFilesScanned / _scanStopwatch.Elapsed.TotalSeconds;
            StatusDetailsText.Text += $"  ·  {filesPerSecond:N0} files/s";
        };
    }

    private void SyncVisibleVersionLabel()
    {
        if (Content is not DependencyObject root)
        {
            return;
        }

        var version = typeof(MainWindow).Assembly.GetName().Version;
        if (version is null)
        {
            return;
        }

        var versionLabel = FindVisualChildren<TextBlock>(root)
            .FirstOrDefault(text => text.Text.TrimStart().StartsWith("ArcSpace v", StringComparison.OrdinalIgnoreCase));

        if (versionLabel is not null)
        {
            versionLabel.Text = $"   ArcSpace v{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in FindVisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
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
