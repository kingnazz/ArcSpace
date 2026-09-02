using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ArcSpace.Services;

namespace ArcSpace;

public sealed class UpdateSettingsWindow : Window
{
    private readonly AppUpdateSettings _settings;
    private readonly GitHubUpdateService _updateService;
    private readonly CheckBox _checkOnLaunch;
    private readonly CheckBox _autoInstall;
    private readonly TextBlock _status;
    private readonly Button _checkButton;

    public UpdateSettingsWindow(AppUpdateSettings settings, GitHubUpdateService updateService)
    {
        _settings = settings;
        _updateService = updateService;

        Title = "ArcSpace Updates";
        Width = 470;
        Height = 330;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)Application.Current.FindResource("AppBackgroundBrush");
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");

        var root = new Grid { Margin = new Thickness(24) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var title = new TextBlock
        {
            Text = "Keep ArcSpace current",
            FontSize = 21,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.FindResource("TextPrimaryBrush")
        };
        root.Children.Add(title);

        var subtitle = new TextBlock
        {
            Text = $"Installed version {GitHubUpdateService.CurrentVersion.Major}.{GitHubUpdateService.CurrentVersion.Minor}.{GitHubUpdateService.CurrentVersion.Build}",
            Margin = new Thickness(0, 5, 0, 18),
            Foreground = (Brush)Application.Current.FindResource("TextSecondaryBrush")
        };
        Grid.SetRow(subtitle, 1);
        root.Children.Add(subtitle);

        var options = new Border
        {
            Background = Brushes.White,
            BorderBrush = (Brush)Application.Current.FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16)
        };
        var optionStack = new StackPanel();
        _checkOnLaunch = new CheckBox
        {
            Content = "Check for updates when ArcSpace starts",
            IsChecked = _settings.CheckForUpdatesOnLaunch,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 13)
        };
        _autoInstall = new CheckBox
        {
            Content = "Automatically download and install updates",
            IsChecked = _settings.AutoInstallUpdates,
            FontWeight = FontWeights.SemiBold
        };
        optionStack.Children.Add(_checkOnLaunch);
        optionStack.Children.Add(_autoInstall);
        options.Child = optionStack;
        Grid.SetRow(options, 2);
        root.Children.Add(options);

        _status = new TextBlock
        {
            Text = "Updates are delivered through official ArcSpace GitHub Releases.",
            Margin = new Thickness(2, 15, 2, 0),
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.FindResource("TextSecondaryBrush")
        };
        Grid.SetRow(_status, 3);
        root.Children.Add(_status);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0)
        };
        _checkButton = new Button
        {
            Content = "Check now",
            Style = (Style)Application.Current.FindResource("BaseButtonStyle"),
            Margin = new Thickness(0, 0, 8, 0)
        };
        _checkButton.Click += CheckNow_Click;

        var saveButton = new Button
        {
            Content = "Save",
            Style = (Style)Application.Current.FindResource("PrimaryButtonStyle"),
            MinWidth = 82
        };
        saveButton.Click += (_, _) =>
        {
            _settings.CheckForUpdatesOnLaunch = _checkOnLaunch.IsChecked == true;
            _settings.AutoInstallUpdates = _autoInstall.IsChecked == true;
            _settings.Save();
            DialogResult = true;
        };

        actions.Children.Add(_checkButton);
        actions.Children.Add(saveButton);
        Grid.SetRow(actions, 4);
        root.Children.Add(actions);

        Content = root;
    }

    private async void CheckNow_Click(object sender, RoutedEventArgs e)
    {
        _checkButton.IsEnabled = false;
        _status.Text = "Checking GitHub Releases…";

        try
        {
            var update = await _updateService.CheckForUpdateAsync();
            if (update is null)
            {
                _status.Text = "ArcSpace is up to date.";
                return;
            }

            _status.Text = $"ArcSpace {update.TagName} is available.";
            var choice = MessageBox.Show(
                this,
                $"ArcSpace {update.TagName} is available.\n\nInstall it now? The update will be applied when ArcSpace closes.",
                "ArcSpace update available",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information,
                MessageBoxResult.Yes);

            if (choice == MessageBoxResult.Yes)
            {
                _status.Text = "Downloading update…";
                await _updateService.DownloadAndStageUpdateAsync(update);
                _status.Text = $"{update.TagName} is ready. Close ArcSpace to finish the update.";
            }
        }
        catch (Exception ex)
        {
            _status.Text = "Could not check for updates.";
            MessageBox.Show(this, ex.Message, "ArcSpace update error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _checkButton.IsEnabled = true;
        }
    }
}
