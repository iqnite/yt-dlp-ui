using ABI.Windows.ApplicationModel.Activation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Web.WebView2.Core;
using Microsoft.Windows.BadgeNotifications;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Storage;
using Windows.Storage.AccessCache;
using Windows.Storage.Pickers;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace YT_DLP_UI;

public sealed partial class HomePage : Page, IDisposable
{
    private const string SettingsFileName = "settings.json";
    private StorageFolder downloadFolder = ApplicationData.Current.LocalFolder;
    private readonly string exePath = Path.Combine(AppContext.BaseDirectory, "yt-dlp", "yt-dlp.exe");
    private readonly string ffmpegPath = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg-master-latest-win32-gpl", "bin", "ffmpeg.exe");
    private bool busy = false;
    private readonly SemaphoreSlim _settingsLock = new(1, 1);

    public class AppSettings
    {
        public bool Loaded { get; set; } = false;
        public string? DownloadFolderPath { get; set; }
        public string AdditionalArguments { get; set; } = "";
        public string Format { get; set; } = "mp4";
        public bool UseBundledFFMPEG { get; set; } = true;
    }

    [JsonSerializable(typeof(AppSettings))]
    internal partial class AppSettingsJsonContext : JsonSerializerContext
    {
    }

    public class DownloadProgress
    {
        public double Percentage = 0.0;
        public int PlaylistItem = 1;
        public int PlaylistTotal = 1;
        private double ExtractedProgress = 0.0;

        public void ExtractPercentage(string line)
        {
            // Looks for a Percentage in the format: [download]  42.3% ...
            Regex regex = DownloadPercentageRegex();
            Match match = regex.Match(line);
            if (!match.Success) return;
            if (!double.TryParse(match.Groups[1].Value, out double newPercentage)) return;
            if (newPercentage > 100) return;
            Percentage = newPercentage;
        }

        public void ExtractPlaylistItems(string line)
        {
            // Looks for playlist item/total in the format: [download] Downloading 1 of 8
            int prevPlaylistItem = PlaylistItem;
            Regex regex = PlaylistItemsRegex();
            Match match = regex.Match(line);
            if (!match.Success) return;
            if (!int.TryParse(match.Groups[1].Value, out PlaylistItem)) return;
            if (!int.TryParse(match.Groups[2].Value, out PlaylistTotal)) return;
            if (PlaylistItem > prevPlaylistItem) Percentage = 0;
        }

        public double ExtractProgress(string line)
        {
            double prevProgress = ExtractedProgress;
            ExtractPercentage(line);
            ExtractPlaylistItems(line);

            Debug.WriteLine($"{PlaylistItem} of {PlaylistTotal}, {Percentage}%");
            double newProgress = (PlaylistItem - 1 + Percentage / 100) / PlaylistTotal * 100;
            if (newProgress > prevProgress) ExtractedProgress = newProgress;
            return ExtractedProgress;
        }
    }

    private AppSettings settings = new();

    public HomePage()
    {
        InitializeComponent();
        Loaded += HomePage_Loaded;
    }

