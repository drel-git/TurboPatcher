using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;

namespace TurboPatcher;

// Downloads the latest Turbo source from GitHub and copies it into the player's
// MacroQuest folder. Uses only the .NET base class library (HttpClient +
// ZipFile + System.Text.Json) - no LibGit2Sharp / native deps - so the
// self-contained single-file build stays small and simple.
public class PatcherService
{
    private const string Owner  = "drel-git";
    private const string Repo   = "Turbo";      // public release mirror (dev happens in the private repo)
    private const string Branch = "main";
    private const string PatcherRepo = "TurboPatcher";

    private static string ZipUrl       => $"https://github.com/{Owner}/{Repo}/archive/refs/heads/{Branch}.zip";
    private static string CommitsApi   => $"https://api.github.com/repos/{Owner}/{Repo}/commits";
    private static string ChangelogUrl => $"https://raw.githubusercontent.com/{Owner}/{Repo}/{Branch}/lua/turbogear/CHANGELOG";
    private static string PatcherLatestApi =>
        $"https://api.github.com/repos/{Owner}/{PatcherRepo}/releases/latest";

    public const string PatcherDownloadUrlWindows =
        "https://github.com/drel-git/TurboPatcher/releases/latest/download/TurboPatcher.exe";

    public const string PatcherDownloadUrlLinux =
        "https://github.com/drel-git/TurboPatcher/releases/latest/download/TurboPatcher-linux-x64";

    /// <summary>Stable download link for this OS (Windows GUI exe or Linux CLI).</summary>
    public static string PatcherDownloadUrl =>
        OperatingSystem.IsWindows() ? PatcherDownloadUrlWindows : PatcherDownloadUrlLinux;

    // Repo-root subtrees copied into the MacroQuest folder (program files).
    private static readonly string[] SyncDirs = { "lua", "Macros" };
    // config/ is copied too, but NEVER overwrites an existing file, so a player's
    // edited .ini is preserved while a fresh install still gets the defaults.
    private const string ConfigDir = "config";
    // Dev-only subtrees that players don't need (relative to each SyncDir root).
    private static readonly string[] SkipRelPrefixes = { Path.Combine("lua", "tests") + Path.DirectorySeparatorChar };
    // Keep this many timestamped backup folders; older ones are pruned.
    private const int BackupsToKeep = 5;

    public const string InstallStampFileName = "turbo_install.json";

