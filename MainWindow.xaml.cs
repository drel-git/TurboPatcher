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
    private bool _busy;

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
        var ver = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
        VersionText.Text = ver is { Major: > 0 } ? $"Patcher v{ver.Major}.{ver.Minor}.{ver.Build}" : "";
        _ = RefreshAsync();
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
        var installed = _settings.InstalledSha;
        var (hasNew, remoteSha, remoteVersion, log) = await Task.Run(() => _service.CheckForUpdate(installed));
        _remoteSha = remoteSha;
        _remoteVersion = remoteVersion;
        PatchNotesText.Text = log;
        StatusLine.Text = string.IsNullOrEmpty(installed)
            ? $"Not installed yet · Available: {Pretty(remoteVersion, remoteSha)}"
            : $"Installed: {Pretty(_settings.InstalledVersion, installed)} · Available: {Pretty(remoteVersion, remoteSha)} · {(hasNew ? "update available" : "up to date")}";
        ActionButton.Content = string.IsNullOrEmpty(installed) ? "Install"
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
