using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using System.Text.Json.Serialization.Metadata;

namespace YT_DLP_UI
{
    public sealed partial class HomePage
    {
        private readonly SemaphoreSlim _settingsLock = new(1, 1);
        private bool _isSettingsLoaded = false;
        public const string SettingsFileName = "Settings.json";
        public AppSettings Settings = new();
        private static readonly JsonTypeInfo<AppSettings> _appSettingsJsonTypeInfo = AppJsonSerializerContext.Default.AppSettings;
        public static readonly JsonSerializerOptions JsonOptions = new()
        {
            IncludeFields = true,
            WriteIndented = true,
        };

        public struct AppSettings
        {
            public AppSettings()
            {
            }

            public string DownloadFolderPath { get; set; } = string.Empty;
            public string AdditionalArguments { get; set; } = string.Empty;
            public string Format { get; set; } = "mp4";
            public bool UseSystemFFMPEG { get; set; }
        }

        public async void SaveSettingsUI(object sender, RoutedEventArgs e)
        {
            SavingSettingsProgressRing.IsActive = true;
            await SaveSettingsAsync();
            SavingSettingsProgressRing.IsActive = false;
        }

        public async Task SaveSettingsAsync()
        {
            if (!_isSettingsLoaded)
            {
                return;
            }

            try
            {
                // Use a semaphore to prevent concurrent access to Settings file
                await _settingsLock.WaitAsync();

                string json = JsonSerializer.Serialize(Settings, _appSettingsJsonTypeInfo);

                if (string.IsNullOrWhiteSpace(json) || json == "{}" || json == "null")
                {
                    Debug.WriteLine("WARNING: Attempted to save empty Settings, operation aborted");
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
                Debug.WriteLine($"ERROR saving Settings: {ex.Message}");
            }
            finally
            {
                _settingsLock.Release();
            }
        }

        public async Task LoadSettingsAsync()
        {
            try
            {
                await _settingsLock.WaitAsync();

                var file = await ApplicationData.Current.LocalFolder.GetFileAsync(SettingsFileName);
                string json = await FileIO.ReadTextAsync(file);

                if (string.IsNullOrWhiteSpace(json))
                {
                    Debug.WriteLine("WARNING: Settings file exists but is empty, using defaults");
                    Settings = new();
                    return;
                }

                var loadedSettings = JsonSerializer.Deserialize(json, _appSettingsJsonTypeInfo);
                Settings = loadedSettings;

                Debug.WriteLine("Settings loaded successfully");
            }
            catch (FileNotFoundException)
            {
                Debug.WriteLine("Settings file not found, using defaults");
                Settings = new();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ERROR loading Settings: {ex.Message}");
                Settings = new(); // Use defaults on any error
            }
            finally
            {
                Settings.DownloadFolderPath ??= string.Empty;
                Settings.Format ??= "mp4";
                Settings.AdditionalArguments ??= string.Empty;

                _isSettingsLoaded = true;
                _settingsLock.Release();
                Debug.WriteLine("Settings are ready for use");
            }
        }

        public void Dispose()
        {
            _settingsLock?.Dispose();
        }
    }

    [JsonSerializable(typeof(HomePage.AppSettings))]
    internal partial class AppJsonSerializerContext : JsonSerializerContext
    {
    }
}
