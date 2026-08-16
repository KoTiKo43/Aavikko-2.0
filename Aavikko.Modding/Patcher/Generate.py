#!/usr/bin/env python3
"""
Generate.py — capture git diff from upstream .cs file, save as .cs.patch.

Workflow:
  1. Developer edits an upstream .cs file (e.g. Content.Shared/Botany/Systems/PlantSystem.cs)
     in IDE — normal editing, IDE sees real project, type checking works.
  2. Developer runs: python3 Generate.py Content.Shared/Botany/Systems/PlantSystem.cs
  3. Generate.py:
     a. Verifies the file exists and is tracked by git
     b. Runs `git diff <file>` to capture changes
     c. If diff is empty: "No changes to capture" → exit
     d. Saves diff to Aavikko.Content/Patches/<mirror_path>.cs.patch
     e. Optionally runs `git checkout -- <file>` to restore upstream (with --restore flag)
     f. Prints summary

Usage:
  python3 Generate.py <path/to/file.cs>
  python3 Generate.py <path/to/file.cs> --restore   # restore upstream after capturing
  python3 Generate.py --all                          # capture all modified .cs files
  python3 Generate.py --list                         # list modified .cs files

The .patch file mirrors the upstream path:
  Upstream:   Content.Shared/Botany/Systems/PlantSystem.cs
  Patch:      Aavikko.Content/Patches/Content.Shared/Botany/Systems/PlantSystem.cs.patch

For .csproj patches (999-csproj-include-aavikko.cs.patch), use --csproj flag:
  python3 Generate.py Content.Server/Content.Server.csproj --csproj
"""
from __future__ import annotations

import argparse
import shlex
import subprocess
import sys
from pathlib import Path

from ui import (
    header, section, divider, kv, ok, info, warn, error, fatal,
    skip, hint, tag, bullet, progress_iter, summary_table,
    success_banner, fail_banner, dim, bold,
    green, yellow, red, cyan, magenta,
)


SCRIPT_DIR = Path(__file__).resolve().parent
BUILD_ROOT = SCRIPT_DIR.parent.parent
PATCHES_DIR = BUILD_ROOT / "Aavikko.Content" / "Patches"
ROBUST_PATCHES_DIR = BUILD_ROOT / "Aavikko.RobustToolbox" / "Patches"
ROBUST_DIR = BUILD_ROOT / "RobustToolbox"


def run(cmd: str, cwd: Path | None = None, check: bool = True) -> tuple[str, str, int]:
    result = subprocess.run(
        cmd, shell=True, cwd=cwd, capture_output=True,
        text=True, encoding="utf-8", errors="replace"
    )
    if check and result.returncode != 0:
        error(f"Command failed: {cmd}")
        hint(f"stderr: {result.stderr}")
        sys.exit(1)
    # NOTE: .strip() on stdout/stderr is OK for most commands (git rev-parse,
    # git status, etc.) but BREAKS git diff output — it removes trailing empty
    # context lines that are part of the patch format. For git diff, use
    # get_diff() which preserves the raw output.
    return result.stdout.strip(), result.stderr.strip(), result.returncode


def get_diff(filepath: str) -> str:
    """Get git diff for a file (compares working tree vs HEAD).
    
    CRITICAL: must NOT strip trailing whitespace from output — git diff's patch
    format includes trailing empty context lines that are part of the hunk.
    Stripping them produces a patch with mismatched hunk counts
    (@@ -X,Y +A,B @@ says Y lines, but only Y-1 are present) →
    "corrupt patch at line N" error in git apply.
    """
    result = subprocess.run(
        f"git diff -- {filepath}",
        shell=True, cwd=BUILD_ROOT, capture_output=True,
        text=True, encoding="utf-8", errors="replace"
    )
    # Preserve trailing newlines — only strip leading/trailing whitespace
    # that's NOT part of the diff content. git diff output ends with \n after
    # the last hunk line — we keep that.
    # We do NOT strip here. Any trailing whitespace IS part of the patch.
    return result.stdout


def get_patch_dest(filepath: str, is_csproj: bool = False, is_robust: bool = False) -> Path:
    """Calculate patch destination path.

    For regular .cs files (Content.*): mirror upstream path
      Content.Shared/Botany/Systems/PlantSystem.cs
      → Aavikko.Content/Patches/Content.Shared/Botany/Systems/PlantSystem.cs.patch

    For RobustToolbox .cs files: mirror path under Aavikko.RobustToolbox/Patches/
      Robust.Shared/ProgramShared.cs
      → Aavikko.RobustToolbox/Patches/Robust.Shared/ProgramShared.cs.patch

    For .csproj files: use 999-csproj-include-aavikko.cs.patch name
      Content.Server/Content.Server.csproj
      → Aavikko.Content/Patches/Content.Server/999-csproj-include-aavikko.cs.patch
    """
    p = Path(filepath)
    base_dir = ROBUST_PATCHES_DIR if is_robust else PATCHES_DIR
    if is_csproj:
        # csproj patches go to <Project>/999-csproj-include-aavikko.cs.patch
        project = p.parent.name  # Content.Server, Content.Client, etc.
        return base_dir / project / "999-csproj-include-aavikko.cs.patch"
    else:
        # Regular .cs: mirror path + .patch extension
        return base_dir / f"{filepath}.patch"


