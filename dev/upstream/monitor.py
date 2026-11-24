"""Check public upstream metadata without checking out or executing upstream code."""

import base64
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timezone
import hashlib
import io
import json
import os
from pathlib import Path
import re
import sys
from time import sleep
from urllib.error import HTTPError
from urllib.parse import quote, urlparse
from urllib.request import HTTPRedirectHandler, Request, build_opener, urlopen
from zipfile import BadZipFile, ZipFile


STATE_BRANCH = "upstream/state"
STATE_FILE = "state.json"
REPORT = "upstream-checks.json"
PALETTE = {"upstream": "0550ae", "core": "5319e7", "offsets": "d93f0b", "signatures": "b60205",
           "plugins": "1d76db", "recovery": "006b75", "release": "0e8a16", "license": "fbca04",
           "needs-analysis": "d4c5f9"}


class NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def serialize(obj):
    return json.dumps(obj, ensure_ascii=False, sort_keys=True)


class GitHub:
    def __init__(self, token):
        self.token = token

    def call(self, path, *, method="GET", body=None, missing=False, public=False):
        url = "https://api.github.com" + path
        payload = None if body is None else serialize(body).encode()
        headers = {"Accept": "application/vnd.github+json", "X-GitHub-Api-Version": "2022-11-28",
                   "User-Agent": "GameHelper2-DevEd-Upstream", "Content-Type": "application/json"}
        if self.token:
            headers["Authorization"] = "Bearer " + self.token
        for attempt in range(2 if public and self.token else 1):
            try:
                with urlopen(Request(url, data=payload, method=method, headers=headers), timeout=5) as response:
                    data = response.read()
                    return json.loads(data) if data else None
            except HTTPError as error:
                if public and attempt == 0 and self.token and error.code in (403, 404):
                    headers.pop("Authorization", None)
                    continue
                if missing and error.code == 404:
                    return None
                raise RuntimeError(f"GitHub {method} {path}: HTTP {error.code}") from None

    def pages(self, path, **options):
        separator = "&" if "?" in path else "?"
        for page in range(1, 11):
            rows = self.call(f"{path}{separator}per_page=100&page={page}", **options)
            yield from rows
            if len(rows) < 100:
                return
        raise RuntimeError(f"Pagination limit exceeded for {path}")


def now():
    return datetime.now(timezone.utc).isoformat(timespec="seconds")


def inspect_source(api, source):
    slug = source["repo"]
    base = f"/repos/{slug}"
    info = api.call(base, public=True)
    branch = source["branch"] or info["default_branch"]
    tip = api.call(f"{base}/git/ref/heads/{quote(branch, safe='')}", public=True)["object"]["sha"]
    tags = list(api.pages(base + "/tags", public=True))
    releases = [row for row in api.pages(base + "/releases", public=True) if not row["draft"]]
    observed = {
        "repo": slug, "url": "https://github.com/" + slug, "branch": branch, "defaultBranch": info["default_branch"],
        "commit": tip, "latestTag": tags[0]["name"] if tags else None,
        "latestRelease": releases[0]["tag_name"] if releases else None,
        "tags": {row["name"]: row["commit"]["sha"] for row in tags},
        "releases": {str(row["id"]): row["tag_name"] for row in releases},
        "license": (info.get("license") or {}).get("spdx_id"), "integrated": source["integrated"],
    }
    return observed, releases


def labels_for(files, metadata):
    labels = {"upstream"}
    for name in files:
        path = name.lower()
        leaf = path.rsplit("/", 1)[-1]
        if path.startswith(("gamehelper/", "launcher/")) and "offset" not in path:
            labels.add("core")
        if path.startswith("gameoffsets/") or "offset" in path:
            labels.add("offsets")
        if "signature" in path or "pattern" in leaf:
            labels.add("signatures")
        if path.startswith("plugins/"):
            labels.add("plugins")
        if "recovery" in path or path.startswith("dev/ghidra/"):
            labels.add("recovery")
        if "license" in leaf or "licence" in leaf or leaf.startswith(("copying", "notice")):
            labels.add("license")
    labels.update(metadata)
    if labels == {"upstream"}:
        labels.add("needs-analysis")
    return sorted(labels)


def safe_lines(lines, limit=14000):
    raw = "\n".join(lines).replace("@", "＠").replace("<", "‹").replace(">", "›")
    clipped = raw[:limit]
    if len(raw) > limit:
        clipped += "\n[truncated]"
    return "\n".join("    " + line for line in clipped.splitlines()) or "    (данные отсутствуют)"


