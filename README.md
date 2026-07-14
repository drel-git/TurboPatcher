# Turbo Patcher

A small Windows installer/updater for the **Turbo suite** (TurboGear, TurboLoot,
TurboGive, TurboMobs, TurboEtc) on MacroQuest.

Players download one self-contained `TurboPatcher.exe`, point it at their
MacroQuest folder, and click **Install / Update**. It works for both first-time
installs and updates.

## What it does

1. On first run, auto-detects the MacroQuest folder (from a running MacroQuest
   process, or a shallow scan of drive roots for MacroQuest/E3Next folders).
2. Shows patch notes from `lua/turbogear/CHANGELOG` (falling back to commit
   history) and `Installed: v<version> (<sha>) · Available: v<version> (<sha>)`.
3. On **Install / Update**:
   - drops `config/turbo_patch.lock` so any running Turbo **self-stops on every
     box** (the config dir is shared, and TurboGear watches for this file - see
     the patch-lock hook in the addon);
   - downloads the latest `main` source zip from GitHub;
   - backs up any files it will replace to `config/TurboPatcher_backup/<timestamp>/`
     (the newest 5 backups are kept, older ones pruned);
   - copies `lua/` and `Macros/` into the MacroQuest folder (program files,
     minus dev-only `lua/tests/`), and copies `config/` **without overwriting**
     existing files (so your edited `.ini`s are preserved);
   - **never touches** your runtime data in `config/` (`TurboGear_*.lua`, the
     `_cache.lua`/`.db`, shared settings, BiS/watch lists) - those aren't in the
     repo, so they're left alone;
   - clears the lock so Turbo can run again;
   - records the installed commit SHA in `%AppData%\TurboPatcher\settings.json`.
4. You reload Turbo in-game (`/lua run turbogear`).

## Configuration

Edit the constants at the top of `PatcherService.cs` if you fork the repo:

```csharp
private const string Owner  = "drel-git";
private const string Repo   = "Turbo";      // public release mirror
private const string Branch = "main";
```

The patcher reads the **public mirror** `drel-git/Turbo`.

## Build

CI builds it for you: pushing a `v*` tag runs `.github/workflows/release.yml`,
which publishes a self-contained single-file `win-x64` exe and attaches
`TurboPatcher.zip` to a GitHub Release. No local toolchain needed.

To build locally instead:

```
dotnet publish TurboPatcher.csproj -c Release -r win-x64 ^
  --self-contained true -p:PublishSingleFile=true
```

## Notes

- Uses only the .NET base class library (`HttpClient`, `ZipFile`,
  `System.Text.Json`) - no third-party packages.
- Unsigned single-file exes can trigger a SmartScreen/antivirus warning on first
  run; code-sign the exe (or document "More info → Run anyway") for wide
  distribution.
- The patch-lock shutdown is handled by TurboGear; if TurboGear isn't running,
  files are still replaced safely (Lua files aren't OS-locked) and picked up on
  the next `/lua run`.