def capture_patch(filepath: str, restore: bool = False, is_csproj: bool = False,
                  keep_broken: bool = False, is_robust: bool = False) -> bool:
    """Capture git diff for a file and save as .patch. Returns True if saved.

    Verification strategy (CRITICAL):
    The patch is generated from working tree diff (changes vs HEAD). To verify
    the patch is valid, we apply --check against the HEAD version of the file
    (not the working tree). If we ran git apply --check against the working
    tree directly, the patch would ALWAYS fail — because the working tree
    already contains the changes.

    On verification failure, the patch is saved with `.broken` suffix (NOT deleted)
    so the developer can inspect/debug. Use --keep-broken to save with normal name.

    Set is_robust=True for RobustToolbox files (paths start with Robust.*)
    — patch is saved to Aavikko.RobustToolbox/Patches/ and git apply runs
    with cwd=RobustToolbox/.
    """
    # Determine working directory and full path
    cwd = ROBUST_DIR if is_robust else BUILD_ROOT
    full_path = cwd / filepath

    # Verify file exists
    if not full_path.exists():
        error(f"File not found: {filepath}")
        hint(f"Check the path. {'RobustToolbox dir' if is_robust else 'BUILD_ROOT'} = {cwd}")
        return False

    # Verify file is tracked by git
    _, _, rc = run(f"git ls-files --error-unmatch {filepath}", cwd=cwd, check=False)
    if rc != 0:
        error(f"File is not tracked by git: {filepath}")
        hint("Generate.py only works on upstream files (tracked by git).")
        hint("For new Aavikko files, place them directly in Aavikko.Content/Mods/ or Aavikko.RobustToolbox/Mods/.")
        return False

    # Get diff (must use cwd-specific git, preserve raw output)
    diff_result = subprocess.run(
        f"git diff -- {filepath}",
        shell=True, cwd=cwd, capture_output=True,
        text=True, encoding="utf-8", errors="replace"
    )
    diff = diff_result.stdout
    if not diff.strip():
        skip(f"No changes in {filepath} (working tree matches HEAD)")
        return False

    # Calculate destination
    dest = get_patch_dest(filepath, is_csproj=is_csproj, is_robust=is_robust)
    dest.parent.mkdir(parents=True, exist_ok=True)

    # Save patch
    dest.write_text(diff + "\n", encoding="utf-8")

    # CRITICAL: verify patch against CLEAN upstream (HEAD version), not working tree.
    # The working tree already has our changes — applying patch there always fails.
    # Strategy: save the developer's current content, checkout HEAD version of
    # the file, run git apply --check, then restore developer's content.
    # try/finally ensures developer's changes are NEVER lost.
    saved_ok = False
    original_content = full_path.read_bytes()
    try:
        # Checkout HEAD version (clean upstream)
        run(f"git checkout HEAD -- {shlex.quote(filepath)}", cwd=cwd, check=False)

        # Verify patch against now-clean upstream
        patch_str = shlex.quote(str(dest))
        _, verify_err, verify_rc = run(
            f"git apply --check {patch_str}", cwd=cwd, check=False
        )

        if verify_rc == 0:
            ok(f"Patch saved + verified: {dest.relative_to(BUILD_ROOT)}")
            saved_ok = True
        else:
            # Verification failed — rename to .broken so dev can inspect
            broken_dest = dest.with_suffix(dest.suffix + ".broken")
            if dest.exists():
                if not broken_dest.exists():
                    dest.rename(broken_dest)
                else:
                    dest.unlink()
            error(f"Patch verification FAILED: {dest.relative_to(BUILD_ROOT)}")
            hint(f"git apply --check error: {verify_err[:300]}")
            if broken_dest.exists():
                hint(f"Patch saved as: {broken_dest.relative_to(BUILD_ROOT)}")
            hint("Common causes:")
            hint("  1. Hunk header counts don't match actual context/added/removed lines")
            hint("  2. Encoding issues (Russian comments mangled)")
            hint("  3. Patch was hand-edited and @@ -X,Y +A,B @@ wasn't updated")
            hint("Inspect .broken file or re-save with --keep-broken")
            saved_ok = False
    finally:
        # ALWAYS restore developer's original content (never lose changes!)
        full_path.write_bytes(original_content)

    if not saved_ok and not keep_broken:
        return False

    kv("Source", filepath, indent=5)
    kv("Diff size", f"{len(diff)} bytes", indent=5)

    # Optionally restore upstream (only if patch was saved OK)
    if restore and saved_ok:
        run(f"git checkout -- {filepath}", cwd=BUILD_ROOT, check=False)
        ok(f"Restored upstream: {filepath}")
    
    return True


