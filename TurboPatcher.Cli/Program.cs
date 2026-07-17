using System.Diagnostics;
using TurboPatcher;

// Exit codes: 0 = ok / up to date, 1 = error, 2 = update available
const int ExitOk = 0;
const int ExitError = 1;
const int ExitUpdateAvailable = 2;

static void PrintHelp()
{
    var ver = PatcherService.LocalPatcherVersionString();
    Console.WriteLine($"Turbo Patcher CLI v{ver}");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  TurboPatcher check [--mq PATH]     Check suite + this patcher version");
    Console.WriteLine("  TurboPatcher update [--mq PATH]    Install / update Turbo into MQ folder");
    Console.WriteLine("  TurboPatcher self-check            Check only this patcher vs GitHub latest");
    Console.WriteLine("  TurboPatcher self-update           Download and replace this patcher binary");
    Console.WriteLine("  TurboPatcher --help");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  --mq PATH   MacroQuest root (folder containing lua/ and config/).");
    Console.WriteLine("              Required for update. For Lutris/Wine: Browse Files on the");
    Console.WriteLine("              game prefix and point at that MQ root.");
    Console.WriteLine();
    Console.WriteLine("Exit codes: 0 ok, 1 error, 2 update available");
}

static string? GetOption(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            return args[i + 1];
    }
    return null;
}

static string? ResolveMqFolder(string[] args, PatcherSettings settings)
{
    var fromArg = GetOption(args, "--mq");
    if (!string.IsNullOrWhiteSpace(fromArg)) return fromArg.Trim();
    if (!string.IsNullOrWhiteSpace(settings.MacroQuestFolder)) return settings.MacroQuestFolder;
    return PatcherService.DetectMacroQuestFolder();
}

var argv = Environment.GetCommandLineArgs().Skip(1).ToArray();
if (argv.Length == 0 || argv.Any(a => a is "-h" or "--help" or "help" or "/?"))
{
    PrintHelp();
    return ExitOk;
}

var command = argv[0].ToLowerInvariant();
var service = new PatcherService();
var settings = PatcherSettings.Load();

