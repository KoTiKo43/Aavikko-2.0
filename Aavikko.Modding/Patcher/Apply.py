#!/usr/bin/env python3
"""
Apply.py — apply Aavikko mod overlay to upstream SS14 build.

Pipeline:
  0. Check for unresolved conflicts (Check.py --apply-check)
  1. Delete upstream files (manifest delete + Deletes/ manual)
  2. Copy Patches/ → Resources/ (overwrite upstream)
  3. Copy Mods/ → Resources/ (add new content)
  4. Apply .cs.patch + .xaml.patch via git apply
  5. Write .applied marker (with head_commit for Clear.py)

Security:
  - safe_resolve_under() prevents path traversal via manifest.yml or Deletes/
  - File lock prevents concurrent Apply.py runs
  - Empty overlay check prevents false success

Deletes/ folder:
  Developer creates empty placeholder files with mirror paths.
  Apply.py removes the corresponding Resources/ file.
"""
from __future__ import annotations

import argparse
import json
import os
import shlex
import shutil
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path

# File locking — fcntl is Unix-only, use msvcrt on Windows
try:
    import fcntl
    HAS_FCNTL = True
except ImportError:
    HAS_FCNTL = False
    try:
        import msvcrt
        HAS_MSVCRT = True
    except ImportError:
        HAS_MSVCRT = False

SCRIPT_DIR = Path(__file__).resolve().parent
BUILD_ROOT = SCRIPT_DIR.parent.parent  # Patcher/ → Aavikko.Modding/ → Corvax_Clean/
RESOURCES_DIR = BUILD_ROOT / "Aavikko.Resources"
MODS_DIR = RESOURCES_DIR / "Mods"
PATCHES_DIR = RESOURCES_DIR / "Patches"
DELETES_DIR = RESOURCES_DIR / "Deletes"
MANIFEST = RESOURCES_DIR / "manifest.yml"
CONTENT_DIR = BUILD_ROOT / "Aavikko.Content"
CS_MODS_DIR = CONTENT_DIR / "Mods"
CS_PATCHES_DIR = CONTENT_DIR / "Patches"
# RobustToolbox overlay — engine modifications (submodule, separate git repo)
ROBUST_DIR = BUILD_ROOT / "RobustToolbox"
ROBUST_OVERLAY_DIR = BUILD_ROOT / "Aavikko.RobustToolbox"
ROBUST_MODS_DIR = ROBUST_OVERLAY_DIR / "Mods"
ROBUST_PATCHES_DIR = ROBUST_OVERLAY_DIR / "Patches"
APPLIED_FILE = SCRIPT_DIR / ".applied"
LOCK_FILE = SCRIPT_DIR / ".apply.lock"

# Temp snapshot suffixes created by Check.py — must NOT be copied to Resources/
TEMP_SNAPSHOT_SUFFIXES = (".conflict.upstream", ".old.patched", ".conflict.patched")


def run(cmd: str, cwd: Path | None = None) -> tuple[str, str, int]:
    result = subprocess.run(
        cmd, shell=True, cwd=cwd, capture_output=True,
        text=True, encoding="utf-8", errors="replace"
    )
    return result.stdout.strip(), result.stderr.strip(), result.returncode


def _atomic_write_text(path: Path, content: str) -> None:
    """Write text to a file atomically: write to temp, then os.replace.

    Prevents corruption if the process is killed (Ctrl+C, OOM) or disk fills
    up mid-write. os.replace() is atomic on POSIX.
    """
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_suffix(path.suffix + ".tmp")
    try:
        tmp.write_text(content, encoding="utf-8")
        os.replace(tmp, path)
    except Exception:
        # Cleanup temp file on failure
        try:
            if tmp.exists():
                tmp.unlink()
        except OSError:
            pass
        raise


def safe_resolve_under(under: Path, rel_path: str) -> Path | None:
    """Resolve rel_path under `under` directory, refusing path traversal.
    Returns the resolved Path, or None if rel_path escapes `under` or targets root."""
    if not rel_path or not rel_path.strip():
        return None
    cleaned = rel_path.lstrip("/")
    if cleaned in (".", "./", ""):
        return None  # Don't allow targeting the root directory itself
    candidate = (under / cleaned).resolve()
    under_resolved = under.resolve()
    try:
        candidate.relative_to(under_resolved)
    except ValueError:
        return None
    if candidate == under_resolved:
        return None  # Don't allow deleting the root itself
    return candidate


