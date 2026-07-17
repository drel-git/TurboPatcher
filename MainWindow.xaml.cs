using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Input;
using System.Diagnostics;
using System.Windows.Navigation;

namespace TurboPatcher;

public partial class MainWindow : Window
{
    private readonly PatcherSettings _settings;
    private readonly PatcherService _service = new();
    private readonly ObservableCollection<string> _logEntries = [];
    private CancellationTokenSource? _cts;
    private string _remoteSha = "";
    private string _remoteVersion = "";
    private string _installedSha = "";
    private string _installedVersion = "";
    private bool _busy;
    private string _selfLatestVersion = "";

    public MainWindow()
    {
        InitializeComponent();
        _settings = PatcherSettings.Load();
        LogItems.ItemsSource = _logEntries;
        // First run: try to find the MacroQuest folder so new users don't have to.
        if (string.IsNullOrWhiteSpace(_settings.MacroQuestFolder))
        {
            var detected = PatcherService.DetectMacroQuestFolder();
            if (detected != null)
            {
                _settings.MacroQuestFolder = detected;
                AppendLog($"Detected MacroQuest folder: {detected}");
            }
        }
        MqDirTextBox.Text = _settings.MacroQuestFolder;
        var localVer = PatcherService.LocalPatcherVersionString();
        VersionText.Text = string.IsNullOrEmpty(localVer) ? "" : $"Patcher v{localVer}";
        MaybeNoteRelaunchAfterSelfUpdate();
        _ = RefreshSelfUpdateAsync();
        _ = RefreshAsync();
    }

    private void MaybeNoteRelaunchAfterSelfUpdate()
    {
        try
        {
            var marker = Path.Combine(Path.GetTempPath(), "TurboPatcher_update", "relaunched.flag");
            if (!File.Exists(marker)) return;
            File.Delete(marker);
            var ver = PatcherService.LocalPatcherVersionString();
            AppendLog(string.IsNullOrEmpty(ver)
                ? "Patcher relaunched after self-update."
                : $"Patcher updated to v{ver}.");
        }
        catch { }
    }

    private async Task RefreshSelfUpdateAsync()
    {
        try
        {
            var result = await Task.Run(() => _service.CheckSelfUpdate());
            if (!result.UpdateAvailable)
            {
                SelfUpdateBanner.Visibility = Visibility.Collapsed;
                return;
            }
            _selfLatestVersion = result.LatestVersion;
            SelfUpdateText.Text =
                $"Patcher update available: you have v{result.LocalVersion}, latest is v{result.LatestVersion}. " +
                "Suite updates still work — update the patcher first for the newest installer features.";
            SelfUpdateBanner.Visibility = Visibility.Visible;
            AppendLog($"Patcher update available: v{result.LocalVersion} → v{result.LatestVersion}");
        }
        catch
        {
            SelfUpdateBanner.Visibility = Visibility.Collapsed;
        }
    }