def list_modified_cs(is_robust: bool = False) -> list[str]:
    """List all modified .cs files (git status)."""
    cwd = ROBUST_DIR if is_robust else BUILD_ROOT
    stdout, _, _ = run("git status --porcelain", cwd=cwd, check=False)
    modified = []
    for line in stdout.splitlines():
        if not line.strip():
            continue
        parts = line.split(None, 1)
        if len(parts) < 2:
            continue
        status = parts[0].strip()
        path = parts[1].strip().split(" -> ")[-1].strip('"')
        # Only .cs files, only modified (M) or added (A) — not deleted
        if path.endswith(".cs") and status[0] in ("M", "A"):
            # Skip Aavikko.* paths
            if path.startswith("Aavikko."):
                continue
            modified.append(path)
    return modified


def main():
    parser = argparse.ArgumentParser(description="Generate .cs.patch from upstream file changes")
    parser.add_argument("filepath", nargs="?", help="Path to .cs file (relative to build root)")
    parser.add_argument("--restore", action="store_true",
                        help="Restore upstream file after capturing diff (git checkout)")
    parser.add_argument("--all", action="store_true",
                        help="Capture all modified .cs files")
    parser.add_argument("--list", action="store_true",
                        help="List modified .cs files")
    parser.add_argument("--csproj", action="store_true",
                        help="Treat filepath as .csproj (saves as 999-csproj-include-aavikko.cs.patch)")
    parser.add_argument("--robust", action="store_true",
                        help="Target is RobustToolbox file (paths like Robust.Shared/...) — save to Aavikko.RobustToolbox/Patches/")
    parser.add_argument("--keep-broken", action="store_true",
                        help="Save patch even if git apply --check fails (debugging only)")
    args = parser.parse_args()
    
    header("Aavikko Patch Generator", "capture git diff → .cs.patch")
    
    if args.list:
        modified = list_modified_cs(is_robust=args.robust)
        if not modified:
            info(f"No modified .cs files found{' in RobustToolbox' if args.robust else ''}.")
        else:
            print()
            print(f"  {bold(f'Modified .cs files ({len(modified)}):')}")
            for f in modified:
                print(f"    {f}")
            print()
            hint(f"Run: python3 Generate.py --all" + (" --robust" if args.robust else ""))
        return
    
    if args.all:
        modified = list_modified_cs(is_robust=args.robust)
        if not modified:
            info(f"No modified .cs files to capture{' in RobustToolbox' if args.robust else ''}.")
            return
        section("all", None, f"Capturing {len(modified)} file(s)")
        saved = 0
        failed = 0
        for f in modified:
            print()
            if capture_patch(f, restore=args.restore, keep_broken=args.keep_broken,
                             is_robust=args.robust):
                saved += 1
            else:
                failed += 1
        if failed > 0:
            fail_banner(
                f"Saved {saved}/{len(modified)} — {failed} FAILED verification",
                hints=[
                    "Patches that failed verification were NOT saved.",
                    "Fix the source file or use --keep-broken for debugging.",
                ],
            )
            sys.exit(1)
        success_banner(
            f"Saved {saved}/{len(modified)} patch(es)",
            next_step=("All upstream files restored to HEAD." if args.restore else None),
        )
        return
    
    if not args.filepath:
        parser.error("filepath required (or use --all / --list)")
    
    filepath = args.filepath
    # Normalize path (remove leading ./)
    if filepath.startswith("./"):
        filepath = filepath[2:]
    
    section("single", None, f"Capturing patch for: {filepath}" +
            (" (RobustToolbox)" if args.robust else ""))
    if capture_patch(filepath, restore=args.restore, is_csproj=args.csproj,
                     keep_broken=args.keep_broken, is_robust=args.robust):
        if args.restore:
            success_banner("Done!", next_step="Upstream file restored. Patch saved separately.")
        else:
            success_banner(
                "Done!",
                next_step=f"Patch saved + verified. Upstream file still has changes.\n"
                          f"  Run `git checkout -- {filepath}` to restore when done.",
            )
    else:
        sys.exit(1)


if __name__ == "__main__":
    main()
