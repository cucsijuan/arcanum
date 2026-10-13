#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
"""Mirrors docs/ to the GitHub wiki: the player guide (docs/guide), the developer docs and the release notes.

docs/ stays the source; every wiki page says which file it comes from. Links between docs become links between
wiki pages, other relative links point at the file on GitHub. The wiki's own pages that don't come from docs/ are
removed, so edit docs/ instead of the wiki.

usage: tools/wiki_sync.py [--push] [--out DIR]
  without --push it writes the pages to DIR (default: a temporary clone of the wiki) and shows what changed.
"""
import argparse
import os
import re
import subprocess
import sys
import tempfile

REPO = "cucsijuan/arcanum"
WIKI = f"https://github.com/{REPO}.wiki.git"
BLOB = f"https://github.com/{REPO}/blob/main/"
ROOT = subprocess.run(["git", "rev-parse", "--show-toplevel"], capture_output=True, text=True, check=True).stdout.strip()

# The guide's pages in reading order, then the developer docs (docs/review-backlog.md stays out: an internal list).
GUIDE = ["getting-started", "playing", "decks", "limited", "online", "career", "settings"]
DEVELOPER = ["building", "engine-map", "card-scripts", "career-mode", "online-services", "roadmap"]


def page_name(stem):
    return "-".join(part.capitalize() for part in stem.split("-"))


def version_key(name):
    return tuple(int(n) for n in re.findall(r"\d+", name))


def pages():
    """Source file (relative to the repository) → wiki page name."""
    result = {}
    for stem in GUIDE:
        if os.path.exists(os.path.join(ROOT, "docs/guide", stem + ".md")):
            result[f"docs/guide/{stem}.md"] = page_name(stem)
    for stem in DEVELOPER:
        if os.path.exists(os.path.join(ROOT, "docs", stem + ".md")):
            result[f"docs/{stem}.md"] = page_name(stem)
    for name in sorted(os.listdir(os.path.join(ROOT, "docs/releases")), key=version_key, reverse=True):
        if name.endswith(".md"):
            result[f"docs/releases/{name}"] = "Release-" + name[:-3]
    return result


LINK = re.compile(r"(\]\()([^)\s]+)(\))")


def relink(text, source, mapping):
    folder = os.path.dirname(source)

    def fix(match):
        target = match.group(2)
        if re.match(r"^[a-z]+:", target) or target.startswith("#"):
            return match.group(0)
        path, _, anchor = target.partition("#")
        resolved = os.path.normpath(os.path.join(folder, path))
        if resolved in mapping:
            url = mapping[resolved] + ("#" + anchor if anchor else "")
        else:
            url = BLOB + resolved + ("#" + anchor if anchor else "")
        return match.group(1) + url + match.group(3)

    return LINK.sub(fix, text)


def read(path):
    with open(os.path.join(ROOT, path), encoding="utf-8") as f:
        return f.read()


def title(path, fallback):
    first = read(path).lstrip().splitlines()[0] if read(path).strip() else ""
    return first.lstrip("#").strip() if first.startswith("#") else fallback.replace("-", " ")


def write_pages(out):
    mapping = pages()
    written = set()

    def put(name, text):
        with open(os.path.join(out, name + ".md"), "w", encoding="utf-8") as f:
            f.write(text.rstrip() + "\n")
        written.add(name + ".md")

    for source, name in mapping.items():
        footer = f"\n\n---\n*This page is made from [`{source}`]({BLOB}{source}); edit it there.*\n"
        put(name, relink(read(source), source, mapping) + footer)

    guide = [(n, title(s, n)) for s, n in mapping.items() if s.startswith("docs/guide/")]
    developer = [(n, title(s, n)) for s, n in mapping.items() if s.startswith("docs/") and s.count("/") == 1]
    releases = [n for s, n in mapping.items() if s.startswith("docs/releases/")]
    intro = read("README.md").split("\n## ")[0].split("\n", 1)[1].strip()

    home = [f"# Arcanum\n\n{intro}\n", "## Player guide"]
    home += [f"- [{t}]({n})" for n, t in guide]
    home += ["", "## Developer docs"] + [f"- [{t}]({n})" for n, t in developer]
    home += ["", "## Releases", f"- [All release notes](Releases) (latest: [{releases[0][8:]}]({releases[0]}))" if releases else "- none yet"]
    home += ["", f"Source code, downloads and issues: https://github.com/{REPO}"]
    put("Home", "\n".join(home))
    put("Releases", "# Release notes\n\n" + "\n".join(f"- [{n[8:]}]({n})" for n in releases))
    sidebar = ["**[Home](Home)**", "", "**Player guide**"] + [f"- [{t}]({n})" for n, t in guide]
    sidebar += ["", "**Developer docs**"] + [f"- [{t}]({n})" for n, t in developer] + ["", "**[Release notes](Releases)**"]
    put("_Sidebar", "\n".join(sidebar))

    # Pages that no longer come from docs/ go away.
    for name in os.listdir(out):
        if name.endswith(".md") and name not in written:
            os.remove(os.path.join(out, name))


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--push", action="store_true", help="commit and push the wiki")
    parser.add_argument("--out", help="the wiki clone to write into (cloned when missing)")
    args = parser.parse_args()
    out = args.out or os.path.join(tempfile.mkdtemp(prefix="arcanum-wiki-"), "wiki")
    if not os.path.isdir(os.path.join(out, ".git")):
        subprocess.run(["git", "clone", "-q", WIKI, out], check=True)
    else:
        subprocess.run(["git", "-C", out, "pull", "-q"], check=True)
    write_pages(out)
    subprocess.run(["git", "-C", out, "add", "-A"], check=True)
    if subprocess.run(["git", "-C", out, "diff", "--cached", "--quiet"]).returncode == 0:
        print("The wiki is up to date.")
        return
    subprocess.run(["git", "-C", out, "diff", "--cached", "--stat"], check=True)
    if not args.push:
        print(f"Not pushed (pages in {out}; rerun with --push).")
        return
    head = subprocess.run(["git", "-C", ROOT, "rev-parse", "--short", "HEAD"], capture_output=True, text=True, check=True).stdout.strip()
    subprocess.run(["git", "-C", out, "commit", "-q", "-m", f"Sync from docs/ at {head}"], check=True)
    subprocess.run(["git", "-C", out, "push", "-q"], check=True)
    print(f"Wiki updated: https://github.com/{REPO}/wiki")


if __name__ == "__main__":
    sys.exit(main())
