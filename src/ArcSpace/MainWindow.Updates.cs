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

        if (_updateSettings.CheckForUpdatesOnLaunch)
        {
            _ = CheckForUpdatesOnLaunchAsync();
        }
    }

    private void InstallUpdateControls()
    {
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.U && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                OpenUpdateSettings();
                e.Handled = true;
            }
        };

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
