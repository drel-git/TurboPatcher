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
    private const string Repo   = "Turbo-v3.9.88";
    private const string Branch = "main";

    private static string ZipUrl     => $"https://github.com/{Owner}/{Repo}/archive/refs/heads/{Branch}.zip";
    private static string CommitsApi => $"https://api.github.com/repos/{Owner}/{Repo}/commits";

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

    // Returns whether the remote head differs from the installed SHA, the remote
    // SHA, and a short changelog to display.
    public (bool HasNew, string RemoteSha, string Log) CheckForUpdate(string installedSha)
    {
        List<(string Sha, string Date, string Message)> commits;
        try { commits = FetchCommits(15); }
        catch { return (true, "", "Could not reach the Turbo repository (check your connection)."); }
        if (commits.Count == 0) return (true, "", "No commits found.");
        var remote = commits[0].Sha;
        bool hasNew = !string.Equals(remote, installedSha, StringComparison.OrdinalIgnoreCase);
        return (hasNew, remote, BuildLog(commits));
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
            Report(0.05, "Signalling Turbo to stop...");
            await File.WriteAllTextAsync(lockFile, DateTimeOffset.Now.ToString("o"), ct);
            await Task.Delay(2500, ct);

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
