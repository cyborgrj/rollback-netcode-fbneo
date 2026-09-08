# json_roms/ — optional rom-database

If one or more `*.json` files are here, the launcher's **Baixar ROM** button
becomes active. The build copies this folder next to `RbfLauncher.exe`.

Shape read (map keyed by set name):

```json
{ "<set>": { "download": "<url>", "require": ["<dep>", "..."] } }
```

The repository ships none of these files and **`*.json` here is git-ignored** —
they never enter version control. Without a file, the download button stays
disabled and the launcher says so.
