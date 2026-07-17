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

    /// <summary>Stable download link; always the newest published TurboPatcher.exe.</summary>
    public const string PatcherDownloadUrl =
        "https://github.com/drel-git/TurboPatcher/releases/latest/download/TurboPatcher.exe";

    // Repo-root subtrees copied into the MacroQuest folder (program files).
    private static readonly string[] SyncDirs = { "lua", "Macros" };
    // config/ is copied too, but NEVER overwrites an existing file, so a player's
    // edited .ini is preserved while a fresh install still gets the defaults.
    private const string ConfigDir = "config";
    // Dev-only subtrees that players don't need (relative to each SyncDir root).
    private static readonly string[] SkipRelPrefixes = { Path.Combine("lua", "tests") + Path.DirectorySeparatorChar };
    // Keep this many timestamped backup folders; older ones are pruned.
    private const int BackupsToKeep = 5;

    private static HttpClient NewHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        h.DefaultRequestHeaders.Add("User-Agent", "TurboPatcher");
        return h;
    }

    // ---- first-run MacroQuest folder detection --------------------------------
    private static bool IsMqFolder(string? dir) =>
        !string.IsNullOrEmpty(dir)
        && Directory.Exists(Path.Combine(dir, "lua"))
        && Directory.Exists(Path.Combine(dir, "config"));

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
            return (version, text);
        }
        catch { return ("", ""); }
    }

    // Returns whether the remote head differs from the installed SHA, the remote
    // SHA + human version (when the CHANGELOG exposes one), and patch notes.
    public (bool HasNew, string RemoteSha, string RemoteVersion, string Log) CheckForUpdate(string installedSha)
    {
        List<(string Sha, string Date, string Message)> commits;
        try { commits = FetchCommits(15); }
        catch { return (true, "", "", "Could not reach the Turbo repository (check your connection)."); }
        if (commits.Count == 0) return (true, "", "", "No commits found.");
        var remote = commits[0].Sha;
        bool hasNew = !string.Equals(remote, installedSha, StringComparison.OrdinalIgnoreCase);
        var (version, notes) = FetchChangelog();
        return (hasNew, remote, version, notes.Length > 0 ? notes : BuildLog(commits));
    }

    /// <summary>This running patcher's Assembly version as major.minor.build.</summary>
    public static string LocalPatcherVersionString()
    {
        var ver = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version
                  ?? typeof(PatcherService).Assembly.GetName().Version;
        if (ver is null) return "";
        return $"{ver.Major}.{ver.Minor}.{ver.Build}";
    }

    private static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];
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
            if (local is null || latest is null)
                return (false, localStr, latestStr, PatcherDownloadUrl);
            // Compare major.minor.build only (Assembly often has Revision=0).
            var localCmp = new Version(local.Major, local.Minor, Math.Max(local.Build, 0));
            var latestCmp = new Version(latest.Major, latest.Minor, Math.Max(latest.Build, 0));
            return (latestCmp > localCmp, localStr, latestStr, PatcherDownloadUrl);
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

            Report(1.0, $"Done - {copied} file(s) updated. Reload Turbo in-game (/lua run turbogear).");
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
