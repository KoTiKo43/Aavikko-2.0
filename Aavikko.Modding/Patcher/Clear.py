#!/usr/bin/env python3
"""
Clear.py — revert upstream to clean state after Apply.py.

What it does:
  1. Reverts Resources/ to HEAD (git checkout + git clean)
  2. Reverts Content.* to HEAD (git checkout + git clean)
  3. Reverts RobustToolbox to HEAD (git checkout + git clean)
  4. Deletes .applied marker

If .applied exists: reads it for info, then does full revert.
If .applied missing: does full revert anyway (safety).

Also cleans up:
  - .upstream_state.json (state tracking — stale after revert)
  - .conflict_decisions.yml (decisions — stale after revert)
  - Temp snapshot files (.old.patched, .conflict.upstream, .conflict.patched)

Usage:
  python3 Clear.py             # Full revert
  python3 Clear.py --dry-run   # Show what would be reverted
"""
from __future__ import annotations

import argparse
import json
import subprocess
import sys
import time
from pathlib import Path

SCRIPT_DIR = Path(__file__).resolve().parent
BUILD_ROOT = SCRIPT_DIR.parent.parent
APPLIED_FILE = SCRIPT_DIR / ".applied"
STATE_FILE = SCRIPT_DIR / ".upstream_state.json"
DECISIONS_FILE = SCRIPT_DIR / ".conflict_decisions.yml"

# Directories that Apply.py touches — all reverted on clear
REVERT_DIRS = [
    "Resources",
    "Content.Server",
    "Content.Client",
    "Content.Shared",
    "Content.Server.Database",
    "Content.Tests",
    "Content.IntegrationTests",
]


def run(cmd: str, cwd: Path | None = None) -> tuple[str, str, int]:
    result = subprocess.run(
        cmd, shell=True, cwd=cwd, capture_output=True,
        text=True, encoding="utf-8", errors="replace"
    )
    return result.stdout.strip(), result.stderr.strip(), result.returncode


def sync_content_mods_back():
    """Sync Content.*/Aavikko/ → Aavikko.Content/Mods/ before reverting.

    Apply.py copies Aavikko.Content/Mods/*.cs → Content.*/Aavikko/.
    Developer may edit these files in-place (in Content.*/Aavikko/).
    Before Clear reverts upstream (git clean -fd removes them), we sync
    changes back to the overlay so nothing is lost.

    Only syncs files that exist in upstream (applied mods). New files created
    by dev in Content.*/Aavikko/ that don't have overlay counterpart are also
    copied back.
    """
    import shutil
    content_mods_dir = BUILD_ROOT / "Aavikko.Content" / "Mods"
    if not content_mods_dir.exists():
        return 0

    synced = 0
    # Walk all Content.* dirs and look for Aavikko/ subdirs
    for content_proj in ("Content.Server", "Content.Client", "Content.Shared",
                         "Content.Tests", "Content.Server.Database",
                         "Content.IntegrationTests"):
        aavikko_dir = BUILD_ROOT / content_proj / "Aavikko"
        if not aavikko_dir.exists():
            continue

        # Walk all files in Content.*/Aavikko/
        for src in sorted(aavikko_dir.rglob("*")):
            if not src.is_file():
                continue
            if src.is_symlink():
                continue
            # Mirror path: Content.Server/Aavikko/Foo.cs → Aavikko.Content/Mods/Content.Server/Aavikko/Foo.cs
            rel = src.relative_to(BUILD_ROOT)
            dst = content_mods_dir / rel
            dst.parent.mkdir(parents=True, exist_ok=True)

            # Only sync if content differs (or file doesn't exist in overlay)
            need_sync = True
            if dst.exists():
                try:
                    if src.read_bytes() == dst.read_bytes():
                        need_sync = False
                except OSError:
                    pass

            if need_sync:
                shutil.copy2(src, dst)
                synced += 1

    return synced


