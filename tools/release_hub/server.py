"""
Deadlock Playground - Release Hub Server
Automated release pipeline orchestrator for:
  1. Updating website changelog.json & Cloudflare Pages auto-deploy
  2. Git commit, branch push & release tagging
  3. GitHub Release creation & binary asset uploading (with progress)
  4. GameBanana Assisted Release (clipboard, explorer reveal & browser launch)
"""

import os
import sys
import json
import time
import queue
import shutil
import hashlib
import zipfile
import threading
import subprocess
import webbrowser
import traceback
from datetime import datetime, timezone
import urllib.request
import urllib.error
import urllib.parse
from http.server import HTTPServer, BaseHTTPRequestHandler
from socketserver import ThreadingMixIn

class ThreadedHTTPServer(ThreadingMixIn, HTTPServer):
    daemon_threads = True
    allow_reuse_address = True

WORKSPACE_DIR = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
RELEASE_HUB_DIR = os.path.dirname(__file__)
CONFIG_PATH = os.path.join(RELEASE_HUB_DIR, "release_config.json")
CHANGELOG_PATH = os.path.join(WORKSPACE_DIR, "web", "src", "data", "changelog.json")
PORT = 5173

# Global SSE message queue & history
log_clients = []
log_lock = threading.Lock()
pipeline_running = False
recent_events = []
MAX_RECENT_EVENTS = 300

DEFAULT_CONFIG = {
    "repo_owner": "juliancz-a",
    "repo_name": "deadlock-playground",
    "github_token": "",
    "gamebanana_id": "24316",
    "gamebanana_url": "https://gamebanana.com/tools/edit/24316",
    "default_branch": "main",
    "search_paths": [
        os.path.join(WORKSPACE_DIR, "releases"),
        "D:\\GameDev\\releases",
    ]
}

def load_config():
    if os.path.exists(CONFIG_PATH):
        try:
            with open(CONFIG_PATH, "r", encoding="utf-8") as f:
                data = json.load(f)
                cfg = DEFAULT_CONFIG.copy()
                cfg.update(data)
                return cfg
        except Exception as e:
            print(f"[Config] Error loading config: {e}")
    return DEFAULT_CONFIG.copy()

def save_config(cfg):
    try:
        with open(CONFIG_PATH, "w", encoding="utf-8") as f:
            json.dump(cfg, f, indent=2)
        return True
    except Exception as e:
        print(f"[Config] Error saving config: {e}")
        return False

def broadcast_event(event_type, **kwargs):
    payload = {
        "type": event_type,
        "timestamp": datetime.now().strftime("%H:%M:%S"),
        **kwargs
    }
    msg = f"data: {json.dumps(payload)}\n\n"
    with log_lock:
        recent_events.append(payload)
        if len(recent_events) > MAX_RECENT_EVENTS:
            recent_events.pop(0)
        dead = []
        for q in log_clients:
            try:
                q.put_nowait(msg)
            except Exception:
                dead.append(q)
        for d in dead:
            if d in log_clients:
                log_clients.remove(d)

# Ensure UTF-8 output on Windows consoles
if hasattr(sys.stdout, "reconfigure"):
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

def safe_print(text):
    try:
        print(text)
    except Exception:
        try:
            print(text.encode("ascii", errors="replace").decode("ascii"))
        except Exception:
            pass

def log_info(msg, step=None, progress=None):
    broadcast_event("log", level="info", message=msg, step=step, progress=progress)
    safe_print(f"[INFO] {msg}")

def log_success(msg, step=None, progress=None):
    broadcast_event("log", level="success", message=msg, step=step, progress=progress)
    safe_print(f"[SUCCESS] {msg}")

def log_warn(msg, step=None, progress=None):
    broadcast_event("log", level="warn", message=msg, step=step, progress=progress)
    safe_print(f"[WARN] {msg}")

def log_error(msg, step=None, progress=None):
    broadcast_event("log", level="error", message=msg, step=step, progress=progress)
    safe_print(f"[ERROR] {msg}")