    private async void SelfUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        SelfUpdateButton.IsEnabled = false;
        SelfUpdateButton.Content = "Updating...";
        try
        {
            var cts = new CancellationTokenSource();
            var log = new Progress<string>(AppendLog);
            var downloaded = await _service.DownloadLatestPatcherAsync(log, cts.Token);
            var target = Environment.ProcessPath;
            if (string.IsNullOrEmpty(target) || !File.Exists(target))
                throw new InvalidOperationException("Could not resolve this patcher's path.");

            var helper = PatcherService.WriteWindowsSelfUpdateHelper(
                Environment.ProcessId, downloaded, target, Path.GetDirectoryName(target));
            try
            {
                File.WriteAllText(
                    Path.Combine(Path.GetTempPath(), "TurboPatcher_update", "relaunched.flag"),
                    _selfLatestVersion);
            }
            catch { }

            Process.Start(new ProcessStartInfo
            {
                FileName = helper,
                UseShellExecute = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(helper) ?? "",
            });
            AppendLog("Restarting into the new patcher...");
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            AppendLog($"[error] Self-update failed: {ex.Message}");
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = PatcherService.PatcherDownloadUrl,
                    UseShellExecute = true
                });
                AppendLog("Opened browser download as fallback.");
            }
            catch (Exception ex2)
            {
                System.Windows.MessageBox.Show(
                    $"Could not update or open the download link.\n\n{ex.Message}\n{ex2.Message}",
                    "Turbo Patcher",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            SelfUpdateButton.Content = "Update Patcher";
            SelfUpdateButton.IsEnabled = true;
            _busy = false;
        }
    }

    private static string Short(string sha) => string.IsNullOrEmpty(sha) ? "none" : (sha.Length >= 7 ? sha[..7] : sha);

    // "v1.1.0 (69ad967)" when a human version is known, otherwise just the SHA.
    private static string Pretty(string version, string sha) =>
        string.IsNullOrEmpty(version) ? Short(sha) : $"v{version} ({Short(sha)})";

    private bool FolderValid =>
        !string.IsNullOrWhiteSpace(_settings.MacroQuestFolder)
        && Directory.Exists(Path.Combine(_settings.MacroQuestFolder, "lua"));

    private async Task RefreshAsync()
    {
        if (_busy) return;
        if (!FolderValid)
        {
            PatchNotesText.Text = "Pick your MacroQuest folder (the one containing 'lua' and 'Macros').";
            StatusLine.Text = "Select your MacroQuest folder to begin.";
            ActionButton.Content = "Check for Updates";
            ActionButton.IsEnabled = false;
            return;
        }

        ActionButton.IsEnabled = false;
        PatchNotesText.Text = "Checking...";
        var mq = _settings.MacroQuestFolder;
        var (hasNew, remoteSha, remoteVersion, log, installedSha, installedVersion) =
            await Task.Run(() => _service.CheckForUpdate(mq, _settings.InstalledSha, _settings.InstalledVersion));
        _remoteSha = remoteSha;
        _remoteVersion = remoteVersion;
        _installedSha = installedSha;
        _installedVersion = installedVersion;
        // Keep AppData in sync with what we resolved from disk.
        if (!string.IsNullOrEmpty(installedSha) && installedSha != _settings.InstalledSha)
        {
            _settings.InstalledSha = installedSha;
            _settings.InstalledVersion = installedVersion;
            _settings.Save();
        }
        else if (!string.IsNullOrEmpty(installedVersion) && string.IsNullOrEmpty(_settings.InstalledVersion))
        {
            _settings.InstalledVersion = installedVersion;
            _settings.Save();
        }
        PatchNotesText.Text = log;
        StatusLine.Text = string.IsNullOrEmpty(installedSha) && string.IsNullOrEmpty(installedVersion)
            ? $"Not installed yet · Available: {Pretty(remoteVersion, remoteSha)}"
            : $"Installed: {Pretty(installedVersion, installedSha)} · Available: {Pretty(remoteVersion, remoteSha)} · {(hasNew ? "update available" : "up to date")}";
        ActionButton.Content = string.IsNullOrEmpty(installedSha) && string.IsNullOrEmpty(installedVersion)
            ? "Install"
            : hasNew ? "Update Now" : "Reinstall / Recheck";
        ActionButton.IsEnabled = true;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void FooterLink_RequestNavigate(
        object sender,
        RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = e.Uri.AbsoluteUri,
                UseShellExecute = true
            });

            e.Handled = true;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"Could not open the link.\n\n{ex.Message}",
                "Turbo Patcher",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        Close();
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "Select your MacroQuest folder (contains 'lua' and 'Macros')",
            SelectedPath = Directory.Exists(_settings.MacroQuestFolder) ? _settings.MacroQuestFolder : "",
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            MqDirTextBox.Text = dlg.SelectedPath;   // triggers TextChanged -> save + refresh
        }
    }

    private void MqDirTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _settings.MacroQuestFolder = MqDirTextBox.Text;
        _settings.Save();
        _ = RefreshAsync();
    }

    private void AppendLog(string message)
    {
        _logEntries.Add(message);
        LogScroller.ScrollToBottom();
    }

    private async void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();

        ActionButton.IsEnabled = false;
        ActionButton.Content = "Working...";
        _logEntries.Clear();

        var progress = new Progress<(double Percent, string Status)>(t => StatusLine.Text = t.Status);
        var log = new Progress<string>(AppendLog);

        try
        {
            var installedSha = await _service.Patch(_settings.MacroQuestFolder, _remoteSha, progress, log, _cts.Token);
            if (!string.IsNullOrEmpty(installedSha))
            {
                _settings.InstalledSha = installedSha;
                _settings.InstalledVersion = _remoteVersion;
                _settings.Save();
            }
            StatusLine.Text = "Done. Reload on each box: /lua run Turbo  and  /lua run turbogear";
        }
        catch (OperationCanceledException) { AppendLog("Cancelled."); }
        catch (Exception ex) { AppendLog($"[error] {ex.Message}"); }
        finally
        {
            _busy = false;
            await RefreshAsync();
        }
    }
}
