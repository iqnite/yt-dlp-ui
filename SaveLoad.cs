using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

namespace YT_DLP_UI
{
    public sealed partial class HomePage
    {
        private readonly SemaphoreSlim _settingsLock = new(1, 1);
        private bool _isSettingsLoaded = false;
        public const string ProfilesFileName = "Profiles.json";
        public const string SettingsFileName = "Settings.json";
        public AppSettings Settings = new();
        private static readonly JsonTypeInfo<AppSettings> _appSettingsJsonTypeInfo = AppJsonSerializerContext.Default.AppSettings;
        private static readonly JsonTypeInfo<AppSettingsProfile> _oldAppSettingsJsonTypeInfo = OldAppJsonSerializerContext.Default.AppSettingsProfile;
        public static readonly JsonSerializerOptions JsonOptions = new()
        {
            IncludeFields = true,
            WriteIndented = true,
        };

        public struct AppSettingsProfile
        {
            public AppSettingsProfile()
            {
            }

            public string Name { get; set; } = "Default";
            public string Link { get; set; } = string.Empty;
            public string LinkActionOnProfileChange { get; set; } = "ClearLink";
            public string DownloadFolderPath { get; set; } = string.Empty;
            public string AdditionalArguments { get; set; } = string.Empty;
            public string Format { get; set; } = "mp4";
            public bool EmbedMetadata { get; set; } = true;
            public bool UseSystemFFMPEG { get; set; }
        }

        public struct AppSettings
        {
            public AppSettings()
            {
            }

            public AppSettingsProfile[] Profiles { get; set; } = [new()];
            public int ActiveProfileId { get; set; } = 0;
            public ref AppSettingsProfile GetActiveProfile()
            {
                return ref Profiles[Math.Clamp(ActiveProfileId, 0, Profiles.Length - 1)];
            }
            public void AddAndUseProfile(AppSettingsProfile profile)
            {
                var profilesList = Profiles.ToList();
                profilesList.Add(profile);
                Profiles = profilesList.ToArray();
                ActiveProfileId = Profiles.Length - 1;
            }
            public void RemoveProfile(int index)
            {
                if (index < 0 || index >= Profiles.Length)
                {
                    throw new ArgumentOutOfRangeException(nameof(index), "Invalid profile index");
                }
                var profilesList = Profiles.ToList();
                if (profilesList.Count <= 1)
                {
                    throw new InvalidOperationException("Cannot remove the last profile");
                }
                profilesList.RemoveAt(index);
                Profiles = profilesList.ToArray();
                if (ActiveProfileId >= Profiles.Length)
                {
                    ActiveProfileId = Profiles.Length - 1;
                }
            }
        }

        public async void SaveSettingsUI(object sender, RoutedEventArgs e)
        {
            SavingSettingsProgressRing.IsActive = true;
            await SaveSettingsAsync();
            SavingSettingsProgressRing.IsActive = false;
        }

        public async void EmbedMetadataToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleSwitch toggle)
            {
                Settings.GetActiveProfile().EmbedMetadata = toggle.IsOn;
            }
            SaveSettingsUI(sender, e);
        }

        public async void UseSystemFFMPEGToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleSwitch toggle)
            {
                Settings.GetActiveProfile().UseSystemFFMPEG = toggle.IsOn;
            }
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

                var tempFileName = ProfilesFileName + ".temp";
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
                    actualFile = await ApplicationData.Current.LocalFolder.GetFileAsync(ProfilesFileName);
                }
                catch
                {
                    actualFile = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                        ProfilesFileName, CreationCollisionOption.ReplaceExisting);
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

                var isMigrating = false;
                StorageFile? file;
                try
                {
                    file = await ApplicationData.Current.LocalFolder.GetFileAsync(ProfilesFileName);
                }
                catch (FileNotFoundException)
                {
                    isMigrating = true;
                    Debug.WriteLine("Profiles file not found, migrating from old settings");
                    file = await ApplicationData.Current.LocalFolder.GetFileAsync(SettingsFileName);
                }
                string json = await FileIO.ReadTextAsync(file);
                if (string.IsNullOrWhiteSpace(json))
                {
                    Debug.WriteLine("WARNING: Settings file exists but is empty, using defaults");
                    Settings = new();
                    return;
                }
                if (isMigrating)
                {
                    var loadedOldSettings = JsonSerializer.Deserialize(json, _oldAppSettingsJsonTypeInfo);
                    Settings = new()
                    {
                        Profiles = [ new AppSettingsProfile
                            {
                                DownloadFolderPath = loadedOldSettings.DownloadFolderPath,
                                AdditionalArguments = loadedOldSettings.AdditionalArguments,
                                Format = loadedOldSettings.Format,
                                EmbedMetadata = loadedOldSettings.EmbedMetadata,
                                UseSystemFFMPEG = loadedOldSettings.UseSystemFFMPEG
                            } ],
                        ActiveProfileId = 0
                    };
                    Debug.WriteLine("Migration successful, saving new settings format");
                    await SaveSettingsAsync();
                }
                else
                {
                    var loadedSettings = JsonSerializer.Deserialize(json, _appSettingsJsonTypeInfo);
                    Settings = loadedSettings;
                }
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
                var activeProfile = Settings.GetActiveProfile();
                activeProfile.DownloadFolderPath ??= string.Empty;
                activeProfile.Format ??= "mp4";
                activeProfile.AdditionalArguments ??= string.Empty;

                _isSettingsLoaded = true;
                _settingsLock.Release();
                Debug.WriteLine("Settings are ready for use");
            }
        }
    }

    [JsonSerializable(typeof(HomePage.AppSettings))]
    internal partial class AppJsonSerializerContext : JsonSerializerContext
    {
    }
    [JsonSerializable(typeof(HomePage.AppSettingsProfile))]
    internal partial class OldAppJsonSerializerContext : JsonSerializerContext
    {
    }
}