    private async void HomePage_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadSettingsAsync();
        if (!string.IsNullOrEmpty(settings.DownloadFolderPath) && Directory.Exists(settings.DownloadFolderPath))
        {
            downloadFolder = await StorageFolder.GetFolderFromPathAsync(settings.DownloadFolderPath);
        }
        PickDestinationOutputTextBlock.Text = downloadFolder.Path;
        FormatComboBox.SelectedItem = settings.Format;
        AdditionalArgumentsTextBox.Text = settings.AdditionalArguments;
        UseBundledFFMPEGToggle.IsOn = settings.UseBundledFFMPEG;
        BadgeNotificationManager.Current.SetBadgeAsGlyph(BadgeNotificationGlyph.None);
    }

    private async void PickDestinationButton_Click(object sender, RoutedEventArgs e)
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
            downloadFolder = folder;
            settings.DownloadFolderPath = folder.Path;
            await SaveSettingsAsync();
            StorageApplicationPermissions.FutureAccessList.AddOrReplace("PickedFolderToken", folder);
            PickDestinationOutputTextBlock.Text = downloadFolder.Path;
        }

        //re-enable the button
        if (senderButton != null) senderButton.IsEnabled = true;
        SavingSettingsProgressRing.IsActive = false;
    }

    private static async Task<string> Paste()
    {
        var package = Clipboard.GetContent();
        if (package.Contains(StandardDataFormats.Text))
        {
            return await package.GetTextAsync();
        }
        return "";
    }

    private async void DownloadButton_Click(object sender, RoutedEventArgs e)
    {
        string link = LinkTextBox.Text.Trim();
        if (string.IsNullOrEmpty(link))
        {
            link = await Paste();
        }
        Download(link);
    }

    private async void Download(string link)
    {
        if (busy) return;
        link = link.Trim();
        if (string.IsNullOrEmpty(link)) return;
        busy = true;
        DownloadStatusInfoBar.IsOpen = true;
        DownloadStatusInfoBar.Severity = InfoBarSeverity.Informational;
        DownloadStatusInfoBar.IsClosable = false;
        DownloadStatusInfoBar.Message = "Downloading...";
        OpenDownloadButton.Visibility = Visibility.Collapsed;
        UpdateDownloadButton();
        DownloadProgressBar.IsIndeterminate = true;
        BadgeNotificationManager.Current.SetBadgeAsGlyph(BadgeNotificationGlyph.Activity);
        DownloadProgressBar.Minimum = 0;
        DownloadProgressBar.Maximum = 100;
        DownloadProgressBar.Value = 0;
        DownloadProgressBar.Visibility = Visibility.Visible;

        string arguments = $"{(downloadFolder.Path != "" ? $"-P \"{downloadFolder.Path}\"" : "")}"
            + (settings.UseBundledFFMPEG ? $" --ffmpeg-location \"{ffmpegPath}\"" : "")
            + $" -t \"{(string.IsNullOrEmpty(settings.Format) ? "mp4" : settings.Format.ToLower())}\" "
            + settings.AdditionalArguments + " "
            + link;

        DownloadProgress progress = new();

        try
        {
            var tcs = new TaskCompletionSource<int?>();
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            string errorOutput = string.Empty;

            using Process downloadProcess = new() { StartInfo = psi, EnableRaisingEvents = true };
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
                DownloadStatusInfoBar.Severity = InfoBarSeverity.Success;
                DownloadStatusInfoBar.Message = "Download completed successfully!";
                OpenDownloadButton.Visibility = Visibility.Visible;
                DownloadStatusInfoBar.IsOpen = true;
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
            busy = false;
            DownloadStatusInfoBar.IsClosable = true;
            DownloadProgressBar.Visibility = Visibility.Collapsed;
            UpdateDownloadButton();
            BadgeNotificationManager.Current.ClearBadge();
        }
    }

    private void OpenDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var folderPath = downloadFolder.Path;
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

    private async Task SaveSettingsAsync()
    {
        if (!settings.Loaded) return;
        try
        {
            // Use a semaphore to prevent concurrent access to settings file
            await _settingsLock.WaitAsync();

            settings ??= new AppSettings();
            settings.DownloadFolderPath ??= string.Empty;
            settings.AdditionalArguments ??= string.Empty;

            string json = JsonSerializer.Serialize(settings, AppSettingsJsonContext.Default.AppSettings);

            if (string.IsNullOrWhiteSpace(json) || json == "{}" || json == "null")
            {
                Debug.WriteLine("WARNING: Attempted to save empty settings, operation aborted");
                return;
            }

            var tempFileName = SettingsFileName + ".temp";
            var tempFile = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                tempFileName, CreationCollisionOption.ReplaceExisting);
            await FileIO.WriteTextAsync(tempFile, json);
            string verificationJson = await FileIO.ReadTextAsync(tempFile);
            if (string.IsNullOrWhiteSpace(verificationJson))
            {
                Debug.WriteLine("ERROR: Settings verification failed - temp file is empty");
                return;
            }

            StorageFile actualFile;
            try
            {
                actualFile = await ApplicationData.Current.LocalFolder.GetFileAsync(SettingsFileName);
            }
            catch
            {
                actualFile = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                    SettingsFileName, CreationCollisionOption.ReplaceExisting);
            }

            await tempFile.CopyAndReplaceAsync(actualFile);
            await tempFile.DeleteAsync();
            Debug.WriteLine("Settings saved successfully");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ERROR saving settings: {ex.Message}");
        }
        finally
        {
            _settingsLock.Release();
        }
    }

    private async Task LoadSettingsAsync()
    {
        try
        {
            await _settingsLock.WaitAsync();

            var file = await ApplicationData.Current.LocalFolder.GetFileAsync(SettingsFileName);
            string json = await FileIO.ReadTextAsync(file);

            if (string.IsNullOrWhiteSpace(json))
            {
                Debug.WriteLine("WARNING: Settings file exists but is empty, using defaults");
                settings = new AppSettings();
                return;
            }

            var loadedSettings = JsonSerializer.Deserialize<AppSettings>(json, AppSettingsJsonContext.Default.AppSettings);
            if (loadedSettings != null)
            {
                settings = loadedSettings;

                // Ensure no null values
                settings.DownloadFolderPath ??= string.Empty;
                settings.AdditionalArguments ??= string.Empty;

                settings.Loaded = true;
                Debug.WriteLine("Settings loaded successfully");
            }
            else
            {
                Debug.WriteLine("WARNING: Failed to deserialize settings, using defaults");
                settings = new AppSettings();
            }
        }
        catch (FileNotFoundException)
        {
            Debug.WriteLine("Settings file not found, using defaults");
            settings = new AppSettings();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ERROR loading settings: {ex.Message}");
            settings = new AppSettings(); // Use defaults on any error
        }
        finally
        {
            _settingsLock.Release();
        }
    }

    private void LinkTextBox_KeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (busy) return;
        UpdateDownloadButton();

        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            Download(LinkTextBox.Text);
        }
    }

    private void UpdateDownloadButton()
    {
        DownloadButton.IsEnabled = !busy;
        DownloadButton.Content = busy ? "Downloading..." : (string.IsNullOrEmpty(LinkTextBox.Text.Trim()) ? "Paste and Download" : "Download");
    }

    private async void SaveSettingsUI(object sender, RoutedEventArgs e)
    {
        SavingSettingsProgressRing.IsActive = true;
        await SaveSettingsAsync();
        SavingSettingsProgressRing.IsActive = false;
    }

    public void Dispose()
    {
        _settingsLock?.Dispose();
    }

    [GeneratedRegex(@"(\d+.?\d*) ?%")]
    private static partial Regex DownloadPercentageRegex();

    [GeneratedRegex(@"Downloading item (\d+) of (\d+)")]
    private static partial Regex PlaylistItemsRegex();
}