    private static HttpClient NewHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        h.DefaultRequestHeaders.Add("User-Agent", "TurboPatcher");
        h.DefaultRequestHeaders.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
        {
            NoCache = true,
            NoStore = true,
        };
        return h;
    }

    public static string InstallStampPath(string mqFolder) =>
        Path.Combine(mqFolder, "config", InstallStampFileName);

    /// <summary>SHA + version recorded in MQ config after a successful Patch.</summary>
    public static (string Sha, string Version) ReadInstallStamp(string? mqFolder)
    {
        if (string.IsNullOrWhiteSpace(mqFolder)) return ("", "");
        try
        {
            var path = InstallStampPath(mqFolder);
            if (!File.Exists(path)) return ("", "");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var sha = root.TryGetProperty("sha", out var s) ? (s.GetString() ?? "") : "";
            var version = root.TryGetProperty("version", out var v) ? (v.GetString() ?? "") : "";
            // Older patchers accidentally stamped the full CHANGELOG into version.
            return (sha.Trim(), NormalizeVersionStamp(version));
        }
        catch { return ("", ""); }
    }

    public static void WriteInstallStamp(string mqFolder, string sha, string version)
    {
        try
        {
            // Guard against callers accidentally passing full CHANGELOG text.
            version = NormalizeVersionStamp(version);
            var configFolder = Path.Combine(mqFolder, "config");
            Directory.CreateDirectory(configFolder);
            var payload = new Dictionary<string, object?>
            {
                ["sha"] = sha ?? "",
                ["version"] = version ?? "",
                ["installedAt"] = DateTimeOffset.Now.ToString("o"),
            };
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(InstallStampPath(mqFolder), json);
        }
        catch { /* stamp is best-effort */ }
    }

    /// <summary>First x.y / x.y.z line from the installed CHANGELOG, if present.</summary>
    public static string ReadLocalChangelogVersion(string? mqFolder)
    {
        if (string.IsNullOrWhiteSpace(mqFolder)) return "";
        try
        {
            var path = Path.Combine(mqFolder, "lua", "turbogear", "CHANGELOG");
            if (!File.Exists(path)) return "";
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (System.Text.RegularExpressions.Regex.IsMatch(line, @"^\d+\.\d+"))
                    return line;
            }
        }
        catch { }
        return "";
    }

    /// <summary>
    /// Prefer MQ install stamp, then local CHANGELOG version (sha empty), then AppData.
    /// </summary>
    public static (string Sha, string Version) ResolveInstalledSuite(
        string? mqFolder, string appDataSha, string appDataVersion)
    {
        var (stampSha, stampVer) = ReadInstallStamp(mqFolder);
        if (!string.IsNullOrEmpty(stampSha))
        {
            // Disk CHANGELOG is ground truth after a patch; stamp version can be stale
            // or (older bug) the entire notes blob normalized to the wrong first line.
            var localVer = ReadLocalChangelogVersion(mqFolder);
            return (stampSha, !string.IsNullOrEmpty(localVer) ? localVer : stampVer);
        }

        var changelogVer = ReadLocalChangelogVersion(mqFolder);
        if (!string.IsNullOrEmpty(changelogVer))
        {
            // Have files on disk but no stamp (manual zip / wiped AppData): treat as installed
            // by version only; SHA compare will still show "update available" until next Patch
            // writes a stamp - unless AppData SHA matches remote.
            if (!string.IsNullOrEmpty(appDataSha))
                return (appDataSha, !string.IsNullOrEmpty(appDataVersion) ? appDataVersion : changelogVer);
            return ("", changelogVer);
        }

        return (appDataSha ?? "", appDataVersion ?? "");
    }

    // ---- first-run MacroQuest folder detection --------------------------------
    public static bool IsMqFolder(string? dir) =>
        !string.IsNullOrEmpty(dir)
        && Directory.Exists(Path.Combine(dir!, "lua"))
        && Directory.Exists(Path.Combine(dir!, "config"));

    // Best-effort: a running MacroQuest process tells us exactly where it lives;
    // otherwise do a shallow scan of drive roots for well-known folder names.
    public static string? DetectMacroQuestFolder()
    {
        foreach (var name in new[] { "MacroQuest", "MacroQuest64", "MacroQuest2" })
        {
            foreach (var p in System.Diagnostics.Process.GetProcessesByName(name))
            {
                try
                {
                    var dir = Path.GetDirectoryName(p.MainModule?.FileName);
                    if (IsMqFolder(dir)) return dir;
                    var parent = dir is null ? null : Directory.GetParent(dir)?.FullName;
                    if (IsMqFolder(parent)) return parent;
                }
                catch { /* different bitness / access denied - keep looking */ }
            }
        }
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                foreach (var dir in Directory.EnumerateDirectories(drive.RootDirectory.FullName))
                {
                    var name = Path.GetFileName(dir);
                    if ((name.Contains("MacroQuest", StringComparison.OrdinalIgnoreCase)
                         || name.Contains("E3Next", StringComparison.OrdinalIgnoreCase)
                         || name.Contains("MQNext", StringComparison.OrdinalIgnoreCase))
                        && IsMqFolder(dir))
                        return dir;
                }
            }
        }
        catch { }
        return null;
    }

    // ---- version check + changelog -------------------------------------------
    private static List<(string Sha, string Date, string Message)> FetchCommits(int perPage)
    {
        using var http = NewHttp();
        http.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");
        var json = http.GetStringAsync($"{CommitsApi}?sha={Branch}&per_page={perPage}").GetAwaiter().GetResult();
        using var doc = JsonDocument.Parse(json);
        var list = new List<(string, string, string)>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            var sha = e.GetProperty("sha").GetString() ?? "";
            var commit = e.GetProperty("commit");
            var date = commit.GetProperty("author").GetProperty("date").GetString() ?? "";
            var msg = commit.GetProperty("message").GetString()?.TrimEnd() ?? "";
            if (DateTimeOffset.TryParse(date, out var dt)) date = dt.LocalDateTime.ToString("yyyy-MM-dd");
            var firstLine = msg.Split('\n')[0].Trim();
            list.Add((sha, date, firstLine));
        }
        return list;
    }

    private static string BuildLog(IEnumerable<(string Sha, string Date, string Message)> commits)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var (_, date, msg) in commits)
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append($"[{date}] {msg}");
        }
        return sb.ToString();
    }

    // The repo's CHANGELOG (lua/turbogear/CHANGELOG) makes far friendlier patch
    // notes than raw commit subjects. Its first line is the release version
    // (e.g. "1.1.0"). Returns ("", "") when the file is absent/unreachable.
    private static (string Version, string Notes) FetchChangelog()
    {
        try
        {
            using var http = NewHttp();
            var text = http.GetStringAsync(ChangelogUrl).GetAwaiter().GetResult().Trim();
            if (text.Length == 0) return ("", "");
            var firstLine = text.Split('\n')[0].Trim();
            var version = System.Text.RegularExpressions.Regex.IsMatch(firstLine, @"^\d+\.\d+")
                ? firstLine : "";
            return (version, AsciiSafe(text));
        }
        catch { return ("", ""); }
    }

    /// <summary>Replace fancy dashes/arrows so UI fonts never show odd glyphs.</summary>
    public static string AsciiSafe(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text
            .Replace('\u2014', '-') // em dash
            .Replace('\u2013', '-') // en dash
            .Replace('\u2212', '-') // minus
            .Replace("\u2192", "->"); // unicode arrow
    }

    // Returns whether the remote head differs from the installed suite, the remote
    // SHA + human version (when the CHANGELOG exposes one), and patch notes.
    // Prefer mqFolder install stamp / local CHANGELOG over AppData alone.
    public (bool HasNew, string RemoteSha, string RemoteVersion, string Log, string InstalledSha, string InstalledVersion)
        CheckForUpdate(string? mqFolder, string appDataSha = "", string appDataVersion = "")
    {
        var (installedSha, installedVersion) = ResolveInstalledSuite(mqFolder, appDataSha, appDataVersion);
        List<(string Sha, string Date, string Message)> commits;
        try { commits = FetchCommits(15); }
        catch
        {
            return (true, "", "", "Could not reach the Turbo repository (check your connection).",
                installedSha, installedVersion);
        }
        if (commits.Count == 0)
            return (true, "", "", "No commits found.", installedSha, installedVersion);
        var remote = commits[0].Sha;
        var (version, notes) = FetchChangelog();
        var instVer = ParseVersion(installedVersion);
        var remVer = ParseVersion(version);
        bool shaDiffers = !string.IsNullOrEmpty(installedSha)
            && !string.Equals(remote, installedSha, StringComparison.OrdinalIgnoreCase);
        bool versionNewer = instVer is not null && remVer is not null && remVer > instVer;

        // Unknown install -> offer Install. Otherwise update if tip SHA moved OR
        // remote CHANGELOG version is greater (covers stamp/version skew).
        bool hasNew = string.IsNullOrEmpty(installedSha) && string.IsNullOrEmpty(installedVersion)
            || shaDiffers
            || versionNewer;

        return (hasNew, remote, version, notes.Length > 0 ? notes : BuildLog(commits),
            installedSha, installedVersion);
    }

    /// <summary>This running patcher's Assembly version as major.minor.build.</summary>
    public static string LocalPatcherVersionString()
    {
        var ver = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version
                  ?? typeof(PatcherService).Assembly.GetName().Version;
        if (ver is null) return "";
        return $"{ver.Major}.{ver.Minor}.{ver.Build}";
    }

    /// <summary>First line only, stripped of a leading v - never full CHANGELOG text.</summary>
    private static string NormalizeVersionStamp(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var line = text.Replace("\r\n", "\n").Split('\n')[0].Trim();
        if (line.StartsWith('v') || line.StartsWith('V')) line = line[1..].Trim();
        return System.Text.RegularExpressions.Regex.IsMatch(line, @"^\d+\.\d+") ? line : "";
    }

    private static Version? ParseVersion(string? text)
    {
        text = NormalizeVersionStamp(text);
        if (string.IsNullOrWhiteSpace(text)) return null;
        // Accept "1.0.3" or "1.0.3+meta"; ignore revision for comparison.
        var core = text.Split('-', '+')[0];
        return Version.TryParse(core, out var v) ? v : null;
    }

    /// <summary>
    /// Compare this exe to GitHub's latest TurboPatcher release. Soft-fails
    /// (UpdateAvailable=false) when the network/API is unreachable so suite
    /// updates are never blocked.
    /// </summary>
    public (bool UpdateAvailable, string LocalVersion, string LatestVersion, string DownloadUrl)
        CheckSelfUpdate()
    {
        var localStr = LocalPatcherVersionString();
        var local = ParseVersion(localStr);
        try
        {
            using var http = NewHttp();
            http.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");
            var json = http.GetStringAsync(PatcherLatestApi).GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(json);
            var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
            var latest = ParseVersion(tag);
            var latestStr = latest is null ? tag.TrimStart('v', 'V') : $"{latest.Major}.{latest.Minor}.{latest.Build}";
            var url = PatcherDownloadUrl;
            if (local is null || latest is null)
                return (false, localStr, latestStr, url);
            // Compare major.minor.build only (Assembly often has Revision=0).
            var localCmp = new Version(local.Major, local.Minor, Math.Max(local.Build, 0));
            var latestCmp = new Version(latest.Major, latest.Minor, Math.Max(latest.Build, 0));
            return (latestCmp > localCmp, localStr, latestStr, url);
        }
        catch
        {
            return (false, localStr, "", PatcherDownloadUrl);
        }
    }

    // ---- patch / install -----------------------------------------------------
    // Returns the remote SHA that was installed (caller persists it).
    public async Task<string> Patch(string mqFolder, string knownRemoteSha,
        IProgress<(double Percent, string Status)> progress, IProgress<string> log, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(mqFolder) || !Directory.Exists(mqFolder))
            throw new InvalidOperationException("Select your MacroQuest folder first.");
        if (!Directory.Exists(Path.Combine(mqFolder, "lua")))
            throw new InvalidOperationException("That folder has no 'lua' subfolder - pick your MacroQuest root.");

        void Report(double p, string m) { progress.Report((p, m)); log.Report(m); }

        var configFolder = Path.Combine(mqFolder, "config");
        Directory.CreateDirectory(configFolder);
        var lockFile = Path.Combine(configFolder, "turbo_patch.lock");
        var tempRoot = Path.Combine(Path.GetTempPath(), "TurboPatcher_" + Guid.NewGuid().ToString("N"));
        var remoteSha = knownRemoteSha;

        try
        {
            // 1. tell any running Turbo (on every box - shared config dir) to stop
            Report(0.05, "Signalling running Turbo to stop (patch lock)...");
            await File.WriteAllTextAsync(lockFile, DateTimeOffset.Now.ToString("o"), ct);
            log.Report("Note: only Turbo builds with the patch-lock hook self-stop; older versions keep "
                + "running (use /tgear stop) but files still update safely.");
            await Task.Delay(4000, ct);

            // 2. resolve the SHA we are about to install (for the version record)
            if (string.IsNullOrEmpty(remoteSha))
            {
                try { remoteSha = FetchCommits(1).FirstOrDefault().Sha ?? ""; } catch { }
            }

            // 3. download the branch source zip
            Report(0.15, "Downloading latest Turbo...");
            Directory.CreateDirectory(tempRoot);
            var zipPath = Path.Combine(tempRoot, "turbo.zip");
            using (var http = NewHttp())
            using (var resp = await http.GetAsync(ZipUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                await using var fs = File.Create(zipPath);
                await resp.Content.CopyToAsync(fs, ct);
            }

            // 4. extract (GitHub nests everything under "<Repo>-<branch>/")
            Report(0.5, "Extracting...");
            var extractDir = Path.Combine(tempRoot, "extract");
            ZipFile.ExtractToDirectory(zipPath, extractDir);
            var srcRoot = Directory.GetDirectories(extractDir).FirstOrDefault()
                          ?? throw new InvalidOperationException("Downloaded archive was empty.");

            // 5. back up + copy the program subtrees; preserve player config
            var backupDir = Path.Combine(configFolder, "TurboPatcher_backup", DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            Report(0.6, "Backing up and copying files...");
            int copied = 0;
            foreach (var dir in SyncDirs)
            {
                var srcDir = Path.Combine(srcRoot, dir);
                if (Directory.Exists(srcDir))
                    copied += CopyTree(srcDir, Path.Combine(mqFolder, dir), backupDir, dir, overwrite: true, ct);
            }
            var srcConfig = Path.Combine(srcRoot, ConfigDir);
            if (Directory.Exists(srcConfig))
                copied += CopyTree(srcConfig, configFolder, backupDir, ConfigDir, overwrite: false, ct);

            // BiS announce disk caches fingerprint catalog *shape* only; same-shape
            // content edits (ID chains, notes) can leave stale TurboGear_dcat_*.lua.
            // Safe to delete - they rebuild on next TurboGear load.
            ClearTurboGearDcatCaches(configFolder, log);

            // Prefer the CHANGELOG we just copied (authoritative for this install).
            // FetchChangelog returns (version, fullNotes) - use Item1 only, never the notes blob.
            var stampVersion = ReadLocalChangelogVersion(mqFolder);
            if (string.IsNullOrEmpty(stampVersion))
            {
                var (remoteVersion, _) = FetchChangelog();
                stampVersion = remoteVersion;
            }
            WriteInstallStamp(mqFolder, remoteSha, stampVersion);

            Report(1.0, $"Done - {copied} file(s) updated.");
            log.Report("Reload on each box: /lua run Turbo  and  /lua run turbogear");
            if (Directory.Exists(backupDir))
                log.Report($"Replaced files backed up to: {backupDir}");
            PruneBackups(Path.Combine(configFolder, "TurboPatcher_backup"), log);
            return remoteSha;
        }
        finally
        {
            // 6. always clear the lock so Turbo can run again, and clean up temp
            try { if (File.Exists(lockFile)) File.Delete(lockFile); } catch { }
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true); } catch { }
        }
    }

    /// <summary>
    /// Download the latest patcher binary to a temp folder. Caller swaps it in
    /// (Windows cannot overwrite a running exe).
    /// </summary>
    public async Task<string> DownloadLatestPatcherAsync(IProgress<string>? log, CancellationToken ct)
    {
        var dir = Path.Combine(Path.GetTempPath(), "TurboPatcher_update");
        Directory.CreateDirectory(dir);
        var fileName = OperatingSystem.IsWindows() ? "TurboPatcher.exe" : "TurboPatcher-linux-x64";
        var dest = Path.Combine(dir, fileName);
        log?.Report($"Downloading {PatcherDownloadUrl} ...");
        using var http = NewHttp();
        using var resp = await http.GetAsync(PatcherDownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using (var fs = File.Create(dest))
            await resp.Content.CopyToAsync(fs, ct);
        log?.Report($"Downloaded to {dest}");
        return dest;
    }

    /// <summary>
    /// Write a helper script that waits for <paramref name="pid"/> to exit, replaces
    /// <paramref name="targetExe"/> with <paramref name="newExe"/>, then relaunches.
    /// Returns the helper path (caller starts it then exits).
    /// </summary>
    /// <param name="relaunchArgs">Optional args after the exe (already escaped for cmd).</param>
    public static string WriteWindowsSelfUpdateHelper(
        int pid, string newExe, string targetExe, string? workingDir, string relaunchArgs = "")
    {
        var dir = Path.GetDirectoryName(newExe) ?? Path.GetTempPath();
        var helper = Path.Combine(dir, "turbo_patcher_self_update.cmd");
        workingDir ??= Path.GetDirectoryName(targetExe) ?? dir;
        var args = string.IsNullOrWhiteSpace(relaunchArgs) ? "" : " " + relaunchArgs.Trim();
        // Quiet wait/copy/relaunch. No pause on success; failure writes a log file.
        var failLog = Path.Combine(dir, "self_update_fail.txt");
        var content = $"""
            @echo off
            setlocal
            :wait
            tasklist /FI "PID eq {pid}" 2>NUL | find "{pid}" >NUL
            if not errorlevel 1 (
              ping -n 2 127.0.0.1 >NUL
              goto wait
            )
            ping -n 2 127.0.0.1 >NUL
            copy /Y "{newExe}" "{targetExe}" >NUL
            if errorlevel 1 (
              echo TurboPatcher self-update failed to replace the exe.> "{failLog}"
              exit /b 1
            )
            start "" /D "{workingDir}" "{targetExe}"{args}
            del "%~f0" >NUL 2>&1
            endlocal
            """;
        File.WriteAllText(helper, content);
        return helper;
    }

    /// <summary>
    /// Linux: download latest binary and replace this process's exe via rename dance.
    /// Returns true when replacement was staged (caller should Environment.Exit).
    /// </summary>
    public async Task ApplyLinuxSelfUpdateAsync(IProgress<string>? log, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux())
            throw new InvalidOperationException("Linux self-update only.");
        var current = Environment.ProcessPath
            ?? throw new InvalidOperationException("Could not resolve current executable path.");
        var downloaded = await DownloadLatestPatcherAsync(log, ct);
        var dir = Path.GetDirectoryName(current) ?? ".";
        var backup = current + ".old";
        var staged = Path.Combine(dir, Path.GetFileName(current) + ".new");
        File.Copy(downloaded, staged, overwrite: true);
        try { if (File.Exists(backup)) File.Delete(backup); } catch { }
        File.Move(current, backup);
        File.Move(staged, current);
        try
        {
            // Match typical publish permissions (executable).
            var mode = Convert.ToInt32("755", 8);
            File.SetUnixFileMode(current, (UnixFileMode)mode);
        }
        catch { }
        log?.Report($"Replaced {current}. Re-run TurboPatcher to use the new version.");
        try { File.Delete(backup); } catch { }
    }

    // Drop TurboGear BiS disk caches so catalog content changes take effect
    // after reload. Does not touch settings, lists, or other TurboGear_*.lua.
    private static void ClearTurboGearDcatCaches(string configFolder, IProgress<string> log)
    {
        try
        {
            if (!Directory.Exists(configFolder)) return;
            var files = Directory.GetFiles(configFolder, "TurboGear_dcat_*.lua");
            if (files.Length == 0) return;
            int removed = 0;
            foreach (var path in files)
            {
                try
                {
                    File.Delete(path);
                    removed++;
                }
                catch { /* best-effort per file */ }
            }
            if (removed > 0)
                log.Report($"Cleared {removed} TurboGear BiS disk cache file(s) (dcat).");
        }
        catch { /* cache clearing is best-effort */ }
    }

    // Backups grow one timestamped folder per update; keep only the newest few.
    private static void PruneBackups(string backupRoot, IProgress<string> log)
    {
        try
        {
            if (!Directory.Exists(backupRoot)) return;
            var old = Directory.GetDirectories(backupRoot)
                .OrderByDescending(d => d, StringComparer.Ordinal) // yyyyMMdd_HHmmss sorts lexically
                .Skip(BackupsToKeep)
                .ToList();
            foreach (var dir in old) Directory.Delete(dir, true);
            if (old.Count > 0) log.Report($"Pruned {old.Count} old backup folder(s).");
        }
        catch { /* pruning is best-effort */ }
    }

    // Recursively copy srcDir -> dstDir. Any destination file that gets
    // overwritten is first copied into backupRoot/relPrefix/... When overwrite
    // is false, existing destination files are left untouched (preserves edits).
    private static int CopyTree(string srcDir, string dstDir, string backupRoot, string relPrefix,
        bool overwrite, CancellationToken ct)
    {
        int n = 0;
        foreach (var src in Directory.EnumerateFiles(srcDir, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(srcDir, src);
            var fullRel = Path.Combine(relPrefix, rel);
            if (SkipRelPrefixes.Any(p => fullRel.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                continue;
            var dst = Path.Combine(dstDir, rel);
            if (File.Exists(dst))
            {
                if (!overwrite) continue;
                var bak = Path.Combine(backupRoot, relPrefix, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(bak)!);
                File.Copy(dst, bak, overwrite: true);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(src, dst, overwrite: true);
            n++;
        }
        return n;
    }
}