try
{
    switch (command)
    {
        case "self-check":
        case "selfcheck":
        {
            var self = service.CheckSelfUpdate();
            Console.WriteLine($"Patcher local:  v{self.LocalVersion}");
            if (string.IsNullOrEmpty(self.LatestVersion))
            {
                Console.WriteLine("Patcher latest: (could not reach GitHub)");
                return ExitOk;
            }
            Console.WriteLine($"Patcher latest: v{self.LatestVersion}");
            if (self.UpdateAvailable)
            {
                Console.WriteLine($"Update available. Run: TurboPatcher self-update");
                Console.WriteLine($"Or download: {self.DownloadUrl}");
                return ExitUpdateAvailable;
            }
            Console.WriteLine("Patcher is up to date.");
            return ExitOk;
        }

        case "self-update":
        case "selfupdate":
        {
            var self = service.CheckSelfUpdate();
            if (!self.UpdateAvailable)
            {
                if (string.IsNullOrEmpty(self.LatestVersion))
                {
                    Console.Error.WriteLine("Could not reach GitHub to check for patcher updates.");
                    return ExitError;
                }
                Console.WriteLine($"Already up to date (v{self.LocalVersion}).");
                return ExitOk;
            }
            Console.WriteLine($"Updating patcher v{self.LocalVersion} -> v{self.LatestVersion}...");
            var log = new Progress<string>(Console.WriteLine);
            if (OperatingSystem.IsLinux())
            {
                await service.ApplyLinuxSelfUpdateAsync(log, CancellationToken.None);
                Console.WriteLine("Done. Re-run TurboPatcher.");
                return ExitOk;
            }
            if (OperatingSystem.IsWindows())
            {
                var downloaded = await service.DownloadLatestPatcherAsync(log, CancellationToken.None);
                var target = Environment.ProcessPath
                    ?? throw new InvalidOperationException("Could not resolve current executable path.");
                var helper = PatcherService.WriteWindowsSelfUpdateHelper(
                    Environment.ProcessId, downloaded, target, Path.GetDirectoryName(target));
                Process.Start(new ProcessStartInfo
                {
                    FileName = helper,
                    UseShellExecute = true,
                    CreateNoWindow = true,
                });
                Console.WriteLine("Restarting into the new patcher...");
                // Must exit so the helper can replace this binary.
                Environment.Exit(ExitOk);
            }
            Console.Error.WriteLine($"Unsupported OS for self-update. Download: {self.DownloadUrl}");
            return ExitError;
        }

        case "check":
        {
            var self = service.CheckSelfUpdate();
            if (self.UpdateAvailable)
                Console.WriteLine($"Patcher update available: v{self.LocalVersion} -> v{self.LatestVersion} (run: self-update)");
            else if (!string.IsNullOrEmpty(self.LatestVersion))
                Console.WriteLine($"Patcher v{self.LocalVersion} (up to date)");
            else
                Console.WriteLine($"Patcher v{self.LocalVersion}");

            var mq = ResolveMqFolder(argv, settings);
            if (string.IsNullOrWhiteSpace(mq) || !PatcherService.IsMqFolder(mq))
            {
                Console.WriteLine("Suite check skipped: pass --mq PATH to your MacroQuest root.");
                return self.UpdateAvailable ? ExitUpdateAvailable : ExitOk;
            }

            settings.MacroQuestFolder = mq;
            settings.Save();
            var (hasNew, remoteSha, remoteVersion, log, installedSha, installedVersion) =
                service.CheckForUpdate(mq, settings.InstalledSha, settings.InstalledVersion);
            if (!string.IsNullOrEmpty(installedSha))
            {
                settings.InstalledSha = installedSha;
                settings.InstalledVersion = installedVersion;
                settings.Save();
            }
            var shortSha = string.IsNullOrEmpty(remoteSha) ? "?" : (remoteSha.Length >= 7 ? remoteSha[..7] : remoteSha);
            var avail = string.IsNullOrEmpty(remoteVersion) ? shortSha : $"v{remoteVersion} ({shortSha})";
            if (string.IsNullOrEmpty(installedSha) && string.IsNullOrEmpty(installedVersion))
                Console.WriteLine($"Suite: not installed · Available: {avail}");
            else
            {
                var instShort = string.IsNullOrEmpty(installedSha)
                    ? "no-sha"
                    : (installedSha.Length >= 7 ? installedSha[..7] : installedSha);
                var inst = string.IsNullOrEmpty(installedVersion)
                    ? instShort
                    : $"v{installedVersion} ({instShort})";
                Console.WriteLine($"Suite installed: {inst} · Available: {avail} · {(hasNew ? "update available" : "up to date")}");
            }
            if (!string.IsNullOrWhiteSpace(log))
            {
                Console.WriteLine();
                Console.WriteLine("--- What's New ---");
                Console.WriteLine(log);
            }
            if (self.UpdateAvailable)
                Console.WriteLine($"\nPatcher download: {self.DownloadUrl}");
            return (hasNew || self.UpdateAvailable) ? ExitUpdateAvailable : ExitOk;
        }

        case "update":
        case "install":
        case "patch":
        {
            var self = service.CheckSelfUpdate();
            if (self.UpdateAvailable)
            {
                Console.WriteLine($"Note: this patcher is behind (v{self.LocalVersion} -> v{self.LatestVersion}).");
                Console.WriteLine("Run: TurboPatcher self-update   (or download from the URL below)");
                Console.WriteLine($"Download: {self.DownloadUrl}");
                Console.WriteLine("Continuing with suite update...");
            }

            var mq = ResolveMqFolder(argv, settings);
            if (string.IsNullOrWhiteSpace(mq) || !PatcherService.IsMqFolder(mq))
            {
                Console.Error.WriteLine("Error: MacroQuest folder required. Use --mq PATH");
                Console.Error.WriteLine("(Must contain lua/ and config/. Lutris: Wine prefix -> Browse Files -> MQ root.)");
                return ExitError;
            }

            settings.MacroQuestFolder = mq;
            settings.Save();

            var check = service.CheckForUpdate(mq, settings.InstalledSha, settings.InstalledVersion);
            var progress = new Progress<(double Percent, string Status)>(t =>
                Console.WriteLine($"[{t.Percent * 100:0}%] {t.Status}"));
            var log = new Progress<string>(Console.WriteLine);

            var sha = await service.Patch(mq, check.RemoteSha, progress, log, CancellationToken.None);
            if (!string.IsNullOrEmpty(sha))
            {
                settings.InstalledSha = sha;
                settings.InstalledVersion = check.RemoteVersion;
                settings.Save();
            }
            Console.WriteLine("Done. Reload on each box: /lua run Turbo  and  /lua run turbogear");
            return ExitOk;
        }

        default:
            Console.Error.WriteLine($"Unknown command: {command}");
            PrintHelp();
            return ExitError;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[error] {ex.Message}");
    return ExitError;
}
