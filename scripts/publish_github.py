#!/usr/bin/env python3
"""
Publish System Assistant to GitHub via the REST API (no `git push` required).

Why API instead of git push:
  Some corporate proxies reject the CONNECT tunnel used by libcurl inside Git
  (HTTP 502) while ordinary HTTPS to api.github.com works fine. This script
  creates the repository, commits the full source tree as blobs/tree/commit,
  and optionally creates a versioned Release with the portable single-file
  archives and the installer attached. The version defaults to <Version> in
  Directory.Build.props, so bumping the app version needs no script change.

Usage:
  set GITHUB_TOKEN=ghp_xxx            (Windows)
  export GITHUB_TOKEN=ghp_xxx         (bash)
  python scripts/publish_github.py --repo System-Assistant --visibility public
  python scripts/publish_github.py --version 1.2.0   # explicit version / new tag

  # or pass the token directly (not recommended: it appears in shell history)
  python scripts/publish_github.py --token ghp_xxx

  # skip release assets
  python scripts/publish_github.py --no-release

Only the Python standard library is used. The token is never written to disk.
A fine-grained token with "Contents: read/write" + "Administration: read/write"
(or a classic token with `repo` scope) is sufficient. Revoke it after publishing
if you do not want it to remain valid.
"""

from __future__ import annotations

import argparse
import base64
import json
import os
import re
import subprocess
import sys
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

API = "https://api.github.com"
UPLOADS = "https://uploads.github.com"
USER_AGENT = "SystemAssistant-Publisher/1.0"


class GitHub:
    def __init__(self, token: str):
        self.token = token

    def request(self, method: str, url: str, payload=None, raw: bytes | None = None,
                content_type: str = "application/json", expect: int | None = None):
        headers = {
            "Authorization": f"Bearer {self.token}",
            "Accept": "application/vnd.github+json",
            "X-GitHub-Api-Version": "2022-11-28",
            "User-Agent": USER_AGENT,
        }
        if raw is not None:
            body = raw
            headers["Content-Type"] = content_type
        elif payload is not None:
            body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
            headers["Content-Type"] = content_type
        else:
            body = None

        request = urllib.request.Request(url, data=body, headers=headers, method=method)
        try:
            # Release assets can be 40+ MB, so give binary uploads a longer window.
            timeout = 600 if raw is not None else 60
            with urllib.request.urlopen(request, timeout=timeout) as response:
                data = response.read()
                status = response.status
        except urllib.error.HTTPError as error:
            detail = error.read().decode("utf-8", errors="replace")
            # 422 = repository already exists; handled by the caller
            if error.code == 422 and "repos" in url and method == "POST":
                return 422, {"message": "already exists", "detail": detail}
            raise RuntimeError(f"{method} {url} -> HTTP {error.code}: {detail[:600]}") from error
        except urllib.error.URLError as error:
            raise RuntimeError(f"{method} {url} -> network error: {error.reason}") from error

        parsed = json.loads(data.decode("utf-8")) if data else {}
        if expect is not None and status != expect:
            raise RuntimeError(f"{method} {url} -> expected HTTP {expect}, got {status}")
        return status, parsed

    def me(self) -> str:
        _, user = self.request("GET", f"{API}/user")
        return user["login"]


def read_project_version(root: Path) -> str:
    """Single source of truth: <Version> in Directory.Build.props."""
    props = root / "Directory.Build.props"
    if props.is_file():
        match = re.search(r"<Version>\s*([^<]+?)\s*</Version>",
                          props.read_text(encoding="utf-8"))
        if match:
            return match.group(1).strip()
    return "1.1.2"


def read_tracked_files(root: Path):
    """
    Read the *index* content (LF-normalized), not the working-tree bytes.
    This keeps the tree SHA identical to the local commit, so a later
    `git push` from this clone does not create a divergent history.
    `git ls-files` also guarantees .gitignore is respected.
    """
    result = subprocess.run(
        ["git", "ls-files", "-z"], cwd=root, capture_output=True, check=True
    )
    names = [n for n in result.stdout.decode("utf-8").split("\0") if n]
    for name in names:
        blob = subprocess.run(
            ["git", "show", f":{name}"], cwd=root, capture_output=True, check=True
        ).stdout
        yield name, blob


