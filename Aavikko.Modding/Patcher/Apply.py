#!/usr/bin/env python3
"""
Apply.py — apply Aavikko mod overlay to upstream SS14 build.

Pipeline:
  0. Check for unresolved conflicts (Check.py --apply-check)
  1. Delete upstream files (manifest delete: section)
  2. Copy Patches/ → Resources/ (overwrite upstream)
  3. Copy Mods/ → Resources/ (add new content)
  4. Apply .cs.patch + .xaml.patch via git apply
  5. Write .applied marker (with head_commit for Clear.py)

Security:
  - safe_resolve_under() prevents path traversal via manifest.yml
  - File lock prevents concurrent Apply.py runs
  - Empty overlay check prevents false success

Note: The Deletes/ folder feature was removed. To "delete" an upstream
file, create a .patch that empties it (with a comment explaining why) or use
the manifest.yml `delete:` section.
"""
from __future__ import annotations

import argparse
import json
import os
import shlex
import shutil
import subprocess
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

# Force UTF-8 for stdout/stderr — Windows default is cp1251 which can't encode
# Unicode characters like →, —, ✓, ⚠ used in print() statements.
# Without this, Apply.py crashes with UnicodeEncodeError on Windows PowerShell.
# Python 3.7+ supports sys.stdout.reconfigure().
if hasattr(sys.stdout, 'reconfigure'):
    try:
        sys.stdout.reconfigure(encoding='utf-8', errors='replace')
        sys.stderr.reconfigure(encoding='utf-8', errors='replace')
    except (OSError, ValueError):
        pass  # Fails on some streams (e.g., redirected to file) — ignore

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


def run(cmd, cwd: Path | None = None, timeout: int | None = None) -> tuple[str, str, int]:
    """Run a command. Accepts either a string (shell-style) OR an argv list.

    Cross-platform: never uses shell=True. If `cmd` is a string, it's split
    via shlex.split (which can BREAK on Windows paths with backslashes if
    they're not properly quoted). For safety, prefer passing an argv list:

        run(["git", "apply", "--check", str(patch_path)])  # SAFE
        run("git apply --check 'path'")                    # OK if quoted
        run("git rev-parse HEAD")                          # OK (no paths)

    The argv list form is 100% cross-platform — no shell, no quoting,
    no backslash interpretation. Always prefer it for commands with paths.
    """
    if isinstance(cmd, str):
        try:
            argv = shlex.split(cmd)
        except ValueError:
            # Fallback for edge cases (unbalanced quotes) — use shell
            result = subprocess.run(
                cmd, shell=True, cwd=cwd, capture_output=True,
                text=True, encoding="utf-8", errors="replace",
                timeout=timeout
            )
            return result.stdout.strip(), result.stderr.strip(), result.returncode
        if not argv:
            return "", "", 0
    else:
        argv = list(cmd)
    result = subprocess.run(
        argv, cwd=cwd, capture_output=True,
        text=True, encoding="utf-8", errors="replace",
        timeout=timeout
    )
    return result.stdout.strip(), result.stderr.strip(), result.returncode


def run_git_with_lock_retry(cmd, cwd: Path | None = None,
                            max_retries: int = 5, retry_delay: float = 1.0,
                            timeout: int | None = None) -> tuple[str, str, int]:
    """Run a git command with retry on index.lock conflict.

    Accepts either a string OR an argv list (delegates to run()).

    Git returns rc=128 with message "Unable to create '.git/index.lock': File exists"
    when another git process (Status.py polling, VS Code Source Control view, etc.)
    is running concurrently. This is a common race condition when:
      - VS Code extension polls Status.py every 15 sec (which calls git status)
      - Apply.py runs git apply (which writes to .git/index.lock)
      - User has VS Code Source Control panel open

    Strategy: detect index.lock error, wait 1 sec, retry. Up to 5 times.
    """
    last_stdout, last_stderr, last_rc = "", "", 0
    for attempt in range(max_retries):
        last_stdout, last_stderr, last_rc = run(cmd, cwd=cwd, timeout=timeout)
        if last_rc == 0:
            return last_stdout, last_stderr, last_rc
        # Check if this is the index.lock error
        if "index.lock" not in last_stderr and "index.lock" not in (last_stdout or ""):
            # Different error — don't retry, return immediately
            return last_stdout, last_stderr, last_rc
        # index.lock conflict — wait and retry
        if attempt < max_retries - 1:
            print(f"  [INFO] git index.lock busy, retry {attempt+1}/{max_retries} in {retry_delay}s...",
                  file=sys.stderr)
            time.sleep(retry_delay)
    return last_stdout, last_stderr, last_rc


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