# ── Lock ───────────────────────────────────────────────────────────────────


_lock_fd = None


def acquire_lock() -> bool:
    """Prevent concurrent Apply.py runs. Returns True if lock acquired.

    Uses fcntl.flock on Unix, msvcrt.locking on Windows. On platforms without
    either (very rare), locking is skipped (no-op, but Apply still works).
    """
    global _lock_fd
    _lock_fd = open(LOCK_FILE, "w")
    try:
        if HAS_FCNTL:
            fcntl.flock(_lock_fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
            return True
        elif HAS_MSVCRT:
            try:
                msvcrt.locking(_lock_fd.fileno(), msvcrt.LK_NBLCK, 1)
                return True
            except OSError:
                return False
        else:
            # No locking available — proceed without (single-instance is dev's responsibility)
            return True
    except (BlockingIOError, OSError):
        return False


def release_lock():
    """Release the file lock."""
    global _lock_fd
    if _lock_fd:
        if HAS_FCNTL:
            fcntl.flock(_lock_fd, fcntl.LOCK_UN)
        elif HAS_MSVCRT:
            try:
                msvcrt.locking(_lock_fd.fileno(), msvcrt.LK_UNLCK, 1)
            except OSError:
                pass
        _lock_fd.close()
        _lock_fd = None


# ── Delete operations ──────────────────────────────────────────────────────


def _delete_section(section_name: str) -> list[str]:
    """Delete files listed in manifest.yml under `section_name:` (delete: or stale:)."""
    if not MANIFEST.exists():
        return []
    deleted = []
    content = MANIFEST.read_text(encoding="utf-8")
    in_section = False
    for line in content.splitlines():
        if line.strip() == f"{section_name}:":
            in_section = True
            continue
        if in_section:
            if line.strip().startswith("- "):
                path = line.strip()[2:].strip()
                target = safe_resolve_under(BUILD_ROOT / "Resources", path)
                if target is None:
                    print(f"  [WARN] Refusing path traversal in manifest.yml: {path}", file=sys.stderr)
                    continue
                if target.exists():
                    if target.is_dir():
                        shutil.rmtree(target)
                    else:
                        target.unlink()
                    deleted.append(path)
                    print(f"  [DEL-{section_name.upper()}] {path}")
                else:
                    if section_name != "stale":
                        print(f"  [SKIP] {path} (not found)")
            elif line.strip() and not line.strip().startswith("#"):
                in_section = False
    return deleted


def delete_conflicts() -> list[str]:
    return _delete_section("delete")


def delete_stale() -> list[str]:
    return _delete_section("stale")


def delete_manual() -> list[str]:
    """Delete upstream files marked by developer in Aavikko.Resources/Deletes/."""
    if not DELETES_DIR.exists():
        return []
    deleted = []
    for f in sorted(DELETES_DIR.rglob("*")):
        if not f.is_file():
            continue
        rel = f.relative_to(DELETES_DIR)
        target = safe_resolve_under(BUILD_ROOT / "Resources", str(rel))
        if target is None:
            print(f"  [WARN] Refusing path traversal in Deletes/: {rel}", file=sys.stderr)
            continue
        if target.exists():
            if target.is_dir():
                shutil.rmtree(target)
            else:
                target.unlink()
            deleted.append(str(rel))
            print(f"  [DEL-MANUAL] {rel}")
        else:
            print(f"  [SKIP-MANUAL] {rel} (already clean)")
    return deleted


# ── Copy operations ────────────────────────────────────────────────────────


def copy_tree(src_dir: Path, label: str) -> int:
    """Copy all files from src_dir to Resources/ (overwrite).
    Skips symlinks and temp snapshot files from Check.py."""
    count = 0
    skipped_symlinks = 0
    skipped_temps = 0
    files = sorted([f for f in src_dir.rglob("*") if f.is_file()])
    for src in files:
        if src.is_symlink():
            print(f"  [WARN] Symlink skipped: {src.relative_to(src_dir)}", file=sys.stderr)
            skipped_symlinks += 1
            continue
        # Skip temp snapshot files (.conflict.upstream, .old.patched, .conflict.patched)
        if any(src.name.endswith(suf) for suf in TEMP_SNAPSHOT_SUFFIXES):
            skipped_temps += 1
            continue
        rel = src.relative_to(src_dir)
        dst = BUILD_ROOT / "Resources" / rel
        dst.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy(src, dst)
        count += 1
        if count % 500 == 0:
            print(f"    ... {count}/{len(files)}")
    if skipped_symlinks:
        print(f"  [WARN] {skipped_symlinks} symlink(s) skipped", file=sys.stderr)
    if skipped_temps:
        print(f"  [SKIP] {skipped_temps} temp snapshot(s) skipped", file=sys.stderr)
    print(f"  [OK] {count} files copied ({label})")
    return count


# ── Patch operations ───────────────────────────────────────────────────────


def load_skip_decisions() -> set[str]:
    """Load patch paths marked as 's' (skip) from .conflict_decisions.yml.

    Returns a set of upstream-relative paths (without .cs.patch/.xaml.patch suffix)
    that should NOT be applied. Apply.py honors these skip decisions so that
    developer choices in Check.py are respected.
    """
    decisions_file = SCRIPT_DIR / ".conflict_decisions.yml"
    if not decisions_file.exists():
        return set()
    skip_paths = set()
    try:
        content = decisions_file.read_text(encoding="utf-8")
    except OSError:
        return set()
    current_section = None
    current_path = None
    for line in content.splitlines():
        stripped = line.strip()
        if stripped == "patches:":
            current_section = "patches"
            current_path = None
            continue
        elif stripped == "mods:":
            current_section = "mods"
            current_path = None
            continue
        elif not stripped or stripped.startswith("#"):
            continue
        if stripped.startswith("- ") and current_section == "patches":
            current_path = stripped[2:].strip()
        elif ":" in stripped and current_path and current_section == "patches":
            key, val = stripped.split(":", 1)
            if key.strip() == "decision" and val.strip() == "s":
                skip_paths.add(current_path)
    return skip_paths


def apply_cs_patch(patch_path: Path, skip_paths: set[str] | None = None,
                   cwd: Path | None = None, upstream_prefix: str = "") -> bool:
    """Apply a .cs.patch or .xaml.patch via git apply. Idempotent.

    Args:
      patch_path:  path to .cs.patch / .xaml.patch file
      skip_paths:  set of upstream paths to skip (decision: s from .conflict_decisions.yml)
      cwd:         working directory for git apply (BUILD_ROOT for Content.*,
                   ROBUST_DIR for RobustToolbox patches)
      upstream_prefix: prefix to prepend to upstream_rel when matching skip_paths
                       (e.g. "RobustToolbox/" for engine patches)

    If `skip_paths` contains the upstream path that this patch corresponds to,
    the patch is skipped (honoring 's' decisions from .conflict_decisions.yml).
    Returns True if applied or skipped intentionally.
    """
    if cwd is None:
        cwd = BUILD_ROOT

    # Determine the upstream path this patch corresponds to
    patches_root = CS_PATCHES_DIR if cwd == BUILD_ROOT else ROBUST_PATCHES_DIR
    try:
        rel = patch_path.relative_to(patches_root)
        # Strip .patch suffix to get upstream .cs/.xaml path
        upstream_rel = str(rel)
        if upstream_rel.endswith(".cs.patch"):
            upstream_rel = upstream_rel[:-len(".cs.patch")] + ".cs"
        elif upstream_rel.endswith(".xaml.cs.patch"):
            upstream_rel = upstream_rel[:-len(".xaml.cs.patch")] + ".xaml.cs"
        elif upstream_rel.endswith(".xaml.patch"):
            upstream_rel = upstream_rel[:-len(".xaml.patch")] + ".xaml"
    except ValueError:
        upstream_rel = None

    # For skip_paths matching: RobustToolbox patches have prefix "RobustToolbox/"
    skip_key = upstream_prefix + upstream_rel if upstream_rel else None
    if skip_paths and skip_key and skip_key in skip_paths:
        print(f"  [SKIP] {patch_path.name} (decision: s in .conflict_decisions.yml)")
        return True

    patch_str = shlex.quote(str(patch_path))
    stdout, stderr, rc = run(f"git apply --check {patch_str}", cwd=cwd)
    if rc == 0:
        stdout, stderr, rc = run(f"git apply {patch_str}", cwd=cwd)
        if rc == 0:
            print(f"  [OK] {patch_path.name}")
            return True
        print(f"  [FAIL] {patch_path.name}: git apply failed after --check passed")
        print(f"         stderr: {stderr[:200]}")
        return False
    stdout2, stderr2, rc2 = run(f"git apply --reverse --check {patch_str}", cwd=cwd)
    if rc2 == 0:
        print(f"  [SKIP] {patch_path.name} (already applied)")
        return True
    print(f"  [FAIL] {patch_path.name}: cannot apply")
    print(f"         forward:  {stderr[:200]}")
    print(f"         reverse:  {stderr2[:200]}")
    # Detect "No such file or directory" — upstream file was deleted
    combined_err = (stderr + stderr2).lower()
    if "no such file or directory" in combined_err and upstream_rel:
        upstream_full = cwd / upstream_rel
        if not upstream_full.exists():
            print(f"         Upstream file was DELETED: {upstream_rel}")
            print(f"         Options:")
            print(f"           - Remove the .patch file (Aavikko no longer needs this change)")
            print(f"           - Move .patch to Mods/ (if Aavikko wants to keep the file)")
    else:
        print(f"         If upstream changed, regenerate: python3 Generate.py <path> --restore")
    return False


def copy_robust_mods() -> int:
    """Copy Aavikko.RobustToolbox/Mods/ → RobustToolbox/ (mirror path, overwrite).

    Used for new engine files that Aavikko adds (rare). Returns file count.
    """
    if not ROBUST_MODS_DIR.exists() or not ROBUST_DIR.exists():
        return 0
    count = 0
    skipped_symlinks = 0
    files = sorted([f for f in ROBUST_MODS_DIR.rglob("*") if f.is_file()
                    and not f.name.startswith(".gitkeep")])
    for src in files:
        if src.is_symlink():
            print(f"  [WARN] Symlink skipped: {src.relative_to(ROBUST_MODS_DIR)}", file=sys.stderr)
            skipped_symlinks += 1
            continue
        if any(src.name.endswith(suf) for suf in TEMP_SNAPSHOT_SUFFIXES):
            continue
        rel = src.relative_to(ROBUST_MODS_DIR)
        dst = ROBUST_DIR / rel
        dst.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy(src, dst)
        count += 1
    if skipped_symlinks:
        print(f"  [WARN] {skipped_symlinks} symlink(s) skipped", file=sys.stderr)
    if count > 0:
        print(f"  [OK] {count} files copied (RobustToolbox Mods)")
    return count


def copy_content_mods() -> int:
    """Copy Aavikko.Content/Mods/ → Content.*/ (mirror path, overwrite).

    Content Mods are new .cs files that Aavikko adds (e.g. Content.Server/Aavikko/...).
    Unlike Resources Mods, these are C# files that need to be IN the project directory
    so the SDK-style csproj picks them up automatically (no csproj patch needed).

    Structure:
      Aavikko.Content/Mods/Content.Server/Aavikko/Foo.cs → Content.Server/Aavikko/Foo.cs
      Aavikko.Content/Mods/Content.Shared/Aavikko/Bar.cs → Content.Shared/Aavikko/Bar.cs

    Returns file count.
    """
    if not CS_MODS_DIR.exists():
        return 0
    count = 0
    skipped_symlinks = 0
    files = sorted([f for f in CS_MODS_DIR.rglob("*") if f.is_file()
                    and not f.name.startswith(".gitkeep")])
    for src in files:
        if src.is_symlink():
            print(f"  [WARN] Symlink skipped: {src.relative_to(CS_MODS_DIR)}", file=sys.stderr)
            skipped_symlinks += 1
            continue
        if any(src.name.endswith(suf) for suf in TEMP_SNAPSHOT_SUFFIXES):
            continue
        # Mirror path: Aavikko.Content/Mods/Content.Server/Aavikko/Foo.cs → Content.Server/Aavikko/Foo.cs
        rel = src.relative_to(CS_MODS_DIR)
        dst = BUILD_ROOT / rel
        dst.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy(src, dst)
        count += 1
    if skipped_symlinks:
        print(f"  [WARN] {skipped_symlinks} symlink(s) skipped", file=sys.stderr)
    if count > 0:
        print(f"  [OK] {count} files copied (Content Mods)")
    return count


# ── Conflict check ────────────────────────────────────────────────────────


def check_conflicts() -> bool:
    """Check if there are unresolved conflicts. Returns True if Apply can proceed.

    If .upstream_state.json is missing (first-time setup), automatically
    records a baseline via `Check.py --baseline`. This is safe because there
    are no conflicts on the very first Apply — the baseline just records
    current upstream state for future conflict detection.
    """
    state_file = SCRIPT_DIR / ".upstream_state.json"
    check_script = shlex.quote(str(SCRIPT_DIR / "Check.py"))

    if not state_file.exists():
        print("  [INFO] No .upstream_state.json found — recording baseline (first-time setup)")
        stdout, stderr, rc = run(
            f"python3 {check_script} --baseline",
            cwd=BUILD_ROOT
        )
        if rc != 0:
            print(f"\n[FATAL] Failed to record baseline via Check.py --baseline", file=sys.stderr)
            if stderr:
                print(f"  stderr: {stderr[:300]}", file=sys.stderr)
            return False
        print("  [OK] Baseline recorded")
        return True

    stdout, stderr, rc = run(
        f"python3 {check_script} --apply-check",
        cwd=BUILD_ROOT
    )
    if rc != 0:
        print(f"\n[BLOCKED] Unresolved conflicts detected — Apply.py cannot run.")
        print(f"  Run: python3 {SCRIPT_DIR.name}/Check.py")
        print(f"  Resolve all conflicts, then re-run Apply.py.")
        return False
    return True


# ── Main ───────────────────────────────────────────────────────────────────


def validate_overlay_placement() -> list[str]:
    """Check that files are in the correct overlay folder.

    Resources/Mods/ should contain only files NOT in upstream Resources/.
    Resources/Patches/ should contain only files that ARE in upstream Resources/.

    If a file is in the wrong folder, warn the developer:
    - Patches/ file that doesn't exist in upstream → should be in Mods/
    - Mods/ file that exists in upstream → should be in Patches/

    Returns list of warning messages.
    """
    warnings = []

    # Check Resources/Patches/ — each file should exist in upstream Resources/
    if PATCHES_DIR.exists():
        for f in PATCHES_DIR.rglob("*"):
            if not f.is_file():
                continue
            rel = f.relative_to(PATCHES_DIR)
            upstream = BUILD_ROOT / "Resources" / rel
            if not upstream.exists():
                warnings.append(
                    f"  Patches/{rel} — upstream file doesn't exist. "
                    f"Should this be in Mods/ instead?"
                )

    # Check Resources/Mods/ — each file should NOT exist in upstream Resources/
    if MODS_DIR.exists():
        for f in MODS_DIR.rglob("*"):
            if not f.is_file():
                continue
            rel = f.relative_to(MODS_DIR)
            upstream = BUILD_ROOT / "Resources" / rel
            if upstream.exists():
                # Check if it's identical (might be a legit copy that upstream added later)
                # — Check.py handles that via Mods/ conflict detection
                # Here we only warn if the file is DIFFERENT from upstream
                try:
                    if f.read_bytes() != upstream.read_bytes():
                        warnings.append(
                            f"  Mods/{rel} — upstream has this file (different content). "
                            f"Should this be in Patches/ instead?"
                        )
                except OSError:
                    pass

    return warnings


def main():
    parser = argparse.ArgumentParser(description="Apply Aavikko mod overlay")
    parser.add_argument("--force", action="store_true",
                        help="Skip conflict check, force apply anyway")
    parser.add_argument("--reapply", action="store_true",
                        help="Force re-apply even if .applied exists (clears .applied first)")
    args = parser.parse_args()

    print("=" * 70)
    print("Aavikko Mod Apply")
    print("=" * 70)

    # Remove ALL symlinks before applying (they would be copied to Resources/)
    try:
        import SymLinks
        print("\n--- Removing all symlinks (@Mods/@Patches/@Path/@patched) ---")
        removed = 0
        for overlay_root, label, _ in SymLinks.OVERLAY_PAIRS:
            if overlay_root.exists():
                removed += SymLinks.remove_nav_links_for_pair(overlay_root, label)
                removed += SymLinks.remove_path_links_for_overlay(overlay_root, label)
                removed += SymLinks.remove_patched_links(overlay_root, label)
        if removed > 0:
            print(f"  [OK] Removed {removed} symlinks")
        else:
            print(f"  [OK] No symlinks found")
    except ImportError:
        pass  # SymLinks.py not available, skip

    # Sanity check: BUILD_ROOT looks like SS14
    if not (BUILD_ROOT / "Resources").exists() or not (BUILD_ROOT / "Content.Server").exists():
        print(f"\n[FATAL] BUILD_ROOT doesn't look like an SS14 build: {BUILD_ROOT}", file=sys.stderr)
        sys.exit(2)

    # Idempotency check: if .applied exists and head_commit matches, abort early
    # (Apply was already run; running again would block on conflict check)
    if APPLIED_FILE.exists() and not args.reapply:
        try:
            applied_info = json.loads(APPLIED_FILE.read_text(encoding="utf-8"))
            applied_commit = applied_info.get("head_commit", "")
            current_commit, _, _ = run("git rev-parse HEAD", cwd=BUILD_ROOT)
            if applied_commit and applied_commit == current_commit:
                print(f"\n[INFO] Already applied at commit {current_commit[:12]}")
                print(f"  Applied at: {applied_info.get('applied_at', '?')}")
                print(f"  Patches: {len(applied_info.get('cs_patches_applied', []))} cs+xaml, "
                      f"{len(applied_info.get('robust_patches_applied', []))} robust")
                print(f"\n  To re-apply: python3 Apply.py --reapply")
                print(f"  To revert:   python3 Clear.py")
                return  # exit 0
            else:
                print(f"\n[WARN] .applied exists but HEAD moved ({applied_commit[:12]} → {current_commit[:12]})")
                print(f"  Running Clear.py first, then re-applying...")
                # Run Clear.py to revert upstream, then proceed
                from subprocess import run as sp_run
                sp_run(
                    ["python3", str(SCRIPT_DIR / "Clear.py")],
                    cwd=BUILD_ROOT, check=False
                )
        except (json.JSONDecodeError, OSError) as e:
            print(f"\n[WARN] .applied is corrupted: {e}")
            print(f"  Proceeding with Apply (state will be overwritten)")

    # Acquire lock
    if not acquire_lock():
        print(f"\n[FATAL] Another Apply.py is already running.", file=sys.stderr)
        print(f"  If you're sure no other instance is running, remove: {LOCK_FILE}", file=sys.stderr)
        sys.exit(1)

    try:
        # Sanity check: overlay is not empty
        total_files = 0
        for d in (PATCHES_DIR, MODS_DIR, CS_PATCHES_DIR, CS_MODS_DIR,
                  ROBUST_PATCHES_DIR, ROBUST_MODS_DIR):
            if d.exists():
                total_files += sum(1 for _ in d.rglob("*")
                                   if _.is_file() and not _.name.startswith(".gitkeep"))
        if total_files == 0 and not MANIFEST.exists() and not DELETES_DIR.exists():
            print(f"\n[FATAL] Overlay is empty — nothing to apply.", file=sys.stderr)
            print(f"  Run Migrate.py first: python3 {SCRIPT_DIR.name}/Migrate.py --clean", file=sys.stderr)
            sys.exit(2)

        # Clean up temp snapshots from Check.py BEFORE anything else
        # (.conflict.upstream, .old.patched, .conflict.patched)
        # These are created by Check.py during conflict detection and must be removed
        # before validate_overlay_placement() sees them and thinks they're misplaced.
        cleaned = 0
        for d in (PATCHES_DIR, CS_PATCHES_DIR, ROBUST_PATCHES_DIR):
            if not d.exists():
                continue
            for suffix in TEMP_SNAPSHOT_SUFFIXES:
                for f in d.rglob(f"*{suffix}"):
                    if f.is_file():
                        f.unlink()
                        cleaned += 1
        if cleaned > 0:
            print(f"  [DEL] {cleaned} temp snapshot(s) cleaned")

        # Validate overlay placement (Mods/Patches swap detection)
        swap_warnings = validate_overlay_placement()
        if swap_warnings:
            print(f"\n[WARNING] {len(swap_warnings)} file(s) may be in the wrong overlay folder:")
            for w in swap_warnings[:10]:
                print(w, file=sys.stderr)
            if len(swap_warnings) > 10:
                print(f"  ... and {len(swap_warnings) - 10} more", file=sys.stderr)
            print(f"\n  Files in Patches/ should exist in upstream (they REPLACE upstream files).", file=sys.stderr)
            print(f"  Files in Mods/ should NOT exist in upstream (they are NEW files).", file=sys.stderr)
            print(f"  Continuing anyway in 3s... (Ctrl+C to abort)\n", file=sys.stderr)
            try:
                import time
                time.sleep(3)
            except KeyboardInterrupt:
                sys.exit(1)

        # 0. Check for unresolved conflicts
        if not args.force:
            print("\n--- [0/6] Check for unresolved conflicts ---")
            if not check_conflicts():
                sys.exit(1)
            print("  [OK] No unresolved conflicts")
        else:
            print("\n--- [0/6] Check for unresolved conflicts (--force, skipped) ---")

        # 1. Delete conflicts + manual deletions (stale removal disabled — too annoying)
        print("\n--- [1/6] Delete conflicting + manual deletions ---")
        deleted = delete_conflicts()
        manual = delete_manual()
        print(f"  Total: {len(deleted)} conflicts, {len(manual)} manual")

        # 2. Copy Patches/ → Resources/
        print("\n--- [2/6] Copy Patches/ → Resources/ ---")
        patches_count = copy_tree(PATCHES_DIR, "Patches") if PATCHES_DIR.exists() else 0

        # 3. Copy Mods/ → Resources/
        print("\n--- [3/6] Copy Mods/ → Resources/ ---")
        mods_count = copy_tree(MODS_DIR, "Mods") if MODS_DIR.exists() else 0

        # 4. Apply .cs.patch AND .xaml.patch (from Aavikko.Content/Patches/)
        #    + Copy Content Mods (.cs files) → Content.*/Aavikko/
        print("\n--- [4/6] Content overlay (patches + mods) ---")
        # Load skip decisions from .conflict_decisions.yml (honors 's' decisions)
        skip_paths = load_skip_decisions()
        if skip_paths:
            print(f"  [INFO] {len(skip_paths)} patch(es) marked as 's' (skip) — will not apply")

        # 4a. Copy Content Mods (.cs files) → Content.*/Aavikko/
        #     SDK-style csproj picks them up automatically — NO csproj patch needed
        content_mods_count = copy_content_mods()

        # 4b. Apply .cs.patch / .xaml.patch (modifications to upstream files)
        all_patches = []
        if CS_PATCHES_DIR.exists():
            all_patches.extend(sorted(CS_PATCHES_DIR.rglob("*.cs.patch")))
            all_patches.extend(sorted(CS_PATCHES_DIR.rglob("*.xaml.patch")))
        applied_patches = []
        skipped_patches = []
        failed_patches = []
        for patch in all_patches:
            if apply_cs_patch(patch, skip_paths=skip_paths, cwd=BUILD_ROOT):
                applied_patches.append(str(patch.relative_to(BUILD_ROOT)))
            else:
                failed_patches.append(str(patch.relative_to(BUILD_ROOT)))
        print(f"  Applied: {len(applied_patches)}/{len(all_patches)}")
        if failed_patches:
            print(f"  FAILED: {len(failed_patches)}")
            for p in failed_patches:
                print(f"    {p}")

        # 5. RobustToolbox overlay: Mods + Patches
        print("\n--- [5/6] RobustToolbox overlay ---")
        robust_mods_count = copy_robust_mods()
        robust_patches = []
        if ROBUST_PATCHES_DIR.exists():
            robust_patches.extend(sorted(ROBUST_PATCHES_DIR.rglob("*.cs.patch")))
            robust_patches.extend(sorted(ROBUST_PATCHES_DIR.rglob("*.xaml.patch")))
            # Filter out .gitkeep
            robust_patches = [p for p in robust_patches if not p.name.startswith(".gitkeep")]
        applied_robust = []
        failed_robust = []
        for patch in robust_patches:
            if apply_cs_patch(patch, skip_paths=skip_paths,
                              cwd=ROBUST_DIR, upstream_prefix="RobustToolbox/"):
                applied_robust.append(str(patch.relative_to(BUILD_ROOT)))
            else:
                failed_robust.append(str(patch.relative_to(BUILD_ROOT)))
        if robust_patches:
            print(f"  Robust patches: {len(applied_robust)}/{len(robust_patches)} applied")
            if failed_robust:
                print(f"  FAILED: {len(failed_robust)}")
                for p in failed_robust:
                    print(f"    {p}")
        elif robust_mods_count == 0:
            print("  (no RobustToolbox overlay — skipped)")
        failed_patches.extend(failed_robust)

        # 6. Write .applied (with head_commit for Clear.py)
        # Use atomic write to prevent corruption on Ctrl+C / disk full
        head_commit, _, _ = run("git rev-parse HEAD", cwd=BUILD_ROOT)
        _applied_data = {
            "schema_version": 4,  # v4: content_mods_copied added, csproj patches removed
            "applied_at": datetime.now(timezone.utc).isoformat(),
            "head_commit": head_commit,
            "deleted": deleted,
            "stale_removed": [],
            "manual_deleted": manual,
            "patches_copied": patches_count,
            "mods_copied": mods_count,
            "content_mods_copied": content_mods_count,
            "robust_mods_copied": robust_mods_count,
            "cs_patches_applied": applied_patches,
            "robust_patches_applied": applied_robust,
            "cs_patches_skipped": sorted(skip_paths) if skip_paths else [],
            "cs_patches_failed": failed_patches,
        }
        _atomic_write_text(APPLIED_FILE, json.dumps(_applied_data, indent=2))

        # Create all symlinks after successful apply
        # (only if no failures — broken symlinks worse than no symlinks)
        if not failed_patches:
            try:
                import SymLinks
                print("\n--- Creating symlinks (@Mods/@Patches/@Path/@patched) ---")
                for overlay_root, label, build_target in SymLinks.OVERLAY_PAIRS:
                    if overlay_root.exists():
                        # @Mods/@Patches (cross-navigation between Mods and Patches)
                        SymLinks.create_nav_links_for_pair(overlay_root, label)
                        # @Path (overlay → build tree, for navigation)
                        SymLinks.create_path_links_for_overlay(
                            overlay_root, label, build_target)
                        # @patched (.cs.patch → patched .cs file in build)
                        SymLinks.create_patched_links(
                            overlay_root, label, build_target)
                print("  [OK] Symlinks created")
            except ImportError:
                pass  # SymLinks.py not available

        print(f"\n{'=' * 70}")
        if failed_patches:
            print(f"Apply complete with {len(failed_patches)} failure(s)")
            print(f"  {len(deleted)} deleted, {len(manual)} manual, "
                  f"{patches_count} res-patches, {mods_count} res-mods, "
                  f"{len(applied_patches)}/{len(all_patches)} cs+xaml patches, "
                  f"{len(applied_robust)}/{len(robust_patches)} robust patches, "
                  f"{robust_mods_count} robust mods")
            print(f"  WARNING: {len(failed_patches)} patch(es) failed — see above")
            sys.exit(1)
        print(f"Done! {len(deleted)} deleted, {len(manual)} manual, "
              f"{patches_count} res-patches, {mods_count} res-mods, "
              f"{len(applied_patches)}/{len(all_patches)} cs+xaml patches, "
              f"{len(applied_robust)}/{len(robust_patches)} robust patches, "
              f"{robust_mods_count} robust mods")
        print(f"{'=' * 70}")
        print("\nNext: dotnet build Content.Server --no-restore")

    finally:
        release_lock()


if __name__ == "__main__":
    main()
