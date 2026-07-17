## **Windows: [Download TurboPatcher.exe](https://github.com/drel-git/TurboPatcher/releases/latest/download/TurboPatcher.exe)**

## **Linux / Lutris: [Download TurboPatcher-linux-x64](https://github.com/drel-git/TurboPatcher/releases/latest/download/TurboPatcher-linux-x64)**

> Type `/lua run turbo` in game and you're ready to ride.

<p align="center">
  <img
    width="604"
    height="680"
    alt="Turbo Patcher"
    src="https://github.com/user-attachments/assets/29a45e2c-5392-4a1a-9e51-09d969e8df09"
  />
</p>

TurboPatcher installs and updates the **Turbo suite** (TurboGear, TurboLoot,
TurboGive, TurboMobs, and more) into your MacroQuest folder.

### Windows (GUI)

Download the self-contained `TurboPatcher.exe`, point it at your MacroQuest
folder, and click **Install / Update**.

When a newer patcher is on GitHub, use **Update Patcher** - it downloads and
restarts into the new exe quietly (no console flash; browser download is only a
fallback). After relaunch, the gold **Update Now** button stays pinned at the
bottom of the window for the suite.

From in-game, the Turbo update banner / mini **Update** button launches:

`TurboPatcher.exe --mq "<your MQ root>" --update`

so the suite update starts automatically after the version check.

### Linux / Lutris (CLI)

MacroQuest under Lutris lives in a Wine prefix. Download the CLI binary, make it
executable, and point `--mq` at the MQ root inside that prefix (the folder that
contains `lua/` and `config/` - Lutris -> right-click game -> **Run EXE inside
Wine prefix** / **Browse files**):

```bash
chmod +x TurboPatcher-linux-x64
./TurboPatcher-linux-x64 update --mq "/path/to/your/MQ/root"
./TurboPatcher-linux-x64 check --mq "/path/to/your/MQ/root"
./TurboPatcher-linux-x64 self-update   # replace this CLI binary when behind
```

## Updating (smooth path)

1. **In game:** Turbo shows a banner when GitHub has a newer suite version
   (`More` -> optional "Check for Turbo updates"). Click **Update** or run
   `/turbopatcher`.
2. **Patcher:** If the patcher itself is behind, click **Update Patcher** first,
   then **Update Now** for the suite.
3. **Reload** on each box: `/lua run Turbo` and `/lua run turbogear`.

## What it does

1. On first run (Windows), auto-detects the MacroQuest folder when possible.
2. Shows patch notes from `lua/turbogear/CHANGELOG` (falling back to commit
   history) and installed vs available suite version.
3. Resolves “installed” from `config/turbo_install.json` (written after each
   successful patch), then local CHANGELOG, then AppData settings.
4. Checks GitHub for a newer **patcher** than the one you are running
   (Windows in-place self-update; CLI `self-update`). Soft-fails offline; suite
   updates still work on an older patcher.
5. On **Install / Update**:
   - drops `config/turbo_patch.lock` so any running Turbo **self-stops on every
     box** (shared config dir; see the patch-lock hook in TurboGear);
   - downloads the latest `main` source zip from GitHub;
   - backs up replaced files to `config/TurboPatcher_backup/<timestamp>/`
     (newest 5 kept);
   - copies `lua/` and `Macros/` (overwrite) and `config/` **without overwriting**
     existing files;
   - clears `config/TurboGear_dcat_*.lua` BiS disk caches;
   - writes `config/turbo_install.json` (sha + version);
   - also records the installed commit SHA in app settings
     (`%AppData%\TurboPatcher\settings.json` on Windows,
     `~/.config/TurboPatcher/settings.json` on Linux).
6. Reload Turbo in-game (`/lua run Turbo` and `/lua run turbogear`).

## Configuration

Edit the constants at the top of `TurboPatcher.Core/PatcherService.cs` if you
fork the repo. The patcher reads the **public mirror** `drel-git/Turbo`.

## Build

Pushing a `v*` tag runs CI and attaches:

- `TurboPatcher.exe` - Windows GUI (`win-x64`)
- `TurboPatcher-linux-x64` - Linux CLI (`linux-x64`)

Local:

```bash
# Windows GUI
dotnet publish TurboPatcher.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true

# Linux CLI (from Windows or Linux)
dotnet publish TurboPatcher.Cli/TurboPatcher.Cli.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true
# rename publish output TurboPatcher -> TurboPatcher-linux-x64 for distribution
```

## License

See the repository for license details.
