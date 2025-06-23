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
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Storage;
using Windows.Storage.AccessCache;
using Windows.Storage.Pickers;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace YT_DLP_UI;

/// <summary>
/// An empty page that can be used on its own or navigated to within a Frame.
/// </summary>
public sealed partial class HomePage : Page
{
    private const string SettingsFileName = "settings.json";
    private StorageFolder downloadFolder = ApplicationData.Current.LocalFolder;
    private string exePath = Path.Combine(AppContext.BaseDirectory, "yt-dlp", "yt-dlp.exe");

    // Define your settings structure
    public class AppSettings
    {
        public string? DownloadFolderPath { get; set; }
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

        string arguments = ((downloadFolder.Path != "") ? ("-P \"" + downloadFolder.Path + "\" ") : "") + link;

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
            downloadProcess.ErrorDataReceived += (s, ea) => { /* Optionally handle errors */ };
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
                DownloadStatusInfoBar.Message = "Download failed with exit code: " + exitCode + "\nCheck the link and try again.";
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
            return line.Substring(start, percentIdx - start);
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
        var file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
            SettingsFileName, CreationCollisionOption.ReplaceExisting);
        string json = JsonSerializer.Serialize(settings);
        await FileIO.WriteTextAsync(file, json);
    }

    private async Task LoadSettingsAsync()
    {
        try
        {
            var file = await ApplicationData.Current.LocalFolder.GetFileAsync(SettingsFileName);
            string json = await FileIO.ReadTextAsync(file);
            settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            settings = new AppSettings(); // Use defaults if file doesn't exist
        }
    }
}