def create_or_get_repo(gh: GitHub, owner: str, repo: str, private: bool, description: str):
    status, _ = gh.request("POST", f"{API}/user/repos", {
        "name": repo,
        "description": description,
        "private": private,
        "has_issues": True,
        "has_projects": False,
        "has_wiki": False,
        "auto_init": False,   # we push the tree ourselves; no README from GitHub
    })
    created = status == 201
    if created:
        print(f"  Repository created: https://github.com/{owner}/{repo}")
    else:
        print(f"  Repository already exists, reusing: https://github.com/{owner}/{repo}")

    _, info = gh.request("GET", f"{API}/repos/{owner}/{repo}")
    return info["default_branch"] or "main"


def current_head(gh: GitHub, owner: str, repo: str, branch: str):
    try:
        _, ref = gh.request("GET", f"{API}/repos/{owner}/{repo}/git/ref/heads/{branch}")
        return ref["object"]["sha"]
    except RuntimeError:
        return None


def push_tree(gh: GitHub, owner: str, repo: str, root: Path, branch: str, message: str):
    files = list(read_tracked_files(root))
    print(f"  Uploading {len(files)} source files as blobs ...")
    tree_entries = []
    for index, (name, content) in enumerate(files, start=1):
        _, blob = gh.request("POST", f"{API}/repos/{owner}/{repo}/git/blobs", {
            "content": base64.b64encode(content).decode("ascii"),
            "encoding": "base64",
        })
        tree_entries.append({
            "path": name,
            "mode": "100755" if name.endswith((".sh",)) else "100644",
            "type": "blob",
            "sha": blob["sha"],
        })
        if index % 20 == 0 or index == len(files):
            print(f"    {index}/{len(files)}")

    _, tree = gh.request("POST", f"{API}/repos/{owner}/{repo}/git/trees", {"tree": tree_entries})
    parent = current_head(gh, owner, repo, branch)
    commit_payload = {"message": message, "tree": tree["sha"]}
    if parent:
        commit_payload["parents"] = [parent]
    _, commit = gh.request("POST", f"{API}/repos/{owner}/{repo}/git/commits", commit_payload)

    if parent:
        gh.request("PATCH", f"{API}/repos/{owner}/{repo}/git/refs/heads/{branch}",
                   {"sha": commit["sha"]})
    else:
        gh.request("POST", f"{API}/repos/{owner}/{repo}/git/refs",
                   {"ref": f"refs/heads/{branch}", "sha": commit["sha"]})
    print(f"  Commit {commit['sha'][:8]} pushed to {branch}")
    return commit["sha"]


def create_release(gh: GitHub, owner: str, repo: str, artifacts: Path,
                   branch: str, root: Path, version: str):
    """Create or update the release for `version`. Idempotent: safe after a rebuild."""
    tag = f"v{version}"
    setup_name = f"SystemAssistant-Setup-{version}.exe"
    title = f"System Assistant {tag}"

    changelog = root / "CHANGELOG.md"
    notes = changelog.read_text(encoding="utf-8") if changelog.exists() else title
    body = (
        f"## {title}\n\n"
        "Windows 硬件监控、安全清理与系统优化工具（C# .NET 9 + Avalonia UI，"
        "Models / Utils / Core / App 四层架构）。所有高危操作二次确认并写日志，"
        "不联网上传任何用户数据。\n\n"
        "**本版本变更**：见下方更新日志；完整清单见仓库 `CHANGELOG.md`。\n\n"
        "**下载 / Downloads**\n\n"
        f"- `{setup_name}` — Windows 安装向导（推荐；免 UAC，含卸载程序）\n"
        "- `win-x64-self-contained.zip` — 绿色单文件便携版，无需安装 .NET 运行时\n"
        "- `win-x64-framework-dependent.zip` — 绿色单文件便携版，需 .NET 9 Desktop Runtime\n\n"
        "> 完整清单见仓库 `FUNCTIONAL-TEST.md`（全功能实测）、`REVIEW.md`（静态复检）"
        "与 `CHANGELOG.md`（逐版变更）。\n\n---\n\n" + notes
    )

    base = f"{API}/repos/{owner}/{repo}/releases"
    existing = None
    try:
        _, existing = gh.request("GET", f"{base}/tags/{tag}")
    except RuntimeError:
        pass  # 404: this version has no release yet; older tags are left untouched

    if existing:
        release_id = existing["id"]
        gh.request("PATCH", f"{base}/{release_id}", {"body": body, "name": title})
        # Re-uploading an asset with the same name returns 422, so clear old ones first.
        for asset in existing.get("assets", []):
            gh.request("DELETE", f"{base}/assets/{asset['id']}")
            print(f"  Removed stale asset: {asset['name']}")
        html_url = existing["html_url"]
        print(f"  Release updated: {html_url}")
    else:
        _, created = gh.request("POST", base, {
            "tag_name": tag,
            "target_commitish": branch,
            "name": title,
            "body": body,
            "draft": False,
            "prerelease": False,
        })
        release_id = created["id"]
        html_url = created["html_url"]
        print(f"  Release created: {html_url}")

    # Only this version's installer is attached (older setup exe files stay local).
    candidates = sorted(artifacts.glob("*.zip"))
    setup = artifacts / setup_name
    if setup.is_file():
        candidates.append(setup)
    else:
        print(f"  Warning: {setup_name} not found, skipping installer asset")
    if not candidates:
        print("  Warning: no artifacts found, release has no binaries")
        return html_url

    for archive in candidates:
        name = archive.name
        url = (f"{UPLOADS}/repos/{owner}/{repo}/releases/{release_id}/assets"
               f"?name={urllib.parse.quote(name)}")
        content_type = "application/zip" if archive.suffix.lower() == ".zip" else "application/octet-stream"
        gh.request("POST", url, raw=archive.read_bytes(), content_type=content_type)
        print(f"  Uploaded {name} ({archive.stat().st_size / 1024 / 1024:.1f} MB)")
    return html_url


