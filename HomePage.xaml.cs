using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Windows.BadgeNotifications;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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
        public string? DownloadFolderPath { get; set; }
        public string? AdditionalArguments { get; set; }
        public string? Format { get; set; }
    }

    private AppSettings settings = new();

    public HomePage()
    {
        InitializeComponent();
        Loaded += HomePage_Loaded;
        Unloaded += Page_Unloaded;
    }

    private async void HomePage_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadSettingsAsync();
        if (!string.IsNullOrEmpty(settings.DownloadFolderPath) && Directory.Exists(settings.DownloadFolderPath))
        {
            downloadFolder = await StorageFolder.GetFolderFromPathAsync(settings.DownloadFolderPath);
        }
        PickDestinationOutputTextBlock.Text = downloadFolder.Path;
        settings.AdditionalArguments ??= "";
        AdditionalArgumentsTextBox.Text = settings.AdditionalArguments;
        settings.Format ??= "MP4";
        FormatComboBox.Text = settings.Format;
    }

    private async void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        // Save settings when the page is unloaded
        await SaveSettingsAsync();
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

    private async void DownloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        busy = true;
        string link = LinkTextBox.Text.Trim();
        if (string.IsNullOrEmpty(link)) return;
        DownloadStatusInfoBar.IsOpen = false;
        OpenDownloadButton.Visibility = Visibility.Collapsed;
        DownloadButton.IsEnabled = false;
        DownloadButton.Content = "Downloading...";
        DownloadProgressBar.IsIndeterminate = true;
        BadgeNotificationManager.Current.SetBadgeAsGlyph(BadgeNotificationGlyph.Activity);
        DownloadProgressBar.Minimum = 0;
        DownloadProgressBar.Maximum = 100;
        DownloadProgressBar.Value = 0;
        DownloadProgressBar.Visibility = Visibility.Visible;

        string arguments = $"{(downloadFolder.Path != "" ? $"-P \"{downloadFolder.Path}\"" : "")}"
            + $" --ffmpeg-location {ffmpegPath} "
            + $" -t {settings.Format?.ToLower() ?? "mp4"} "
            + settings.AdditionalArguments + " "
            + link;

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
                CreateNoWindow = true
            };

            string errorOutput = string.Empty;

            using var downloadProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };
            downloadProcess.OutputDataReceived += (s, ea) =>
            {
                if (ea.Data != null)
                {
                    // Example: [download]  42.3% of   13.48MiB in 00:00:04 at 2.86MiB/s
                    var line = ea.Data;
                    if (line.StartsWith("[download]"))
                    {
                        var percentStr = ExtractPercentage(line);
                        if (percentStr != null && double.TryParse(percentStr, out double percent))
                        {
                            _ = DispatcherQueue.TryEnqueue(() =>
                            {
                                DownloadProgressBar.IsIndeterminate = false;
                                DownloadProgressBar.Value = percent;
                            });
                        }
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
            DownloadProgressBar.Visibility = Visibility.Collapsed;
            DownloadButton.Content = "Download";
            DownloadButton.IsEnabled = true;
            busy = false;
            BadgeNotificationManager.Current.ClearBadge();
        }
    }

    private static string? ExtractPercentage(string line)
    {
        // Looks for a percentage in the format: [download]  42.3% ...
        int percentIdx = line.IndexOf('%');
        if (percentIdx > 0)
        {
            int start = percentIdx - 1;
            while (start >= 0 && (char.IsDigit(line[start]) || line[start] == '.'))
                start--;
            start++;
            return line[start..percentIdx];
        }
        return null;
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
        try
        {
            // Use a semaphore to prevent concurrent access to settings file
            await _settingsLock.WaitAsync();

            settings ??= new AppSettings();
            settings.DownloadFolderPath ??= string.Empty;
            settings.AdditionalArguments ??= string.Empty;

            JsonSerializerOptions jsonSerializerOptions = new()
            {
                WriteIndented = true
            };
            var options = jsonSerializerOptions;

            string json = JsonSerializer.Serialize(settings, options);

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

            var loadedSettings = JsonSerializer.Deserialize<AppSettings>(json);
            if (loadedSettings != null)
            {
                settings = loadedSettings;

                // Ensure no null values
                settings.DownloadFolderPath ??= string.Empty;
                settings.AdditionalArguments ??= string.Empty;

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
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            DownloadButton_Click(sender, e);
        }
    }

    private async void AdditionalArgumentsTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (AdditionalArgumentsTextBox != null)
        {
            SavingSettingsProgressRing.IsActive = true;
            settings.AdditionalArguments = AdditionalArgumentsTextBox.Text?.Trim() ?? string.Empty;
            await SaveSettingsAsync();
            SavingSettingsProgressRing.IsActive = false;
        }
    }

    public void Dispose()
    {
        _settingsLock?.Dispose();
    }

    private async void FormatComboBox_SelectionChanged(object _, object __)
    {
        SavingSettingsProgressRing.IsActive = true;
        settings.Format = FormatComboBox.Text;
        await SaveSettingsAsync();
        SavingSettingsProgressRing.IsActive = false;
    }
}
