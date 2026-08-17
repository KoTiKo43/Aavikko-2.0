# Aavikko Modding

Universal modding system for Space Station 14 builds. Overlay resources, patch C# code, and add new functionality — without modifying upstream files.

## Structure

```
<SS14 build root>/
├── Aavikko.Content/          ← New C# files (.cs.mod)
│   ├── Content.Server/
│   ├── Content.Client/
│   └── Content.Shared/
│
├── Aavikko.Resources/        ← Resource overlay (--mount-dir)
│   ├── Textures/
│   ├── Prototypes/
│   ├── Audio/
│   └── Locale/
│
└── Aavikko.Modding/          ← Tools + patches
    ├── Patcher/
    │   ├── Generate.py       ← Scan git changes, create .cs.mod + .cs.patch
    │   ├── Apply.py          ← Apply patches, restore .cs.mod → .cs
    │   ├── Clear.py          ← Revert upstream to base_commit
    │   ├── .base_commit      ← (auto) Corvax + RobustToolbox HEAD hashes
    │   ├── .applied          ← (auto) List of applied patches
    │   └── Patches/
    │       ├── RobustToolbox/    ← Engine patches
    │       ├── Content.Server/   ← Server C# patches
    │       ├── Content.Client/   ← Client C# patches
    │       └── Content.Shared/   ← Shared C# patches
    │
    └── Launcher/
        ├── Linux/            ← .sh scripts
        └── Windows/          ← .bat scripts
```

## Quick start

```bash
# 1. Edit upstream code as usual (or create new .cs files)
vim Content.Server/Administration/Commands/MyCommand.cs
vim Content.Shared/Botany/Systems/PlantSystem.cs  # fix a bug

# 2. Generate mod files (scans git, creates .cs.mod + .cs.patch)
python3 Aavikko.Modding/Patcher/Generate.py

# 3. Build (Clear + Apply + dotnet build)
bash Aavikko.Modding/Launcher/Linux/Server.RestoreAndBuild.sh

# 4. Run
bash Aavikko.Modding/Launcher/Linux/Server.Run.sh

# 5. Commit (only Aavikko.* folders!)
git add Aavikko.Content/ Aavikko.Modding/ Aavikko.Resources/
git commit -m "feat: my changes"
```

## How it works

### New C# code → `Aavikko.Content/` (`.cs.mod` files)

- Write `.cs` files anywhere in `Content.*/`
- `Generate.py` copies them to `Aavikko.Content/` as `.cs.mod`
- `Apply.py` restores them as `.cs` during build + adds `<Compile Include>` to `.csproj`

### Modified upstream C# → `Aavikko.Modding/Patches/` (`.cs.patch` files)

- Edit upstream `.cs` file
- `Generate.py` creates `git diff` as `.cs.patch`
- `Apply.py` applies patch via `git apply`

### Modified RobustToolbox → `Aavikko.Modding/Patches/RobustToolbox/`

- Same as above, but for `RobustToolbox/` submodule
- `Apply.py` handles paths correctly

### Resource overlay → `Aavikko.Resources/`

- Place textures, prototypes, audio, locale here
- `Server.Run.sh` passes `--mount-dir Aavikko.Resources` to SS14
- RobustToolbox loads mod resources **before** upstream (mod wins)

## Conflict detection

`Apply.py` checks if upstream files changed since `.base_commit` was recorded.

If upstream changed:
```
[CONFLICT] 001-plant-system.cs.patch
  Target: Content.Shared/Botany/Systems/PlantSystem.cs
  Upstream file changed since base commit abc1234.

Options:
  [s] Skip this patch
  [f] Force apply (may fail)
  [a] Abort
```

## Commands

| Command | What it does |
|---|---|
| `Generate.py` | Scan git, create `.cs.mod` + `.cs.patch` (doesn't touch upstream) |
| `Generate.py --clean` | Remove all generated files |
| `Apply.py` | Apply patches + restore `.cs.mod` (requires clean upstream) |
| `Apply.py --force` | Skip conflict checks |
| `Apply.py --skip-conflicts` | Skip conflicting patches |
| `Clear.py` | Revert upstream to base_commit |
| `Clear.py --dry-run` | Show what would be cleared |

## Launcher scripts

| Script | What it does |
|---|---|
| `Server.RestoreAndBuild.sh` | Clear + Apply + `dotnet build` |
| `Server.Run.sh` | `dotnet run` with `--mount-dir` |
| `Server.Clear.sh` | Clear patches |
| `Client.RestoreAndBuild.sh` | Same for client |
| `Client.Run.sh` | Same for client |

## Examples

See:
- `Aavikko.Content/Content.Server/Administration/Commands/HelloAavikkoCommand.cs.mod` — new command
- `Aavikko.Modding/Patches/Content.Shared/001-plant-system-try-get-tray.cs.patch` — bug fix
- `Aavikko.Modding/Patches/RobustToolbox/001-program-shared-mount-dir.cs.patch` — engine fix
