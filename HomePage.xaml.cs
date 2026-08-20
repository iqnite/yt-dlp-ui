using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Windows.BadgeNotifications;
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.AccessCache;
using Windows.Storage.Pickers;
using Windows.System;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace YT_DLP_UI;

public sealed partial class HomePage : Page, IDisposable
{
    private StorageFolder DownloadFolder = ApplicationData.Current.LocalFolder;
    private readonly string YTDLPPath = Path.Combine(AppContext.BaseDirectory, "dependencies", "yt-dlp", "yt-dlp.exe");
    private readonly string FFMPEGPath = Path.Combine(AppContext.BaseDirectory, "dependencies", "ffmpeg", "ffmpeg-master-latest-win32-gpl", "bin", "ffmpeg.exe");
    private readonly string DenoPath = Path.Combine(AppContext.BaseDirectory, "dependencies", "deno", "bin", "deno.exe");
    private bool IsBusy = false;
    private bool IsDownloadCancelled = false;
    private bool IsUpdatingSettings = false;
    private Process? DownloadProcess;
    private CancellationTokenSource? DownloadCancellationTokenSource;

    public string AppVersion
    {
        get
        {
            try
            {
                var version = Package.Current.Id.Version;
                return $"Version: {version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
            }
            catch
            {
                var version = Assembly.GetExecutingAssembly().GetName().Version;
                return $"Version: {(version is null ? "unknown" : $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}")}";
            }
        }
    }

    public HomePage()
    {
        InitializeComponent();
        DataContext = this;
        Loaded += HomePage_Loaded;
    }

    public async void HomePage_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadSettingsAsync();
        UpdateSettingsUI();
    }

    private async void UpdateSettingsUI()
    {
        if (IsUpdatingSettings) return;
        IsUpdatingSettings = true;
        try
        {
            ProfilesComboBox.Items.Clear();
            foreach (var profile in Settings.Profiles)
            {
                ProfilesComboBox.Items.Add(profile.Name);
            }
            UpdateRemoveButtonState();
            ProfilesComboBox.SelectedIndex = Settings.ActiveProfileId;
            AppSettingsProfile appSettingsProfile = Settings.GetActiveProfile();

            SelectLastUsedLinkOption.IsChecked = false;
            ClearSelectedLinkOption.IsChecked = false;
            KeepSelectedLinkOption.IsChecked = false;
            if (appSettingsProfile.LinkActionOnProfileChange == "SaveLink")
            {
                LinkTextBox.Text = appSettingsProfile.Link;
                SelectLastUsedLinkOption.IsChecked = true;
            }
            else if (appSettingsProfile.LinkActionOnProfileChange == "ClearLink")
            {
                LinkTextBox.Text = string.Empty;
                ClearSelectedLinkOption.IsChecked = true;
            }
            else
            {
                KeepSelectedLinkOption.IsChecked = true;
            }

            string downloadFolderPath = Settings.GetActiveProfile().DownloadFolderPath;
            if (!string.IsNullOrEmpty(downloadFolderPath) && Directory.Exists(downloadFolderPath))
            {
                DownloadFolder = await StorageFolder.GetFolderFromPathAsync(downloadFolderPath);
            }
            PickDestinationOutputTextBlock.Content = appSettingsProfile.DownloadFolderPath;
            FormatComboBox.SelectedItem = GetDisplayNameFromFormat(appSettingsProfile.Format) ?? FormatComboBox.Items[0];
            AdditionalArgumentsTextBox.Text = appSettingsProfile.AdditionalArguments;
            EmbedMetadataToggle.IsOn = appSettingsProfile.EmbedMetadata;
            SponsorblockToggle.IsOn = appSettingsProfile.Sponsorblock;
            UseSystemFFMPEGToggle.IsOn = appSettingsProfile.UseSystemFFMPEG;
            BadgeNotificationManager.Current.SetBadgeAsGlyph(BadgeNotificationGlyph.None);
        }
        finally
        {
            IsUpdatingSettings = false;
        }
    }

    private async void ProfilesComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsUpdatingSettings) return;
        if (ProfilesComboBox.SelectedIndex >= 0)
        {
            Settings.ActiveProfileId = ProfilesComboBox.SelectedIndex;
        }
        UpdateSettingsUI();
        await SaveSettingsAsync();
        Debug.WriteLine($"Profile changed to: {ProfilesComboBox.SelectedItem}");
    }

    private void AddProfileButton_Click(object sender, RoutedEventArgs e)
    {
        AddProfileNameTextBox.Text = string.Empty;
        var optionsButton = AddProfileButton;
        AddProfileFlyout.ShowAt(optionsButton);
        AddProfileNameTextBox.Focus(FocusState.Programmatic);
    }

    private void RenameProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProfilesComboBox.SelectedItem is not string currentProfile)
        {
            ShowErrorDialog("Please select a profile to rename");
            return;
        }

        RenameProfileNameTextBox.Text = currentProfile;
        RenameProfileNameTextBox.SelectAll();
        var optionsButton = ProfileOptionsButton;
        RenameProfileFlyout.ShowAt(optionsButton);
        RenameProfileNameTextBox.Focus(FocusState.Programmatic);
    }

    private void RemoveProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProfilesComboBox.SelectedItem is not string selectedProfile)
        {
            return;
        }

        if (ProfilesComboBox.Items.Count <= 1)
        {
            ShowErrorDialog("You must have at least one profile");
            return;
        }

        var optionsButton = ProfileOptionsButton;
        RemoveProfileFlyout.ShowAt(optionsButton);
    }

    private async void RemoveProfileConfirm_Click(object sender, RoutedEventArgs e)
    {
        if (ProfilesComboBox.SelectedItem is not string selectedProfile)
        {
            return;
        }

        IsUpdatingSettings = true;
        int currentIndex = ProfilesComboBox.SelectedIndex;
        ProfilesComboBox.Items.RemoveAt(currentIndex);
        Settings.RemoveProfile(currentIndex);
        if (ProfilesComboBox.Items.Count > 0)
        {
            ProfilesComboBox.SelectedIndex = Math.Min(currentIndex, ProfilesComboBox.Items.Count - 1);
        }
        IsUpdatingSettings = false;

        RemoveProfileFlyout.Hide();
        UpdateSettingsUI();
        await SaveSettingsAsync();
    }

    private async void AddProfileConfirm_Click(object sender, RoutedEventArgs e)
    {
        string profileName = AddProfileNameTextBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(profileName))
        {
            return;
        }

        if (ProfilesComboBox.Items.Contains(profileName))
        {
            ShowErrorDialog("A profile with this name already exists");
            return;
        }

        AppSettingsProfile newProfile = new()
        {
            Name = profileName,
            LinkActionOnProfileChange = Settings.GetActiveProfile().LinkActionOnProfileChange,
            DownloadFolderPath = Settings.GetActiveProfile().DownloadFolderPath,
            AdditionalArguments = Settings.GetActiveProfile().AdditionalArguments,
            Format = Settings.GetActiveProfile().Format,
            EmbedMetadata = Settings.GetActiveProfile().EmbedMetadata,
            Sponsorblock = Settings.GetActiveProfile().Sponsorblock,
            UseSystemFFMPEG = Settings.GetActiveProfile().UseSystemFFMPEG
        };
        Settings.AddAndUseProfile(newProfile);
        ProfilesComboBox.Items.Add(profileName);
        ProfilesComboBox.SelectedItem = profileName;
        AddProfileFlyout.Hide();
        UpdateRemoveButtonState();
        await SaveSettingsAsync();
    }

    private void AddProfileNameTextBox_KeyUp(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            AddProfileConfirm_Click(sender, e);
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            AddProfileFlyout.Hide();
        }
    }

    private async void RenameProfileConfirm_Click(object sender, RoutedEventArgs e)
    {
        string profileName = RenameProfileNameTextBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(profileName))
        {
            return;
        }
        if (ProfilesComboBox.SelectedItem is not string currentProfile)
        {
            return;
        }
        if (profileName == currentProfile)
        {
            return;
        }
        if (ProfilesComboBox.Items.Contains(profileName))
        {
            ShowErrorDialog("A profile with this name already exists");
            return;
        }

        IsUpdatingSettings = true;
        int selectedIndex = ProfilesComboBox.SelectedIndex;
        Settings.GetActiveProfile().Name = profileName;
        ProfilesComboBox.Items[selectedIndex] = profileName;
        ProfilesComboBox.SelectedIndex = selectedIndex;
        IsUpdatingSettings = false;
        RenameProfileFlyout.Hide();
        await SaveSettingsAsync();
    }

    private void RenameProfileNameTextBox_KeyUp(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            RenameProfileConfirm_Click(sender, e);
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            RenameProfileFlyout.Hide();
        }
    }

    private void UpdateRemoveButtonState()
    {
        RemoveButton.IsEnabled = ProfilesComboBox.Items.Count > 1;
    }

    private async void ShowErrorDialog(string message)
    {
        var dialog = new ContentDialog
        {
            Title = "Invalid Action",
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = XamlRoot
        };
        await dialog.ShowAsync();
    }

    public async void PickDestinationButton_Click(object sender, RoutedEventArgs e)
    {
        //disable the button to avoid double-clicking
        var senderButton = sender as Button;
        if (senderButton != null) senderButton.IsEnabled = false;

        FolderPicker downloadFolderPicker = new()
        {
            SuggestedStartLocation = PickerLocationId.Desktop
        };
        downloadFolderPicker.FileTypeFilter.Add("*");

        var window = App.MainWindow;
        var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        WinRT.Interop.InitializeWithWindow.Initialize(downloadFolderPicker, hWnd);

        // Open the picker for the user to pick a folder
        StorageFolder folder = await downloadFolderPicker.PickSingleFolderAsync();
        SavingSettingsProgressRing.IsActive = true;
        if (folder != null)
        {
            DownloadFolder = folder;
            Settings.GetActiveProfile().DownloadFolderPath = folder.Path;
            await SaveSettingsAsync();
            StorageApplicationPermissions.FutureAccessList.AddOrReplace("PickedFolderToken", folder);
            PickDestinationOutputTextBlock.Content = DownloadFolder.Path;
        }

        //re-enable the button
        if (senderButton != null) senderButton.IsEnabled = true;
        SavingSettingsProgressRing.IsActive = false;
    }

    public static async Task<string> Paste()
    {
        var package = Clipboard.GetContent();
        if (package.Contains(StandardDataFormats.Text))
        {
            return await package.GetTextAsync();
        }
        return "";
    }

    public async void DownloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy)
        {
            CancelDownload();
            return;
        }
        string link = LinkTextBox.Text.Trim();
        if (string.IsNullOrEmpty(link))
        {
            link = await Paste();
        }
        Download(link);
    }

    public async void Download(string link)
    {
        if (IsBusy) return;
        link = link.Trim();
        if (string.IsNullOrEmpty(link)) return;
        IsBusy = true;
        IsDownloadCancelled = false;
        DownloadStatusInfoBar.IsOpen = true;
        DownloadStatusInfoBar.Severity = InfoBarSeverity.Informational;
        DownloadStatusInfoBar.IsClosable = false;
        DownloadStatusInfoBar.Message = "Starting...";
        OpenDownloadButton.Visibility = Visibility.Collapsed;
        UpdateDownloadButton();
        DownloadProgressBar.IsIndeterminate = true;
        BadgeNotificationManager.Current.SetBadgeAsGlyph(BadgeNotificationGlyph.Activity);
        DownloadProgressBar.Minimum = 0;
        DownloadProgressBar.Maximum = 100;
        DownloadProgressBar.Value = 0;
        DownloadProgressBar.Visibility = Visibility.Visible;
        string arguments = GetDownloadArguments(link);

        DownloadProgress progress = new();
        DownloadCancellationTokenSource = new CancellationTokenSource();

        try
        {
            var tcs = new TaskCompletionSource<int?>();
            var psi = new ProcessStartInfo
            {
                FileName = YTDLPPath,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            string errorOutput = string.Empty;

            using Process downloadProcess = new() { StartInfo = psi, EnableRaisingEvents = true };
            DownloadProcess = downloadProcess;
            downloadProcess.OutputDataReceived += (s, ea) =>
            {
                var line = ea.Data;
                if (string.IsNullOrEmpty(line)) return;
                _ = DispatcherQueue.TryEnqueue(() =>
                {
                    DownloadStatusInfoBar.Message = line;
                });
                if (line.StartsWith("[download]"))
                {
                    var percent = progress.ExtractProgress(line);
                    if (percent != 0.0)
                    {
                        _ = DispatcherQueue.TryEnqueue(() =>
                        {
                            DownloadProgressBar.IsIndeterminate = false;
                            DownloadProgressBar.Value = (double)percent;
                        });
                    }
                }
            };
            downloadProcess.ErrorDataReceived += (s, ea) =>
            {
                if (!string.IsNullOrEmpty(ea.Data))
                {
                    errorOutput += ea.Data + "\n";
                }
            };
            downloadProcess.Exited += (s, ea) =>
            {
                tcs.TrySetResult(downloadProcess.ExitCode);
            };

            downloadProcess.Start();
            downloadProcess.BeginOutputReadLine();
            downloadProcess.BeginErrorReadLine();

            int? exitCode = await tcs.Task;

            if (exitCode == 0)
            {
                if (string.IsNullOrEmpty(errorOutput))
                {
                    DownloadStatusInfoBar.Severity = InfoBarSeverity.Success;
                    DownloadStatusInfoBar.Message = "Download completed successfully!";
                }
                else
                {
                    DownloadStatusInfoBar.Severity = InfoBarSeverity.Warning;
                    DownloadStatusInfoBar.Message = "Download completed with warnings:\n" + errorOutput.Trim() + "\n";
                }
                OpenDownloadButton.Visibility = Visibility.Visible;
                DownloadStatusInfoBar.IsOpen = true;
            }
            else if (IsDownloadCancelled)
            {
                DownloadStatusInfoBar.IsOpen = false;
            }
            else
            {
                DownloadStatusInfoBar.Severity = InfoBarSeverity.Error;
                DownloadStatusInfoBar.Message = string.IsNullOrWhiteSpace(errorOutput) ? "An error occurred. Please try again." : errorOutput.Trim();
                DownloadStatusInfoBar.IsOpen = true;
            }
        }
        catch (Exception ex)
        {
            DownloadStatusInfoBar.Severity = InfoBarSeverity.Error;
            DownloadStatusInfoBar.Message = "An error occurred while downloading: " + ex.Message;
            DownloadStatusInfoBar.IsOpen = true;
        }
        finally
        {
            DownloadProcess?.Dispose();
            DownloadProcess = null;
            DownloadCancellationTokenSource?.Dispose();
            DownloadCancellationTokenSource = null;
            IsBusy = false;
            DownloadStatusInfoBar.IsClosable = true;
            DownloadProgressBar.Visibility = Visibility.Collapsed;
            UpdateDownloadButton();
            BadgeNotificationManager.Current.ClearBadge();
        }
    }

    private string GetDownloadArguments(string link)
    {
        return $"{(DownloadFolder.Path != "" ? $"-P \"{DownloadFolder.Path}\"" : "")}"
            + (Settings.GetActiveProfile().UseSystemFFMPEG ? "" : $" --ffmpeg-location \"{FFMPEGPath}\" --js-runtimes deno:\"{DenoPath}\"")
            + (Settings.GetActiveProfile().Format.Equals("advanced", StringComparison.CurrentCultureIgnoreCase) ? "" : $" -t \"{Settings.GetActiveProfile().Format.ToLower()}\"")
            + (Settings.GetActiveProfile().EmbedMetadata ? " --embed-metadata --embed-subs --embed-thumbnail" : "")
            + (Settings.GetActiveProfile().Sponsorblock ? " --sponsorblock-remove sponsor" : "")
            + " " + Settings.GetActiveProfile().AdditionalArguments
            + " " + link;
    }

    public void CancelDownload()
    {
        DownloadCancellationTokenSource?.Cancel();

        if (DownloadProcess != null && !DownloadProcess.HasExited)
        {
            IsDownloadCancelled = true;
            try
            {
                DownloadProcess.Kill(true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error killing process: {ex.Message}");
            }
        }
    }

    public void OpenDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var folderPath = DownloadFolder.Path;
            if (!string.IsNullOrEmpty(folderPath) && Directory.Exists(folderPath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = folderPath,
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            DownloadStatusInfoBar.Severity = InfoBarSeverity.Error;
            DownloadStatusInfoBar.Message = "Failed to open folder: " + ex.Message;
            DownloadStatusInfoBar.IsOpen = true;
        }
    }

    public void LinkTextBox_KeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (IsBusy) return;
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            Download(LinkTextBox.Text);
        }
    }

    public void LinkTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateDownloadButton();
        if (Settings.GetActiveProfile().LinkActionOnProfileChange == "SaveLink")
        {
            Settings.GetActiveProfile().Link = LinkTextBox.Text;
        }
    }

    public void UpdateDownloadButton()
    {
        DownloadButton.Style = (Style)Application.Current.Resources[IsBusy ? "DefaultButtonStyle" : "AccentButtonStyle"];
        DownloadButton.Content = IsBusy ? "Cancel" : (string.IsNullOrEmpty(LinkTextBox.Text.Trim()) ? "Paste and Download" : "Download");
    }
    private async void FormatComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        Settings.GetActiveProfile().Format = GetFormatFromDisplayName(FormatComboBox.SelectedItem.ToString() ?? "") ?? "mp4";
        await SaveSettingsAsync();
    }

    private void AdditionalArgumentsTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        Settings.GetActiveProfile().AdditionalArguments = AdditionalArgumentsTextBox.Text;
    }

    public void Dispose()
    {
        _settingsLock?.Dispose();
        DownloadCancellationTokenSource?.Dispose();
        DownloadProcess?.Dispose();
    }

    [GeneratedRegex(@"(?<!\d)(\d+(?:[.,]\d+)?)\s?%", RegexOptions.CultureInvariant)]
    public static partial Regex DownloadPercentageRegex();

    [GeneratedRegex(@"Downloading item (\d+) of (\d+)")]
    public static partial Regex PlaylistItemsRegex();

    private void CopyCommandButton_Click(object sender, RoutedEventArgs e)
    {
        string link = LinkTextBox.Text.Trim();
        string command = $"{YTDLPPath} {GetDownloadArguments(link)}";
        var dataPackage = new DataPackage();
        dataPackage.SetText(command);
        Clipboard.SetContent(dataPackage);
    }

    private void CopyAppDataPathButton_Click(object sender, RoutedEventArgs e)
    {
        string appDataPath = ApplicationData.Current.LocalFolder.Path;
        var dataPackage = new DataPackage();
        dataPackage.SetText(appDataPath);
        Clipboard.SetContent(dataPackage);
    }

    private async void LinkOptions_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioMenuFlyoutItem radio)
        {
            if (radio.Name == "KeepSelectedLinkOption")
            {
                Settings.GetActiveProfile().LinkActionOnProfileChange = "";
            }
            else if (radio.Name == "SelectLastUsedLinkOption")
            {
                Settings.GetActiveProfile().LinkActionOnProfileChange = "SaveLink";
                Settings.GetActiveProfile().Link = LinkTextBox.Text;
            }
            else if (radio.Name == "ClearSelectedLinkOption")
            {
                Settings.GetActiveProfile().LinkActionOnProfileChange = "ClearLink";
            }
            await SaveSettingsAsync();
        }
    }
}
