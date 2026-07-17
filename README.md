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

### Linux / Lutris (CLI)

MacroQuest under Lutris lives in a Wine prefix. Download the CLI binary, make it
executable, and point `--mq` at the MQ root inside that prefix (the folder that
contains `lua/` and `config/` — Lutris → right-click game → **Run EXE inside
Wine prefix** / **Browse files**):

```bash
chmod +x TurboPatcher-linux-x64
./TurboPatcher-linux-x64 update --mq "/path/to/your/MQ/root"
./TurboPatcher-linux-x64 check --mq "/path/to/your/MQ/root"
```

## What it does

1. On first run (Windows), auto-detects the MacroQuest folder when possible.
2. Shows patch notes from `lua/turbogear/CHANGELOG` (falling back to commit
   history) and installed vs available suite version.
3. Checks GitHub for a newer **patcher** than the one you are running
   (Windows banner + Download; CLI prints a note). Soft-fails offline; suite
   updates still work on an older patcher.
4. On **Install / Update**:
   - drops `config/turbo_patch.lock` so any running Turbo **self-stops on every
     box** (shared config dir; see the patch-lock hook in TurboGear);
   - downloads the latest `main` source zip from GitHub;
   - backs up replaced files to `config/TurboPatcher_backup/<timestamp>/`
     (newest 5 kept);
   - copies `lua/` and `Macros/` (overwrite) and `config/` **without overwriting**
     existing files;
   - clears `config/TurboGear_dcat_*.lua` BiS disk caches;
   - leaves other runtime `config/` data alone;
   - records the installed commit SHA in app settings
     (`%AppData%\TurboPatcher\settings.json` on Windows,
     `~/.config/TurboPatcher/settings.json` on Linux).
5. Reload Turbo in-game (`/lua run turbogear`).

## Configuration

Edit the constants at the top of `TurboPatcher.Core/PatcherService.cs` if you
fork the repo. The patcher reads the **public mirror** `drel-git/Turbo`.

## Build

Pushing a `v*` tag runs CI and attaches:

- `TurboPatcher.exe` — Windows GUI (`win-x64`)
- `TurboPatcher-linux-x64` — Linux CLI (`linux-x64`)

Local:

```bash
# Windows GUI
dotnet publish TurboPatcher.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true

# Linux CLI (from Windows or Linux)
dotnet publish TurboPatcher.Cli/TurboPatcher.Cli.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true
# rename publish output TurboPatcher -> TurboPatcher-linux-x64 for distribution
```

## Notes

- Core logic is shared (`TurboPatcher.Core`); no third-party packages.
- Unsigned Windows exes can trigger SmartScreen on first run.
- The patch-lock shutdown is handled by TurboGear when it is running.

Loot up.