def update_event(api, prior, observed, releases):
    if prior is None:
        return None
    # An edit to our integrated SHA is bookkeeping and never an upstream event.
    keys = ("branch", "defaultBranch", "commit", "tags", "releases", "license")
    changed = [key for key in keys if observed[key] != prior.get(key)]
    if not changed:
        return None
    slug = observed["repo"]
    before, after = prior["commit"], observed["commit"]
    comparison_url = f"https://github.com/{slug}/compare/{before}...{after}"
    files, commits, metadata = [], [], set()
    if before != after:
        comparison = api.call(f"/repos/{slug}/compare/{before}...{after}?per_page=100", missing=True, public=True)
        if comparison:
            files = [row["filename"] for row in comparison.get("files", [])]
            commits = [f"{row['sha'][:10]} {row['commit']['message'].splitlines()[0]}" for row in comparison.get("commits", [])
                       if row["commit"]["message"]]
            if len(files) >= 300 or comparison.get("total_commits", 0) > len(commits) or comparison.get("status") != "ahead":
                metadata.add("needs-analysis")
        else:
            metadata.add("needs-analysis")
    for field, suffix in (("tags", "теги"), ("releases", "релизы")):
        old = prior.get(field, {})
        new = observed[field]
        commits += [f"{suffix}: {key} → {value}" for key, value in new.items() if old.get(key) != value]
        commits += [f"{suffix}: удалено {key} ({value})" for key, value in old.items() if key not in new]
    if {"tags", "releases"}.intersection(changed):
        metadata.add("release")
    if "license" in changed:
        metadata.add("license")
    release_notes = [f"{row['tag_name']}\n{(row.get('body') or '')[:3200]}" for row in releases
                     if str(row["id"]) not in prior.get("releases", {})][:4]
    labels = labels_for(files, metadata)
    digest = hashlib.sha256(serialize([slug, {key: [prior.get(key), observed[key]] for key in sorted(changed)}]).encode()).hexdigest()
    affected = ", ".join(label for label in labels if label not in ("upstream", "needs-analysis")) or "требуется анализ"
    body = f"""<!-- upstream-event:{digest} -->
Источник [{slug}](https://github.com/{slug}).

- Ветка `{safe_inline(prior['branch'])}` → `{safe_inline(observed['branch'])}`. Основная ветка источника `{safe_inline(observed['defaultBranch'])}`.
- Коммит `{before}` → `{after}`.
- Последний релиз `{safe_inline(observed['latestRelease'])}`. Последний тег `{safe_inline(observed['latestTag'])}`.
- Лицензия GitHub `{safe_inline(prior.get('license'))}` → `{safe_inline(observed['license'])}`.
- Последняя интегрированная ревизия `{safe_inline(observed['integrated'])}`.
- Возможные затронутые компоненты DevEd: {affected}.
- [Сравнение](<{comparison_url}>).

### Коммиты и ссылки

{safe_lines(commits)}

### Файлы из ответа Compare API

{safe_lines(files, 11500)}

### Примечания к релизам

{safe_lines(release_notes, 9500)}
"""
    return f"[Upstream] {slug} — обновление", body, labels


def safe_inline(value):
    return re.sub(r"[^\w. /+:-]", "_", str(value or "нет"))[:140]


class Issues:
    def __init__(self, api, base):
        self.api, self.base = api, base
        self.seen = None

    def find_existing(self):
        seen = {}
        # The label-filtered list may lag behind issue creation. Read the
        # repository issue list directly and match the marker on the first line.
        for issue in self.api.pages(self.base + "/issues?state=all&sort=created&direction=desc"):
            if "pull_request" in issue:
                continue
            marker, _, _ = (issue.get("body") or "").partition("\n")
            if re.fullmatch(r"<!-- upstream-event:[a-z0-9-]+ -->", marker):
                seen[marker] = issue
        return seen

    def ensure(self, title, body, labels):
        if self.seen is None:
            existing_labels = {row["name"] for row in self.api.pages(self.base + "/labels")}
            for name, color in PALETTE.items():
                if name not in existing_labels:
                    self.api.call(self.base + "/labels", method="POST", body={"name": name, "color": color})
            self.seen = self.find_existing()
        marker = body.splitlines()[0]
        created = marker not in self.seen
        if created:
            self.seen[marker] = self.api.call(self.base + "/issues", method="POST",
                                             body={"title": title, "body": body, "labels": labels})
        return created, self.seen[marker]


def read_state(api, base):
    stored = api.call(f"{base}/contents/{STATE_FILE}?ref={STATE_BRANCH}", missing=True)
    if stored is None:
        return {}, None
    state = json.loads(base64.b64decode(stored["content"]))
    if state.get("schema") != 1 or not isinstance(state.get("sources"), dict):
        raise RuntimeError("Unsupported upstream state")
    return state["sources"], stored["sha"]