def run_cmd(cmd, cwd=WORKSPACE_DIR, check=True):
    log_info(f"Executing: {' '.join(cmd)}")
    proc = subprocess.Popen(
        cmd,
        cwd=cwd,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        shell=False
    )
    stdout, stderr = proc.communicate()
    if stdout.strip():
        for line in stdout.strip().splitlines():
            log_info(f"  {line}")
    if stderr.strip():
        for line in stderr.strip().splitlines():
            if proc.returncode != 0:
                log_error(f"  {line}")
            else:
                log_warn(f"  {line}")
    if check and proc.returncode != 0:
        raise RuntimeError(f"Command failed with code {proc.returncode}: {' '.join(cmd)}\n{stderr}")
    return stdout.strip()

def copy_to_clipboard(text):
    """Copies text to Windows clipboard reliably with full UTF-8 support."""
    try:
        proc = subprocess.Popen(
            ['powershell', '-NoProfile', '-Command', '[Console]::InputEncoding = [System.Text.Encoding]::UTF8; $input | Set-Clipboard'],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE
        )
        proc.communicate(input=text.encode('utf-8'))
        if proc.returncode == 0:
            return True
    except Exception as e:
        log_warn(f"PowerShell clipboard copy exception: {e}")
        
    try:
        proc = subprocess.Popen(['clip'], stdin=subprocess.PIPE)
        proc.communicate(input=text.encode('utf-16le'))
        return True
    except Exception as e2:
        log_error(f"Failed to copy to clipboard: {e2}")
        return False

def open_file_in_explorer(file_path):
    """Selects and highlights a file in Windows Explorer."""
    try:
        norm_path = os.path.normpath(file_path)
        if os.path.exists(norm_path):
            subprocess.Popen(['explorer.exe', f'/select,{norm_path}'])
            return True
        elif os.path.exists(os.path.dirname(norm_path)):
            subprocess.Popen(['explorer.exe', os.path.dirname(norm_path)])
            return True
    except Exception as e:
        log_warn(f"Could not open explorer: {e}")
    return False

def get_file_sha256(filepath):
    h = hashlib.sha256()
    with open(filepath, "rb") as f:
        while chunk := f.read(65536):
            h.update(chunk)
    return h.hexdigest()

def detect_existing_releases(config):
    """Scans configured paths and returns both a directory tree and a flat list of releases."""
    roots = []
    all_items = []
    
    for base in config.get("search_paths", []):
        if not os.path.exists(base):
            continue
        norm_base = os.path.normpath(base)
        root_entry = {
            "root_path": norm_base,
            "root_name": os.path.basename(norm_base) or norm_base,
            "children": []
        }
        try:
            for entry in os.scandir(norm_base):
                if entry.is_file() and entry.name.endswith((".7z", ".zip")) and "deadlock-playground" in entry.name.lower():
                    try:
                        stat = entry.stat()
                        item = {
                            "name": entry.name,
                            "path": os.path.normpath(entry.path),
                            "size_mb": round(stat.st_size / (1024 * 1024), 2),
                            "modified": datetime.fromtimestamp(stat.st_mtime).strftime("%Y-%m-%d %H:%M"),
                            "is_folder": False
                        }
                        root_entry["children"].append(item)
                        all_items.append(item)
                    except Exception:
                        pass
                elif entry.is_dir():
                    dir_name = entry.name
                    folder_p = os.path.normpath(entry.path)
                    exe_p = os.path.join(folder_p, "deadlock-playground.exe")
                    try:
                        stat = entry.stat()
                        if os.path.exists(exe_p):
                            item = {
                                "name": f"{dir_name} (Build Folder)",
                                "path": folder_p,
                                "size_mb": 0,
                                "modified": datetime.fromtimestamp(stat.st_mtime).strftime("%Y-%m-%d %H:%M"),
                                "is_folder": True,
                                "is_build_folder": True
                            }
                            root_entry["children"].append(item)
                            all_items.append(item)
                        else:
                            sub_items = []
                            for sub in os.scandir(folder_p):
                                if sub.is_file() and sub.name.endswith((".7z", ".zip")) and "deadlock-playground" in sub.name.lower():
                                    s_stat = sub.stat()
                                    sub_item = {
                                        "name": sub.name,
                                        "path": os.path.normpath(sub.path),
                                        "size_mb": round(s_stat.st_size / (1024 * 1024), 2),
                                        "modified": datetime.fromtimestamp(s_stat.st_mtime).strftime("%Y-%m-%d %H:%M"),
                                        "is_folder": False
                                    }
                                    sub_items.append(sub_item)
                                    all_items.append(sub_item)
                                elif sub.is_dir():
                                    sub_exe = os.path.join(sub.path, "deadlock-playground.exe")
                                    if os.path.exists(sub_exe):
                                        s_stat = sub.stat()
                                        sub_item = {
                                            "name": f"{sub.name} (Build Folder)",
                                            "path": os.path.normpath(sub.path),
                                            "size_mb": 0,
                                            "modified": datetime.fromtimestamp(s_stat.st_mtime).strftime("%Y-%m-%d %H:%M"),
                                            "is_folder": True,
                                            "is_build_folder": True
                                        }
                                        sub_items.append(sub_item)
                                        all_items.append(sub_item)
                            if sub_items:
                                sub_items.sort(key=lambda x: x["modified"], reverse=True)
                                root_entry["children"].append({
                                    "name": dir_name,
                                    "path": folder_p,
                                    "is_group": True,
                                    "children": sub_items
                                })
                    except Exception:
                        pass
        except Exception as e:
            print(f"[Detect] Error walking {norm_base}: {e}")
            
        if root_entry["children"]:
            roots.append(root_entry)

    all_items.sort(key=lambda x: x["modified"], reverse=True)
    return {
        "roots": roots,
        "items": all_items
    }

