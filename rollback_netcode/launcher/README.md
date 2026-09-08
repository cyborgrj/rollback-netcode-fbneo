# RBF Launcher

Desktop lobby for Rollback FinalBurn Neo. Lists the supported games, shows which
ROMs you have, downloads missing ones, and boots the **patched `fbneo.exe`** on a
chosen game.

It is a **separate app** from the emulator — it does not build with it. It just
spawns `fbneo.exe <shortname>` as a child process.

## Why C# / WPF on .NET Framework 4.8

`.NET Framework 4.8` ships inside every Windows 7 SP1 … 11, so **end users install
nothing** — no .NET download, no DirectX/VC++ redist. WPF renders in software when
there is no GPU (fine for VMs). Light enough for DDR2-era machines for a static
lobby.

## Build

Needs only the **.NET SDK** on your dev box (not the Framework Developer Pack, not
Visual Studio):

```bash
winget install Microsoft.DotNet.SDK.8
```

```bash
cd rollback_netcode/launcher
dotnet build  -c Release
dotnet run    --project RbfLauncher            # dev run
dotnet publish -c Release -o out               # -> out\RbfLauncher.exe + a few DLLs
```

`Microsoft.NETFramework.ReferenceAssemblies` (a NuGet package, pulled
automatically) is what lets the SDK target net48 with no Developer Pack.

## Runtime folder

```
RBF\
  RbfLauncher.exe
  *.dll                 (System.Text.Json + deps, ~1 MB)
  src\  vsav.png kof98.png sfa2.jpg sf2ce.png     (you provide the art)
  fbneo.exe             (copy the patched emulator build here)
  roms\arcade\*.zip     (you provide the ROMs, however you obtain them)
  json_roms\*.json      (optional; enables the in-app download button)
  rbf-launcher.json     (created on first run)
```

## Game art

Lives in the project-wide [`rollback_netcode/src/`](../src/) folder
(`vampire.png`, `kof98.png`, `sfa2.jpg`, `sf2ce.png`). The csproj copies it next
to the exe as `src\` on build. Missing art → a placeholder tile.

## Config (`rbf-launcher.json`, next to the exe)

```json
{
  "emulatorPath": "fbneo.exe",
  "emulatorArgs": "-w",
  "romsDir": "roms\\arcade",
  "playerName": "you"
}
```

Relative paths resolve against the launcher folder. `romsDir` defaults to
FBNeo's own arcade default; if you point it elsewhere you must also tell FBNeo
(future: pass a rom path / write `fbneo.ini`).

**`emulatorArgs`** is appended after the game name. FBNeo boots **fullscreen**
when given only a game name, which fails on some setups; the default `-w` forces
a window. FBNeo remembers the window size / blitter you pick from its own menu in
`config\fbneo.ini` next to `fbneo.exe`. For a low fullscreen mode instead, use
`-r 800 x 600 x 32`.

## ROMs

The launcher **carries no ROM URLs**. Put the ZIP sets in `romsDir`
(`roms\arcade` by default) however you obtain them.

Optionally, if one or more rom-database JSON files are present in `json_roms\`,
the room screen's **Baixar ROM** button becomes active: it resolves the set plus
any `require` dependencies and fetches each from the URL in that file (to
`<name>.zip.part`, then moved into place). Format read:

```json
{ "<setname>": { "download": "<url>", "require": ["<dep>", "..."] } }
```

No such file → the button stays disabled and the room screen says so. The repo
ships none and `json_roms\*.json` is git-ignored.

## Scope now / next

- **Now:** 4 games, ROM presence + download, launch emulator, settings.
- **Next:** the gRPC agent — player login, online rooms, "challenge" between
  machines, then a server + DB (local, later AWS).

Featured catalogue lives in `Core/Model.cs` (`GameCatalog.All`) — short name +
title + art file, no URLs. Add an entry + its art to feature another game.
