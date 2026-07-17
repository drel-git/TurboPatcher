## **Windows: [Click here to download TurboPatcher.exe and drop it in your E3Next/MQ folder.](https://github.com/drel-git/TurboPatcher/releases/latest/download/TurboPatcher.exe)**

## **Linux: [Click here to download the zip files and extract to your E3Next or MQ folder.](https://github.com/drel-git/Turbo)**

> Type `/lua run turbo` in game and you're ready to ride.

<p align="center">
  <img
    width="604"
    height="680"
    alt="Turbo Patcher"
    src="https://github.com/user-attachments/assets/29a45e2c-5392-4a1a-9e51-09d969e8df09"
  />
</p>

TurboPatcher is small Windows installer/updater for the **Turbo suite** (TurboGear, TurboLoot,
TurboGive, TurboMobs, TurboEtc) on MacroQuest.

Players download the self-contained `TurboPatcher.exe`, point it at their
MacroQuest folder, and click **Install / Update**. It works for both first-time
installs and updates.

## What it does

1. On first run, auto-detects the MacroQuest folder (from a running MacroQuest
   process, or a shallow scan of drive roots for MacroQuest/E3Next folders).
2. Shows patch notes from `lua/turbogear/CHANGELOG` (falling back to commit
   history) and `Installed: v<version> (<sha>) · Available: v<version> (<sha>)`.
3. On startup, checks GitHub for a newer **TurboPatcher.exe** than the one you
   are running. If behind, shows a banner with a **Download** button (suite
   updates still work on an older patcher). Soft-fails offline.
4. On **Install / Update**:
   - drops `config/turbo_patch.lock` so any running Turbo **self-stops on every
     box** (the config dir is shared, and TurboGear watches for this file - see
     the patch-lock hook in the addon);
   - downloads the latest `main` source zip from GitHub;
   - backs up any files it will replace to `config/TurboPatcher_backup/<timestamp>/`
     (the newest 5 backups are kept, older ones pruned);
   - copies `lua/` and `Macros/` into the MacroQuest folder and copies `config/` **without overwriting**
     existing files (so your edited `.ini`s and other configs are preserved);
   - clears `config/TurboGear_dcat_*.lua` BiS disk caches so catalog content
     updates apply after reload (settings, custom lists, and other `TurboGear_*.lua`
     runtime data are left alone);
   - **never touches** other runtime data in `config/` (shared settings, BiS/watch
     lists, `_cache.lua`/`.db`) - those aren't in the repo, so they're left alone;
   - clears the lock so Turbo can run again;
   - records the installed commit SHA in `%AppData%\TurboPatcher\settings.json`.
5. You reload Turbo in-game (`/lua run turbogear`).

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
`TurboPatcher.exe` directly to a GitHub Release, so the stable link
`https://github.com/drel-git/TurboPatcher/releases/latest/download/TurboPatcher.exe`
always serves the newest build. No local toolchain needed.

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

Loot up.