def package_folder_to_zip(folder_path, output_zip_path, progress_callback=None):
    """Zips a folder with progress logging."""
    folder_path = os.path.abspath(folder_path)
    output_zip_path = os.path.abspath(output_zip_path)
    os.makedirs(os.path.dirname(output_zip_path), exist_ok=True)
    
    file_list = []
    total_bytes = 0
    for root, _, files in os.walk(folder_path):
        for f in files:
            full = os.path.join(root, f)
            rel = os.path.relpath(full, folder_path)
            size = os.path.getsize(full)
            file_list.append((full, rel, size))
            total_bytes += size
            
    written_bytes = 0
    with zipfile.ZipFile(output_zip_path, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as zf:
        for full, rel, size in file_list:
            zf.write(full, rel)
            written_bytes += size
            if progress_callback and total_bytes > 0:
                pct = int((written_bytes / total_bytes) * 100)
                progress_callback(pct, rel)
                
    return output_zip_path

# ==============================================================================
# PIPELINE WORKER EXECUTION
# ==============================================================================

def execute_pipeline(payload):
    global pipeline_running
    pipeline_running = True
    config = load_config()
    
    version = payload.get("version", "").strip().lstrip("v")
    title = payload.get("title", "").strip()
    date_str = payload.get("date", "").strip() or datetime.now().strftime("%B %d, %Y")
    highlights = payload.get("highlights", [])
    markdown = payload.get("markdown", "").strip()
    archive_path = payload.get("archive_path", "").strip()
    target_branch = payload.get("target_branch", config.get("default_branch", "main")).strip()
    github_token = payload.get("github_token", "").strip() or config.get("github_token", "")
    dry_run = bool(payload.get("dry_run", False))
    steps = payload.get("steps", {})
    
    tag_name = f"v{version}"
    if "is_prerelease" in payload:
        is_prerelease = bool(payload["is_prerelease"])
    elif payload.get("release_type") == "prerelease":
        is_prerelease = True
    elif payload.get("release_type") == "latest":
        is_prerelease = False
    else:
        is_prerelease = "beta" in version.lower() or "alpha" in version.lower()
    
    broadcast_event("pipeline_start", version=version, title=title, dry_run=dry_run, is_prerelease=is_prerelease)
    log_info(f"=== Starting Deadlock Playground Release Pipeline v{version} ({'Pre-Release' if is_prerelease else 'Latest Release'}) ===", step=0, progress=0)
    if dry_run:
        log_warn("DRY RUN MODE ENABLED: No files will be committed, no tags pushed, no releases published.")
        
    try:
        # ----------------------------------------------------------------------
        # STEP 1: ARCHIVE PACKAGING & VALIDATION
        # ----------------------------------------------------------------------
        log_info("[Step 1/6] Packaging & Validating Binary Release Archive...", step=1, progress=5)
        final_archive = ""
        sha256_hash = ""
        size_mb = 0.0
        
        if archive_path and os.path.exists(archive_path):
            if os.path.isdir(archive_path):
                log_info(f"Selected folder '{archive_path}'. Compressing to ZIP...")
                out_dir = os.path.join(WORKSPACE_DIR, "releases")
                out_zip = os.path.join(out_dir, f"deadlock-playground-v{version}-windows-x64.zip")
                if not dry_run:
                    def on_pack(pct, curr_file):
                        if pct % 20 == 0:
                            log_info(f"  Zipping ({pct}%): {curr_file}", step=1, progress=5 + int(pct * 0.15))
                    package_folder_to_zip(archive_path, out_zip, on_pack)
                    final_archive = out_zip
                else:
                    log_info(f"[Dry Run] Would compress '{archive_path}' -> '{out_zip}'")
                    final_archive = out_zip
            else:
                final_archive = archive_path
                
            if os.path.exists(final_archive) and not dry_run:
                size_mb = round(os.path.getsize(final_archive) / (1024 * 1024), 2)
                log_info("Calculating SHA-256 checksum...")
                sha256_hash = get_file_sha256(final_archive)
                log_success(f"Release Archive Ready: {os.path.basename(final_archive)} ({size_mb} MB)", step=1, progress=20)
                log_info(f"SHA-256: {sha256_hash}")
        else:
            if steps.get("github_release", True):
                raise FileNotFoundError(f"Binary archive or build folder not found at: {archive_path}")
            else:
                log_warn("No binary archive specified (skipping archive validation).", step=1, progress=20)

        # ----------------------------------------------------------------------
        # STEP 2: UPDATE WEBSITE CHANGELOG.JSON
        # ----------------------------------------------------------------------
        if steps.get("update_changelog", True):
            log_info("[Step 2/6] Updating web/src/data/changelog.json...", step=2, progress=25)
            if not os.path.exists(CHANGELOG_PATH):
                raise FileNotFoundError(f"Changelog file not found at: {CHANGELOG_PATH}")
                
            with open(CHANGELOG_PATH, "r", encoding="utf-8") as f:
                changelog_data = json.load(f)
                
            full_title = f"Version {version} — {title}" if not title.startswith("Version") else title
            
            badge_title = "Pre-Release" if is_prerelease else "Latest Release"
            badge_color = "slate" if is_prerelease else "gold"
            
            # Format history item
            new_history_item = {
                "version": version,
                "date": date_str,
                "title": full_title,
                "badge": badge_title,
                "badgeType": badge_color,
                "videoUrl": "",
                "highlights": [h.strip() for h in highlights if h.strip()]
            }
            
            history = changelog_data.get("history", [])
            # Only demote previous latest and update latestVersion if this release is marked as Latest
            if not is_prerelease:
                for item in history:
                    if item.get("badge") == "Latest Release":
                        item["badge"] = "Release"
                        item["badgeType"] = "slate"
                changelog_data["latestVersion"] = version
                changelog_data["publishedAt"] = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
                changelog_data["title"] = full_title
                changelog_data["markdown"] = markdown or f"### {full_title}\n\n" + "\n".join(f"* {h}" for h in highlights)
                    
            history.insert(0, new_history_item)
            changelog_data["history"] = history
            
            if not dry_run:
                with open(CHANGELOG_PATH, "w", encoding="utf-8") as f:
                    json.dump(changelog_data, f, indent=2, ensure_ascii=False)
                log_success(f"Successfully updated {CHANGELOG_PATH} with version {version} ({badge_title})!", step=2, progress=40)
            else:
                log_info(f"[Dry Run] Prepared updated changelog JSON for version {version}", step=2, progress=40)
        else:
            log_info("[Step 2/6] Skipped website changelog update as requested.", step=2, progress=40)

        # ----------------------------------------------------------------------
        # STEP 3: GIT COMMIT & PUSH TO BRANCH
        # ----------------------------------------------------------------------
        if steps.get("git_commit", True):
            log_info(f"[Step 3/6] Staging & Committing changes to git branch '{target_branch}'...", step=3, progress=45)
            if not dry_run:
                # Stage changelog
                run_cmd(["git", "add", "web/src/data/changelog.json"])
                commit_msg = f"release: bump version to {tag_name} - {title}"
                # Check if there is anything to commit
                status_out = run_cmd(["git", "status", "--porcelain"])
                if "web/src/data/changelog.json" in status_out:
                    run_cmd(["git", "commit", "-m", commit_msg])
                    log_success(f"Committed: '{commit_msg}'", step=3, progress=50)
                else:
                    log_info("Changelog already committed or clean.", step=3, progress=50)
                    
                if steps.get("git_push", True):
                    log_info(f"Pushing commit to origin/{target_branch} (triggers Cloudflare Pages deploy)...")
                    run_cmd(["git", "push", "origin", target_branch])
                    log_success(f"Pushed to origin/{target_branch}! Cloudflare Pages deployment started.", step=3, progress=60)
            else:
                log_info(f"[Dry Run] Would commit web/src/data/changelog.json and push to origin/{target_branch}", step=3, progress=60)
        else:
            log_info("[Step 3/6] Skipped git commit/push.", step=3, progress=60)

        # ----------------------------------------------------------------------
        # STEP 4: GIT TAGGING
        # ----------------------------------------------------------------------
        if steps.get("git_tag", True):
            log_info(f"[Step 4/6] Creating & pushing Git Tag '{tag_name}'...", step=4, progress=65)
            tag_msg = f"Release {tag_name}: {title}"
            if not dry_run:
                # Check if tag already exists
                existing_tags = run_cmd(["git", "tag", "-l", tag_name], check=False)
                if tag_name in existing_tags:
                    log_warn(f"Tag '{tag_name}' already exists locally. Forcing tag update...")
                    run_cmd(["git", "tag", "-fa", tag_name, "-m", tag_msg])
                else:
                    run_cmd(["git", "tag", "-a", tag_name, "-m", tag_msg])
                log_success(f"Created annotated tag: {tag_name}", step=4, progress=70)
                
                if steps.get("git_push", True):
                    log_info(f"Pushing tag '{tag_name}' to origin...")
                    run_cmd(["git", "push", "origin", tag_name, "--force"])
                    log_success(f"Tag '{tag_name}' pushed to GitHub!", step=4, progress=75)
            else:
                log_info(f"[Dry Run] Would create tag '{tag_name}' and push to origin", step=4, progress=75)
        else:
            log_info("[Step 4/6] Skipped git tagging.", step=4, progress=75)

        # ----------------------------------------------------------------------
        # STEP 5: GITHUB RELEASE CREATION & ASSET UPLOAD
        # ----------------------------------------------------------------------
        if steps.get("github_release", True):
            rel_label = "Pre-Release" if is_prerelease else "Latest Release"
            log_info(f"[Step 5/6] Creating GitHub Release ({rel_label}) for tag '{tag_name}'...", step=5, progress=80)
            if not github_token and not dry_run:
                raise ValueError("GitHub Personal Access Token is required to create a GitHub Release. Enter it in settings.")
            elif not github_token and dry_run:
                log_warn("[Dry Run] No GitHub token provided; simulating release creation without authentication.")
                
            owner = config.get("repo_owner", "juliancz-a")
            repo = config.get("repo_name", "deadlock-playground")
            api_url = f"https://api.github.com/repos/{owner}/{repo}/releases"
            
            body_content = markdown if markdown else f"### Version {version} — {title}\n\n" + "\n".join(f"* {h}" for h in highlights)
            if sha256_hash:
                body_content += f"\n\n---\n**Binary Verification:**\n* File: `{os.path.basename(final_archive)}`\n* SHA-256: `{sha256_hash}`"
                
            req_data = {
                "tag_name": tag_name,
                "target_commitish": target_branch,
                "name": f"Version {version} — {title}",
                "body": body_content,
                "draft": False,
                "prerelease": is_prerelease,
                "make_latest": "false" if is_prerelease else "true"
            }
            
            if not dry_run:
                # 1. Create or Find existing Release
                headers = {
                    "Authorization": f"Bearer {github_token}",
                    "Accept": "application/vnd.github+json",
                    "User-Agent": "DeadlockPlayground-ReleaseHub",
                    "Content-Type": "application/json"
                }
                
                req = urllib.request.Request(api_url, data=json.dumps(req_data).encode("utf-8"), headers=headers, method="POST")
                release_info = None
                try:
                    with urllib.request.urlopen(req) as resp:
                        release_info = json.loads(resp.read().decode("utf-8"))
                        log_success(f"Created GitHub Release ({rel_label}): {release_info.get('html_url')}", step=5, progress=85)
                except urllib.error.HTTPError as he:
                    if he.code == 422: # Already exists
                        log_warn(f"Release for tag '{tag_name}' already exists. Updating release metadata...")
                        get_req = urllib.request.Request(f"{api_url}/tags/{tag_name}", headers=headers)
                        with urllib.request.urlopen(get_req) as g_resp:
                            release_info = json.loads(g_resp.read().decode("utf-8"))
                            rel_id = release_info.get("id")
                            log_info(f"Found existing release ID: {rel_id}. Updating...")
                            patch_req = urllib.request.Request(f"{api_url}/{rel_id}", data=json.dumps(req_data).encode("utf-8"), headers=headers, method="PATCH")
                            with urllib.request.urlopen(patch_req) as p_resp:
                                release_info = json.loads(p_resp.read().decode("utf-8"))
                                log_success(f"Updated GitHub Release metadata: {release_info.get('html_url')}")
                    else:
                        err_body = he.read().decode("utf-8")
                        raise RuntimeError(f"GitHub API error {he.code}: {err_body}")

                # 2. Upload Binary Asset
                if final_archive and os.path.exists(final_archive) and release_info:
                    asset_filename = os.path.basename(final_archive)
                    upload_url_tpl = release_info.get("upload_url", "")
                    upload_base = upload_url_tpl.split("{")[0]
                    upload_url = f"{upload_base}?name={urllib.parse.quote(asset_filename)}"
                    
                    # Delete existing asset with same name if present
                    for existing_asset in release_info.get("assets", []):
                        if existing_asset.get("name") == asset_filename:
                            del_id = existing_asset.get("id")
                            log_warn(f"Asset '{asset_filename}' already exists on release. Deleting old asset ID {del_id}...")
                            del_req = urllib.request.Request(f"https://api.github.com/repos/{owner}/{repo}/releases/assets/{del_id}", headers=headers, method="DELETE")
                            urllib.request.urlopen(del_req).close()
                            
                    log_info(f"Streaming upload of '{asset_filename}' ({size_mb} MB) to GitHub Releases...", step=5, progress=88)
                    
                    with open(final_archive, "rb") as f:
                        file_bytes = f.read()
                        
                    up_headers = {
                        "Authorization": f"Bearer {github_token}",
                        "Accept": "application/vnd.github+json",
                        "User-Agent": "DeadlockPlayground-ReleaseHub",
                        "Content-Type": "application/octet-stream",
                        "Content-Length": str(len(file_bytes))
                    }
                    
                    up_req = urllib.request.Request(upload_url, data=file_bytes, headers=up_headers, method="POST")
                    with urllib.request.urlopen(up_req) as up_resp:
                        asset_resp = json.loads(up_resp.read().decode("utf-8"))
                        log_success(f"Asset uploaded successfully! Download URL: {asset_resp.get('browser_download_url')}", step=5, progress=92)
            else:
                log_info(f"[Dry Run] Would call GitHub API to create release '{tag_name}' ({rel_label}) and upload '{os.path.basename(final_archive)}'", step=5, progress=92)
        else:
            log_info("[Step 5/6] Skipped GitHub release upload.", step=5, progress=92)

        # ----------------------------------------------------------------------
        # STEP 6: GAMEBANANA ASSISTANT (OPTION 2)
        # ----------------------------------------------------------------------
        if steps.get("gamebanana_assistant", True):
            log_info("[Step 6/6] Launching GameBanana Release Assistant...", step=6, progress=95)
            # 1. Format text for GameBanana
            gb_text = f"Version {version} — {title}\n\n"
            if highlights:
                gb_text += "Highlights:\n" + "\n".join(f"• {h}" for h in highlights) + "\n\n"
            if markdown:
                gb_text += markdown
                
            # 2. Copy to clipboard
            if copy_to_clipboard(gb_text):
                log_success("Changelog copied to Windows Clipboard! Ready to paste into GameBanana.", step=6, progress=97)
            else:
                log_warn("Clipboard copy failed. You can copy the notes from the text preview.")
                
            # 3. Reveal file in Explorer
            if final_archive and os.path.exists(final_archive):
                log_info(f"Highlighting '{os.path.basename(final_archive)}' in Windows Explorer...")
                open_file_in_explorer(final_archive)
                
            # 4. Open GameBanana in default browser
            gb_url = config.get("gamebanana_url", "https://gamebanana.com/tools/edit/24316")
            log_info(f"Opening GameBanana edit page: {gb_url}")
            webbrowser.open(gb_url)
            log_success("GameBanana submission page opened in your browser! Simply drag the highlighted archive and press Ctrl+V to paste your changelog.", step=6, progress=100)
        else:
            log_info("[Step 6/6] Skipped GameBanana Assistant.", step=6, progress=100)

        # ----------------------------------------------------------------------
        # PIPELINE COMPLETE
        # ----------------------------------------------------------------------
        log_success(f"🎉 Pipeline completed successfully for v{version}!", step=6, progress=100)
        broadcast_event("pipeline_complete", success=True, version=version)
        
    except Exception as e:
        log_error(f"Pipeline Failed: {str(e)}", progress=0)
        traceback.print_exc()
        broadcast_event("pipeline_complete", success=False, error=str(e))
    finally:
        pipeline_running = False

# ==============================================================================
# HTTP REQUEST HANDLER
# ==============================================================================

class ReleaseHubHandler(BaseHTTPRequestHandler):
    def log_message(self, format, *args):
        # Suppress normal request noise in console
        pass

    def send_cors_headers(self):
        self.send_header("Access-Control-Allow-Origin", "*")
        self.send_header("Access-Control-Allow-Methods", "GET, POST, OPTIONS")
        self.send_header("Access-Control-Allow-Headers", "Content-Type, Authorization")

    def do_OPTIONS(self):
        self.send_response(200)
        self.send_cors_headers()
        self.end_headers()

    def do_GET(self):
        parsed = urllib.parse.urlparse(self.path)
        path = parsed.path
        
        if path == "/" or path == "/index.html":
            html_path = os.path.join(RELEASE_HUB_DIR, "index.html")
            if os.path.exists(html_path):
                with open(html_path, "rb") as f:
                    content = f.read()
                self.send_response(200)
                self.send_header("Content-Type", "text/html; charset=utf-8")
                self.send_header("Content-Length", str(len(content)))
                self.end_headers()
                self.wfile.write(content)
            else:
                self.send_error(404, "index.html not found")
                
        elif path == "/api/info":
            config = load_config()
            # Read current changelog
            current_ver = "Unknown"
            current_title = ""
            current_notes = ""
            if os.path.exists(CHANGELOG_PATH):
                try:
                    with open(CHANGELOG_PATH, "r", encoding="utf-8") as f:
                        cd = json.load(f)
                        current_ver = cd.get("latestVersion", "Unknown")
                        current_title = cd.get("title", "")
                        current_notes = cd.get("markdown", "")
                except Exception:
                    pass
                    
            # Git branch
            branch = "main"
            try:
                proc = subprocess.run(["git", "branch", "--show-current"], cwd=WORKSPACE_DIR, capture_output=True, text=True)
                if proc.returncode == 0 and proc.stdout.strip():
                    branch = proc.stdout.strip()
            except Exception:
                pass
                
            releases_data = detect_existing_releases(config)
            
            data = {
                "latest_version": current_ver,
                "current_title": current_title,
                "current_branch": branch,
                "has_github_token": bool(config.get("github_token")),
                "github_owner": config.get("repo_owner", "juliancz-a"),
                "github_repo": config.get("repo_name", "deadlock-playground"),
                "gamebanana_url": config.get("gamebanana_url", "https://gamebanana.com/tools/edit/24316"),
                "detected_releases": releases_data.get("items", []),
                "detected_tree": releases_data.get("roots", []),
                "pipeline_running": pipeline_running
            }
            
            resp_bytes = json.dumps(data).encode("utf-8")
            self.send_response(200)
            self.send_cors_headers()
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(resp_bytes)))
            self.end_headers()
            self.wfile.write(resp_bytes)
            
        elif path == "/api/refresh-releases":
            config = load_config()
            releases_data = detect_existing_releases(config)
            resp = json.dumps(releases_data).encode("utf-8")
            self.send_response(200)
            self.send_cors_headers()
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(resp)))
            self.end_headers()
            self.wfile.write(resp)

        elif path == "/api/poll-logs":
            with log_lock:
                logs_copy = list(recent_events)
            resp = json.dumps({"status": "ok", "logs": logs_copy, "running": pipeline_running}).encode("utf-8")
            self.send_response(200)
            self.send_cors_headers()
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(resp)))
            self.end_headers()
            self.wfile.write(resp)
            
        elif path == "/api/logs":
            # Server-Sent Events (SSE) stream with auto-reconnect & history replay
            self.send_response(200)
            self.send_cors_headers()
            self.send_header("Content-Type", "text/event-stream; charset=utf-8")
            self.send_header("Cache-Control", "no-cache, no-transform")
            self.send_header("Connection", "keep-alive")
            self.send_header("X-Accel-Buffering", "no")
            self.end_headers()
            
            client_queue = queue.Queue(maxsize=1000)
            with log_lock:
                log_clients.append(client_queue)
                # Replay recent events so browser immediately shows console logs & current progress
                for evt in recent_events:
                    client_queue.put_nowait(f"data: {json.dumps(evt)}\n\n")
                    
            try:
                while True:
                    try:
                        msg = client_queue.get(timeout=10)
                        self.wfile.write(msg.encode("utf-8"))
                        self.wfile.flush()
                    except queue.Empty:
                        # Keep-alive heartbeat (comment in SSE)
                        self.wfile.write(b": ping\n\n")
                        self.wfile.flush()
            except (ConnectionResetError, BrokenPipeError):
                pass
            except Exception:
                pass
            finally:
                with log_lock:
                    if client_queue in log_clients:
                        log_clients.remove(client_queue)
        else:
            self.send_error(404, "Endpoint not found")

    def do_POST(self):
        try:
            parsed = urllib.parse.urlparse(self.path)
            path = parsed.path
            length = int(self.headers.get("Content-Length", 0))
            body = self.rfile.read(length) if length > 0 else b"{}"
            
            try:
                payload = json.loads(body.decode("utf-8"))
            except Exception:
                payload = {}
                
            if path == "/api/save-config":
                cfg = load_config()
                if "github_token" in payload and payload["github_token"]:
                    cfg["github_token"] = payload["github_token"].strip()
                if "repo_owner" in payload:
                    cfg["repo_owner"] = payload["repo_owner"].strip()
                if "repo_name" in payload:
                    cfg["repo_name"] = payload["repo_name"].strip()
                if "gamebanana_url" in payload:
                    cfg["gamebanana_url"] = payload["gamebanana_url"].strip()
                save_config(cfg)
                
                resp = json.dumps({"status": "ok", "message": "Settings saved successfully"}).encode("utf-8")
                self.send_response(200)
                self.send_cors_headers()
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(resp)))
                self.end_headers()
                self.wfile.write(resp)
                
            elif path == "/api/run-pipeline":
                global pipeline_running
                if pipeline_running:
                    resp = json.dumps({"status": "error", "message": "Pipeline is already running"}).encode("utf-8")
                    self.send_response(409)
                else:
                    thread = threading.Thread(target=execute_pipeline, args=(payload,), daemon=True)
                    thread.start()
                    resp = json.dumps({"status": "started", "message": "Release pipeline launched"}).encode("utf-8")
                    self.send_response(200)
                    
                self.send_cors_headers()
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(resp)))
                self.end_headers()
                self.wfile.write(resp)
                
            elif path == "/api/open-explorer":
                p = payload.get("path", "")
                success = open_file_in_explorer(p)
                resp = json.dumps({"status": "ok" if success else "error"}).encode("utf-8")
                self.send_response(200)
                self.send_cors_headers()
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(resp)))
                self.end_headers()
                self.wfile.write(resp)
            else:
                self.send_error(404, "Endpoint not found")
        except Exception as e:
            traceback.print_exc()
            err_resp = json.dumps({"status": "error", "message": str(e)}).encode("utf-8")
            try:
                self.send_response(500)
                self.send_cors_headers()
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(err_resp)))
                self.end_headers()
                self.wfile.write(err_resp)
            except Exception:
                pass

def start_server():
    server = ThreadedHTTPServer(("127.0.0.1", PORT), ReleaseHubHandler)
    url = f"http://127.0.0.1:{PORT}"
    print(f"\n=======================================================")
    print(f" Deadlock Playground - Release Hub Online")
    print(f" Local Web Dashboard: {url}")
    print(f" Press Ctrl+C in this terminal to stop.")
    print(f"=======================================================\n")
    # Open browser automatically
    threading.Timer(0.8, lambda: webbrowser.open(url)).start()
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        print("\nStopping Release Hub Server...")
        server.server_close()

if __name__ == "__main__":
    start_server()
