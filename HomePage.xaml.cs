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
using System.Runtime.Intrinsics.Arm;
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
    private bool Busy = false;

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

            string numeric = match.Groups[1].Value.Replace(',', '.');
            if (!double.TryParse(numeric, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double newPercentage)) return;
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

    public HomePage()
    {
        InitializeComponent();
        Loaded += HomePage_Loaded;
    }

    public async void HomePage_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadSettingsAsync();
        if (!string.IsNullOrEmpty(Settings.DownloadFolderPath) && Directory.Exists(Settings.DownloadFolderPath))
        {
            DownloadFolder = await StorageFolder.GetFolderFromPathAsync(Settings.DownloadFolderPath);
        }
        PickDestinationOutputTextBlock.Content = DownloadFolder.Path;
        FormatComboBox.SelectedItem = Settings.Format;
        AdditionalArgumentsTextBox.Text = Settings.AdditionalArguments;
        UseSystemFFMPEGToggle.IsOn = Settings.UseSystemFFMPEG;
        BadgeNotificationManager.Current.SetBadgeAsGlyph(BadgeNotificationGlyph.None);
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
            Settings.DownloadFolderPath = folder.Path;
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
        string link = LinkTextBox.Text.Trim();
        if (string.IsNullOrEmpty(link))
        {
            link = await Paste();
        }
        Download(link);
    }

    public async void Download(string link)
    {
        if (Busy) return;
        link = link.Trim();
        if (string.IsNullOrEmpty(link)) return;
        Busy = true;
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

        string arguments = $"{(DownloadFolder.Path != "" ? $"-P \"{DownloadFolder.Path}\"" : "")}"
            + (Settings.UseSystemFFMPEG ? "" : $" --ffmpeg-location \"{FFMPEGPath}\"")
            + $" --js-runtimes deno:\"{DenoPath}\""
            + (Settings.Format.Equals("advanced", StringComparison.CurrentCultureIgnoreCase) ? "" : $" -t \"{Settings.Format.ToLower()}\"")
            + " " + Settings.AdditionalArguments
            + " " + link;

        DownloadProgress progress = new();

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
            Busy = false;
            DownloadStatusInfoBar.IsClosable = true;
            DownloadProgressBar.Visibility = Visibility.Collapsed;
            UpdateDownloadButton();
            BadgeNotificationManager.Current.ClearBadge();
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
        if (Busy) return;
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            Download(LinkTextBox.Text);
        }
    }

    public void LinkTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateDownloadButton();
    }

    public void UpdateDownloadButton()
    {
        DownloadButton.IsEnabled = !Busy;
        DownloadButton.Content = Busy ? "Downloading..." : (string.IsNullOrEmpty(LinkTextBox.Text.Trim()) ? "Paste and Download" : "Download");
    }

    [GeneratedRegex(@"(?<!\d)(\d+(?:[.,]\d+)?)\s?%", RegexOptions.CultureInvariant)]
    public static partial Regex DownloadPercentageRegex();

    [GeneratedRegex(@"Downloading item (\d+) of (\d+)")]
    public static partial Regex PlaylistItemsRegex();
}