def save_state(api, base, state, previous_sha):
    if previous_sha is None and api.call(f"{base}/git/ref/heads/{STATE_BRANCH}", missing=True) is None:
        ref = api.call(f"{base}/git/ref/heads/main")["object"]["sha"]
        api.call(base + "/git/refs", method="POST", body={"ref": "refs/heads/" + STATE_BRANCH, "sha": ref})
    body = {"message": "Record detected upstream state", "branch": STATE_BRANCH,
            "content": base64.b64encode((serialize({"schema": 1, "sources": state}) + "\n").encode()).decode()}
    if previous_sha:
        body["sha"] = previous_sha
    api.call(f"{base}/contents/{STATE_FILE}", method="PUT", body=body)


def prior_checks(api, base):
    if not os.getenv("GITHUB_ACTIONS"):
        return {}
    try:
        result = api.call(f"{base}/actions/artifacts?name=upstream-checks&per_page=3")
        for artifact in result["artifacts"]:
            if artifact["expired"]:
                continue
            # Resolve the signed redirect separately so that the repository token is never forwarded.
            request = Request(f"https://api.github.com{base}/actions/artifacts/{artifact['id']}/zip",
                              headers={"Authorization": "Bearer " + api.token, "User-Agent": "GameHelper2-DevEd-Upstream"})
            try:
                with build_opener(NoRedirect()).open(request, timeout=3):
                    pass
            except HTTPError as response:
                location = response.headers.get("Location")
                if response.code != 302 or not location or urlparse(location).scheme != "https":
                    continue
                with urlopen(location, timeout=3) as signed, ZipFile(io.BytesIO(signed.read(65537))) as zipped:
                    if zipped.getinfo(REPORT).file_size <= 65536:
                        return json.loads(zipped.read(REPORT))["lastSuccessfulChecks"]
    except (OSError, ValueError, KeyError, RuntimeError, BadZipFile) as error:
        print(f"Timestamp artifact unavailable: {type(error).__name__}")
    return {}


def monitor(api, base, config):
    previous, sha = read_state(api, base)
    current = dict(previous)
    ticks = {slug: row["lastSuccessfulCheck"] for slug, row in previous.items()}
    ticks.update(prior_checks(api, base))
    issues = Issues(api, base)
    errors = []
    with ThreadPoolExecutor(max_workers=5) as workers:
        futures = [(source["repo"], workers.submit(inspect_source, api, source)) for source in config]
        for slug, future in futures:
            try:
                observed, releases = future.result()
                prior = previous.get(slug)
                event = update_event(api, prior, observed, releases)
                if event:
                    created, issue = issues.ensure(*event)
                    print(f"{slug}: {'opened' if created else 'already recorded'} #{issue['number']}")
                if prior is None or event or observed["integrated"] != prior.get("integrated"):
                    current[slug] = {**observed, "lastSuccessfulCheck": now()}
                ticks[slug] = now()
            except (OSError, ValueError, KeyError, RuntimeError) as error:
                errors.append(slug)
                print(f"{slug}: retained previous state ({error})")
    if current != previous:
        save_state(api, base, current, sha)
    Path(REPORT).write_text(serialize({"lastSuccessfulChecks": ticks, "failed": errors}), encoding="utf-8")
    print(f"Checked {len(config) - len(errors)}/{len(config)} sources; durable state {'updated' if current != previous else 'unchanged'}.")
    return int(bool(errors))


def test_api(api, base):
    issues = Issues(api, base)
    title = "[Upstream test] Controlled fixture"
    marker = "<!-- upstream-event:controlled-fixture-v2 -->"
    body = marker + "\nSynthetic verification of issue creation and deduplication. No upstream update."
    created, issue = issues.ensure(title, body, ["upstream"])
    restarted = Issues(api, base)
    for delay in (0, 1, 2, 4):
        sleep(delay)
        restarted.seen = restarted.find_existing()
        if marker in restarted.seen:
            break
    if marker not in restarted.seen:
        raise RuntimeError("Created fixture is not visible in repository issue listing")
    again, same = restarted.ensure(title, body, ["upstream"])
    if again or same["number"] != issue["number"]:
        raise RuntimeError("Issue deduplication failed")
    if same["state"] != "closed":
        api.call(f"{base}/issues/{same['number']}", method="PATCH", body={"state": "closed"})
    print(f"Controlled fixture #{issue['number']}: {'created' if created else 'reused'}, deduplicated and closed.")
    return 0


if __name__ == "__main__":
    api = GitHub(os.environ["GH_TOKEN"])
    base = "/repos/" + os.environ["GITHUB_REPOSITORY"]
    config = json.loads(Path(__file__).with_name("sources.json").read_text(encoding="utf-8"))["sources"]
    raise SystemExit(test_api(api, base) if sys.argv[1:] == ["test-api"] else monitor(api, base, config))