def main() -> int:
    parser = argparse.ArgumentParser(description="Publish System Assistant to GitHub")
    parser.add_argument("--token", default=os.environ.get("GITHUB_TOKEN") or os.environ.get("GH_TOKEN"))
    parser.add_argument("--token-file", default="",
                        help="Read the token from a local file instead of the environment "
                             "(defaults to <source>/.github_token when present)")
    parser.add_argument("--owner", default="", help="GitHub owner (default: authenticated user)")
    parser.add_argument("--repo", default="System-Assistant")
    parser.add_argument("--visibility", choices=["public", "private"], default="public")
    parser.add_argument("--source", default=str(Path(__file__).resolve().parent.parent))
    parser.add_argument("--artifacts", default="", help="Directory containing release zip files")
    parser.add_argument("--no-release", action="store_true")
    parser.add_argument("--version", default="",
                        help="Release version / tag (default: <Version> in Directory.Build.props)")
    parser.add_argument("--message", default="",
                        help="Commit message (default: chore: release vX.Y.Z)")
    args = parser.parse_args()

    root = Path(args.source).resolve()
    version = (args.version or "").strip() or read_project_version(root)
    message = args.message or f"chore: release v{version}"
    token_file = Path(args.token_file).resolve() if args.token_file else root / ".github_token"
    token = (args.token or "").strip()
    if not token and token_file.is_file():
        # Local-only credential file: never tracked by git, never printed to output.
        token = token_file.read_text(encoding="utf-8-sig").strip()
    if not token:
        print("ERROR: GitHub token missing. Set GITHUB_TOKEN, pass --token, or fill .github_token.",
              file=sys.stderr)
        print("       Create a token at https://github.com/settings/tokens (classic, scope: repo)",
              file=sys.stderr)
        return 2

    artifacts = Path(args.artifacts).resolve() if args.artifacts else root / "artifacts"
    if not (root / ".git").exists():
        print(f"ERROR: {root} is not a git repository (script reads 'git ls-files').", file=sys.stderr)
        return 2
    if not (root / "README.md").exists():
        print(f"ERROR: README.md not found under {root}.", file=sys.stderr)
        return 2

    gh = GitHub(token)
    try:
        owner = args.owner or gh.me()
    except RuntimeError as error:
        print(f"ERROR: authentication failed: {error}", file=sys.stderr)
        return 1

    print(f"Publishing v{version} to https://github.com/{owner}/{args.repo} ({args.visibility})")
    try:
        branch = create_or_get_repo(gh, owner, args.repo, args.visibility == "private",
                                    "Windows hardware monitor, safe cleanup and system toolkit "
                                    "built with C# .NET 9 + Avalonia UI")
        push_tree(gh, owner, args.repo, root, branch, message)
        if not args.no_release:
            create_release(gh, owner, args.repo, artifacts, branch, root, version)
    except RuntimeError as error:
        print(f"ERROR: {error}", file=sys.stderr)
        return 1

    # Wipe the local credential once publishing fully succeeds, so no token is
    # left lying around. Failures keep the token for a safe retry.
    if not args.token and token_file.is_file():
        token_file.write_text("", encoding="utf-8")

    print("\nDone: " + f"https://github.com/{owner}/{args.repo}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
