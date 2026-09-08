# src/ — game art

Art used by the RBF Launcher. Copied next to `RbfLauncher.exe` as `src\` at
build time (`launcher/RbfLauncher/RbfLauncher.csproj`).

Two images per game (referenced by `GameCatalog.All` in
`launcher/RbfLauncher/Core/Model.cs`):

| game | thumbnail (library grid, ~4:3) | portrait (room screen) |
|------|-------------------------------|------------------------|
| Vampire Savior (`vsav`)      | `vampire.png` | `vsavbox.png` / `.jpg` |
| KOF '98 (`kof98`)            | `kof98.png`   | `kof98box.png` / `.jpg` |
| SF Alpha 2 (`sfa2`)          | `sfa2.jpg`    | `sfa2box.png` / `.jpg` |
| SF II' CE (`sf2ce`)          | `sf2ce.png`   | `sf2cebox.png` / `.jpg` |

- **Thumbnail** — an in-game screenshot, roughly 320×240 / 4:3. Shown whole (no
  crop) on a dark mat in the game grid.
- **Portrait (`<short>box`)** — a tall image for the room screen. Extension is
  auto-resolved (`.png`, `.jpg`, `.jpeg`, `.bmp`, `.gif`), so just name it
  `vsavbox.png` or `vsavbox.jpg`. Shown whole (no crop). If absent, the room
  screen falls back to the thumbnail, then to a plain placeholder.

Any format WPF decodes works.