# ── Copy operations ────────────────────────────────────────────────────────


def _copy_with_retry(src: Path, dst: Path, max_retries: int = 3) -> bool:
    """Copy a file with retry on PermissionError (Windows file locking).

    On Windows, if a file is open in VS Code (or another editor), shutil.copy
    may fail with PermissionError: [Errno 13] Permission denied. This function
    retries up to max_retries times with 0.5s delay between attempts.

    Returns True if copy succeeded, False if all retries failed.
    """
    for attempt in range(max_retries):
        try:
            shutil.copy(src, dst)
            return True
        except PermissionError as e:
            if attempt < max_retries - 1:
                # File may be locked by VS Code or antivirus — wait and retry
                print(f"  [WARN] Permission denied copying {src.name} (attempt {attempt+1}/{max_retries}), retrying in 0.5s...",
                      file=sys.stderr)
                time.sleep(0.5)
            else:
                print(f"  [FAIL] Could not copy {src.name} after {max_retries} attempts: {e}",
                      file=sys.stderr)
                return False
        except OSError as e:
            # Other OS errors (disk full, path too long, etc.) — don't retry
            print(f"  [FAIL] Could not copy {src.name}: {e}", file=sys.stderr)
            return False
    return False


def copy_tree(src_dir: Path, label: str) -> int:
    """Copy all files from src_dir to Resources/ (overwrite).
    Skips symlinks and temp snapshot files from Check.py.

    Uses _copy_with_retry() for PermissionError handling (Windows file locking
    when files are open in VS Code)."""
    count = 0
    skipped_symlinks = 0
    skipped_temps = 0
    failed_copies = 0
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
        if _copy_with_retry(src, dst):
            count += 1
        else:
            failed_copies += 1
        if count % 500 == 0:
            print(f"    ... {count}/{len(files)}")
    if skipped_symlinks:
        print(f"  [WARN] {skipped_symlinks} symlink(s) skipped", file=sys.stderr)
    if skipped_temps:
        print(f"  [SKIP] {skipped_temps} temp snapshot(s) skipped", file=sys.stderr)
    if failed_copies:
        print(f"  [FAIL] {failed_copies} file(s) could not be copied (PermissionError?)", file=sys.stderr)
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

    # Use argv list form — 100% cross-platform safe (no shlex.split/quoting).
    # On Windows, shlex.split can mangle backslashes in paths if not properly
    # quoted. Passing argv directly avoids all shell interpretation.
    #
    # CRITICAL: Use `git -c core.autocrlf=false apply` on Windows.
    # On Windows with core.autocrlf=true, git converts LF→CRLF in working tree
    # files during checkout. But .cs.patch files contain LF (created on Linux).
    # When `git apply` compares patch context (LF) with file content (CRLF),
    # it FAILS with "patch does not apply" even though the content matches.
    # Disabling autocrlf for this specific command fixes it.
    # On Linux/macOS this is a no-op (autocrlf is already false).
    patch_path_str = str(patch_path)
    stdout, stderr, rc = run_git_with_lock_retry(
        ["git", "-c", "core.autocrlf=false", "apply", "--check", patch_path_str], cwd=cwd)
    if rc == 0:
        stdout, stderr, rc = run_git_with_lock_retry(
            ["git", "-c", "core.autocrlf=false", "apply", patch_path_str], cwd=cwd)
        if rc == 0:
            print(f"  [OK] {patch_path.name}")
            return True
        print(f"  [FAIL] {patch_path.name}: git apply failed after --check passed")
        print(f"         stderr: {stderr[:300]}", file=sys.stderr)
        return False
    stdout2, stderr2, rc2 = run_git_with_lock_retry(
        ["git", "-c", "core.autocrlf=false", "apply", "--reverse", "--check", patch_path_str], cwd=cwd)
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

    Timeout: 120 seconds for Check.py (sha256 scanning of 5000+ files can be slow).
    """
    state_file = SCRIPT_DIR / ".upstream_state.json"
    check_script = str(SCRIPT_DIR / "Check.py")

    if not state_file.exists():
        print("  [INFO] No .upstream_state.json found — recording baseline (first-time setup)")
        # Use sys.executable (NOT "python3") for cross-platform compat.
        # Use argv list form — 100% cross-platform safe (no shlex.split/quoting).
        # Use -X utf8 flag to force UTF-8 mode in child process (Windows cp1251 fix).
        stdout, stderr, rc = run(
            [sys.executable, "-X", "utf8", check_script, "--baseline"],
            cwd=BUILD_ROOT,
            timeout=300  # 5 min for baseline (scans all files)
        )
        if rc != 0:
            print(f"\n[FATAL] Failed to record baseline via Check.py --baseline", file=sys.stderr)
            if stderr:
                print(f"  stderr: {stderr[:500]}", file=sys.stderr)
            return False
        print("  [OK] Baseline recorded")
        return True

    print("  [INFO] Running conflict check (can take 30-60s for large builds)...")
    try:
        stdout, stderr, rc = run(
            [sys.executable, "-X", "utf8", check_script, "--apply-check"],
            cwd=BUILD_ROOT,
            timeout=300  # 5 min (was 2 min) — Windows HDD can be slow on first run
        )
    except subprocess.TimeoutExpired:
        print("  [WARN] Conflict check timed out (300s) — continuing with --force behaviour")
        return True
    if rc != 0:
        print(f"\n[BLOCKED] Unresolved conflicts detected — Apply.py cannot run.")
        print(f"  Run: python {SCRIPT_DIR.name}/Check.py")
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
    parser.add_argument("--strict", action="store_true",
                        help="Run validate_overlay_placement() sanity check (slow on HDD)")
    args = parser.parse_args()

    print("=" * 70)
    print("Aavikko Mod Apply")
    print("=" * 70)

    # Remove ALL symlinks before applying (they would be copied to Resources/)
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
                    [sys.executable, "-X", "utf8", str(SCRIPT_DIR / "Clear.py")],
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
        # Fast version: use next(rglob, None) — returns on first file found,
        # avoids walking entire 5000+ file tree just to count.
        overlay_dirs = (PATCHES_DIR, MODS_DIR, CS_PATCHES_DIR, CS_MODS_DIR,
                        ROBUST_PATCHES_DIR, ROBUST_MODS_DIR)
        has_any_file = False
        for d in overlay_dirs:
            if d.exists():
                if next((f for f in d.rglob("*") if f.is_file() and not f.name.startswith(".gitkeep")), None) is not None:
                    has_any_file = True
                    break
        if not has_any_file and not MANIFEST.exists():
            print(f"\n[FATAL] Overlay is empty — nothing to apply.", file=sys.stderr)
            print(f"  Run Migrate.py first: python3 {SCRIPT_DIR.name}/Migrate.py --clean", file=sys.stderr)
            sys.exit(2)
        # --- END: was inside try: ---

        # Clean up temp snapshots from Check.py BEFORE anything else
        # (.conflict.upstream, .old.patched, .conflict.patched)
        # Single-walk optimization: walk tree ONCE, check all suffixes per file
        # (old version walked tree 3 times — once per suffix).
        cleaned = 0
        for d in (PATCHES_DIR, CS_PATCHES_DIR, ROBUST_PATCHES_DIR):
            if not d.exists():
                continue
            for f in d.rglob("*"):
                if not f.is_file():
                    continue
                if any(f.name.endswith(suf) for suf in TEMP_SNAPSHOT_SUFFIXES):
                    try:
                        f.unlink()
                        cleaned += 1
                    except OSError:
                        pass
        if cleaned > 0:
            print(f"  [DEL] {cleaned} temp snapshot(s) cleaned")

        # Validate overlay placement (Mods/Patches swap detection)
        # OPTIMIZATION: skipped by default — it walks all 5000+ files twice
        # (once for Patches, once for Mods) and checks upstream existence per file.
        # On HDD this adds 30-60s. Use --strict to enable.
        if args.strict:
            swap_warnings = validate_overlay_placement()
            if swap_warnings:
                print(f"\n[WARNING] {len(swap_warnings)} file(s) may be in the wrong overlay folder:")
                for w in swap_warnings[:10]:
                    print(w, file=sys.stderr)
                if len(swap_warnings) > 10:
                    print(f"  ... and {len(swap_warnings) - 10} more", file=sys.stderr)
                print(f"\n  Files in Patches/ should exist in upstream (they REPLACE upstream files).", file=sys.stderr)
                print(f"  Files in Mods/ should NOT exist in upstream (they are NEW files).", file=sys.stderr)
                print(f"\n  Continuing anyway in 3s... (Ctrl+C to abort)\n", file=sys.stderr)
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

        # 1. Delete conflicts (manifest.yml `delete:` section only)
        #    The old Deletes/ folder feature was removed — use empty-content
        #    .patch files instead if you need to "blank out" an upstream file.
        print("\n--- [1/6] Delete conflicting files (manifest.yml) ---")
        deleted = delete_conflicts()
        print(f"  Total: {len(deleted)} conflicts deleted")

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
            "schema_version": 5,  # v5: Deletes/ folder removed (manual_deleted field dropped)
            "applied_at": datetime.now(timezone.utc).isoformat(),
            "head_commit": head_commit,
            "deleted": deleted,
            "stale_removed": [],
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
                print("\n--- Creating @patched symlinks ---")
                for overlay_root, label, build_target in SymLinks.OVERLAY_PAIRS:
                    if overlay_root.exists():
                        # Only @patched (.cs.patch → patched .cs file in build)
                        SymLinks.create_patched_links(
                            overlay_root, label, build_target)
                print("  [OK] Symlinks created")
            except ImportError:
                pass  # SymLinks.py not available

        print(f"\n{'=' * 70}")
        if failed_patches:
            print(f"Apply complete with {len(failed_patches)} failure(s)")
            print(f"  {len(deleted)} deleted, "
                  f"{patches_count} res-patches, {mods_count} res-mods, "
                  f"{len(applied_patches)}/{len(all_patches)} cs+xaml patches, "
                  f"{len(applied_robust)}/{len(robust_patches)} robust patches, "
                  f"{robust_mods_count} robust mods")
            print(f"  WARNING: {len(failed_patches)} patch(es) failed — see above")
            sys.exit(1)
        print(f"Done! {len(deleted)} deleted, "
              f"{patches_count} res-patches, {mods_count} res-mods, "
              f"{len(applied_patches)}/{len(all_patches)} cs+xaml patches, "
              f"{len(applied_robust)}/{len(robust_patches)} robust patches, "
              f"{robust_mods_count} robust mods")
        print(f"{'=' * 70}")
        print("\nNext: dotnet build Content.Server --no-restore")

    finally:
        release_lock()


def _print_crash_diagnostics(exc: Exception) -> None:
    """Print detailed crash info so user can report the exact error.

    When Apply.py crashes with an unexpected exception (OSError, PermissionError,
    UnicodeDecodeError, etc.), the default Python traceback is often cryptic.
    This function prints a user-friendly summary + the full traceback, so
    the user can copy-paste it to the developer.
    """
    import traceback
    print(f"\n{'=' * 70}", file=sys.stderr)
    print(f"[CRASH] Apply.py failed with unexpected error", file=sys.stderr)
    print(f"{'=' * 70}", file=sys.stderr)
    print(f"  Error type: {type(exc).__name__}", file=sys.stderr)
    print(f"  Error message: {exc}", file=sys.stderr)
    print(f"  Python: {sys.version.split()[0]}", file=sys.stderr)
    print(f"  Platform: {sys.platform}", file=sys.stderr)
    print(f"  BUILD_ROOT: {BUILD_ROOT}", file=sys.stderr)
    print(f"  SCRIPT_DIR: {SCRIPT_DIR}", file=sys.stderr)
    # Check if .applied exists (partial state?)
    if APPLIED_FILE.exists():
        print(f"  .applied: EXISTS (Apply may have partially completed)", file=sys.stderr)
    else:
        print(f"  .applied: missing", file=sys.stderr)
    # Check if .apply.lock exists (stale lock?)
    if LOCK_FILE.exists():
        print(f"  .apply.lock: EXISTS (may need manual removal)", file=sys.stderr)
    print(f"\n  Full traceback:", file=sys.stderr)
    traceback.print_exc(file=sys.stderr)
    print(f"\n{'=' * 70}", file=sys.stderr)
    print(f"  Please copy the above output and report to the developer.", file=sys.stderr)
    print(f"  Include: error type, message, traceback, and what you did before.", file=sys.stderr)
    print(f"{'=' * 70}", file=sys.stderr)


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        print("\n[ABORTED] Apply.py interrupted by user (Ctrl+C)", file=sys.stderr)
        sys.exit(130)
    except SystemExit:
        raise  # Don't catch sys.exit()
    except Exception as e:
        _print_crash_diagnostics(e)
        sys.exit(1)
