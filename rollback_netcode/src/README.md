# src/ — game art

Box art used by the RBF Launcher. Copied next to `RbfLauncher.exe` as `src\` at
build time (see `launcher/RbfLauncher/RbfLauncher.csproj`).

Expected names (referenced by `GameCatalog.All` in `launcher/RbfLauncher/Core/Model.cs`):

| file | game |
|------|------|
| `vampire.png` | Vampire Savior (`vsav`) |
| `kof98.png` | The King of Fighters '98 (`kof98`) |
| `sfa2.jpg` | Street Fighter Alpha 2 (`sfa2`) |
| `sf2ce.png` | Street Fighter II': Champion Edition (`sf2ce`) |

Any format WPF decodes (PNG/JPG/BMP/GIF). Missing file → the launcher shows a
plain placeholder tile with the game's short name.