def revert_dir(dirname: str) -> bool:
    """Revert a directory to HEAD: git checkout + git clean.
    Returns True if BOTH commands succeeded (no errors)."""
    target = BUILD_ROOT / dirname
    if not target.exists():
        return False

    # git checkout HEAD -- <dir> (revert tracked file modifications)
    _, _, rc1 = run(f"git checkout HEAD -- {dirname}/", cwd=BUILD_ROOT)

    # git clean -fd <dir> (remove untracked files added by Apply)
    _, _, rc2 = run(f"git clean -fd {dirname}/", cwd=BUILD_ROOT)

    # Both should succeed; if either fails, it's a real error (not partial)
    if rc1 != 0:
        print(f"  [WARN] git checkout failed for {dirname}/ (rc={rc1})", file=sys.stderr)
    if rc2 != 0:
        print(f"  [WARN] git clean failed for {dirname}/ (rc={rc2})", file=sys.stderr)
    return rc1 == 0 and rc2 == 0


def revert_robusttoolbox() -> bool:
    """Revert RobustToolbox submodule to HEAD."""
    rb = BUILD_ROOT / "RobustToolbox"
    if not rb.exists():
        return False
    run("git checkout HEAD -- .", cwd=rb)
    run("git clean -fd", cwd=rb)
    return True


def cleanup_temp_snapshots():
    """Remove temp snapshot files created by Check.py.
    These are: .old.patched, .conflict.upstream, .conflict.patched
    Found in Aavikko.Resources/Patches/ and Aavikko.Content/Patches/"""
    cleaned = 0
    for patches_dir in [
        BUILD_ROOT / "Aavikko.Resources" / "Patches",
        BUILD_ROOT / "Aavikko.Content" / "Patches",
    ]:
        if not patches_dir.exists():
            continue
        for suffix in [".old.patched", ".conflict.upstream", ".conflict.patched"]:
            for f in patches_dir.rglob(f"*{suffix}"):
                if f.is_file():
                    f.unlink()
                    cleaned += 1
    return cleaned


