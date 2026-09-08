# src/ — game art

Art used by the RBF Launcher. Copied next to `RbfLauncher.exe` as `src\` at
build time (`launcher/RbfLauncher/RbfLauncher.csproj`).

Naming is standardised on the ROM short name (`GameCatalog.All` in
`launcher/RbfLauncher/Core/Model.cs` only lists short name + title):

| game | thumbnail (library grid, ~4:3) | portrait (room screen) |
|------|-------------------------------|------------------------|
| Vampire Savior (`vsav`)      | `vsav.*`  | `vsavbox.*`  |
| KOF '98 (`kof98`)            | `kof98.*` | `kof98box.*` |
| SF Alpha 2 (`sfa2`)          | `sfa2.*`  | `sfa2box.*`  |
| SF II' CE (`sf2ce`)          | `sf2ce.*` | `sf2cebox.*` |

- **`<short>.*`** — an in-game screenshot, roughly 320×240 / 4:3. Shown whole
  (no crop) on a dark mat in the game grid.
- **`<short>box.*`** — a tall image for the room screen. Shown whole (no crop);
  if absent, the room screen falls back to the thumbnail, then a placeholder.

Extension is auto-resolved (`.png`, `.jpg`, `.jpeg`, `.bmp`, `.gif`) — name each
file whatever you like as long as the base matches.

Any format WPF decodes works.