def main():
    parser = argparse.ArgumentParser(description="Revert Aavikko mod overlay (clear upstream)")
    parser.add_argument("--dry-run", action="store_true",
                        help="Show what would be reverted, don't actually do it")
    parser.add_argument("--wipe-decisions", action="store_true",
                        help="Also delete .conflict_decisions.yml (default: keep)")
    args = parser.parse_args()

    print("=" * 70)
    print("Aavikko Mod Clear")
    print("=" * 70)

    # Remove ALL symlinks before revert: @Mods/@Patches, @Path, @patched
    # (they would dangle after Clear reverts upstream files)
    # Fast-path: read .symlinks.json state file (O(N) where N = symlinks created, ~32).
    # Fallback: slow rglob walk (O(filesystem tree), ~50s on HDD with 5000+ files)
    # if state file is missing (first run, legacy state, or manual cleanup).
    try:
        import SymLinks
        print("\n--- Removing all symlinks (@Mods/@Patches/@Path/@patched) ---", flush=True)
        removed = SymLinks.remove_all_tracked_symlinks()
        if removed >= 0:
            # Fast path succeeded
            if removed > 0:
                print(f"  [OK] Removed {removed} symlinks (fast-path via .symlinks.json)")
            else:
                print(f"  [OK] No symlinks found (state file empty)")
        else:
            # Fallback: state file missing — use slow rglob walk
            print("  [INFO] No .symlinks.json — falling back to slow rglob scan...")
            removed = 0
            for overlay_root, label, _ in SymLinks.OVERLAY_PAIRS:
                if overlay_root.exists():
                    removed += SymLinks.remove_nav_links_for_pair(overlay_root, label)
                    removed += SymLinks.remove_path_links_for_overlay(overlay_root, label)
                    removed += SymLinks.remove_patched_links(overlay_root, label)
            if removed > 0:
                print(f"  [OK] Removed {removed} symlinks (legacy rglob scan)")
            else:
                print(f"  [OK] No symlinks found (legacy scan)")
    except ImportError:
        pass

    # Read .applied for info (if exists)
    applied_info = {}
    if APPLIED_FILE.exists():
        try:
            applied_info = json.loads(APPLIED_FILE.read_text(encoding="utf-8"))
            applied_at = applied_info.get("applied_at", "?")
            patches = len(applied_info.get("cs_patches_applied", []))
            mods = applied_info.get("mods_copied", 0)
            res_patches = applied_info.get("patches_copied", 0)
            print(f"\n  .applied found:")
            print(f"    Applied at:   {applied_at}")
            print(f"    CS patches:   {patches}")
            print(f"    Res patches:  {res_patches}")
            print(f"    Mods copied:  {mods}")
        except (json.JSONDecodeError, OSError) as e:
            print(f"\n  [WARN] .applied is corrupted: {e}")
            print(f"  Will proceed with full revert anyway.")
    else:
        print(f"\n  No .applied found — doing full revert for safety.")

    if args.dry_run:
        print(f"\n  [DRY-RUN] Would revert:")
        for d in REVERT_DIRS:
            print(f"    git checkout HEAD -- {d}/ + git clean -fd {d}/")
        print(f"    git checkout HEAD -- . (RobustToolbox/) + git clean -fd")
        print(f"    Delete: .applied, .upstream_state.json, .conflict_decisions.yml")
        print(f"    Clean temp snapshots (.old.patched, .conflict.*)")
        return

    # 1. Revert Resources/
    print(f"\n--- [1/5] Revert Resources/ ---")
    revert_dir("Resources")
    print("  [OK] Resources/ reverted to HEAD")

    # 2. Sync Content Mods back (before reverting Content.*!)
    #    Apply.py copies Aavikko.Content/Mods/*.cs → Content.*/Aavikko/
    #    Dev may have edited them in Content.*/Aavikko/ — sync back so nothing is lost
    print(f"\n--- [2/5] Sync Content Mods back ---")
    synced = sync_content_mods_back()
    if synced > 0:
        print(f"  [OK] Synced {synced} file(s) back to Aavikko.Content/Mods/")
    else:
        print(f"  [OK] No changes to sync back")

    # 3. Revert Content.*
    print(f"\n--- [3/5] Revert Content.* ---")
    for d in REVERT_DIRS[1:]:  # Skip "Resources", already done
        if (BUILD_ROOT / d).exists():
            revert_dir(d)
    print("  [OK] Content.* reverted to HEAD")

    # 4. Revert RobustToolbox
    print(f"\n--- [4/5] Revert RobustToolbox ---")
    if revert_robusttoolbox():
        print("  [OK] RobustToolbox/ reverted to HEAD")
    else:
        print("  [SKIP] RobustToolbox/ not found")

    # 5. Cleanup state files + temp snapshots
    print(f"\n--- [5/5] Cleanup state files ---")
    cleaned_snapshots = cleanup_temp_snapshots()
    if cleaned_snapshots > 0:
        print(f"  [DEL] {cleaned_snapshots} temp snapshot(s) removed")

    if APPLIED_FILE.exists():
        APPLIED_FILE.unlink()
        print("  [DEL] .applied")

    if STATE_FILE.exists():
        STATE_FILE.unlink()
        print("  [DEL] .upstream_state.json")

    if DECISIONS_FILE.exists():
        if args.wipe_decisions:
            DECISIONS_FILE.unlink()
            print("  [DEL] .conflict_decisions.yml (--wipe-decisions)")
        else:
            print("  [KEEP] .conflict_decisions.yml (use --wipe-decisions to delete)")

    print(f"\n{'=' * 70}")
    print("Done! Upstream is clean.")
    print("  Safe to: git pull, run Migrate.py, run Apply.py")
    print(f"{'=' * 70}")


if __name__ == "__main__":
    main()
