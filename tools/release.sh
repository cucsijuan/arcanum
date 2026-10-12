#!/usr/bin/env bash
# SPDX-License-Identifier: AGPL-3.0-or-later
# Cuts a game release: asks for the version, sets it in project.godot, makes sure the release notes exist
# (docs/releases/vX.Y.Z.md, opened in $EDITOR from the last notes when missing), commits, pushes, and pushes the tag
# vX.Y.Z; GitHub Actions then exports Linux/Windows and publishes the release (.github/workflows/release.yml).
#
# usage: tools/release.sh            (run from anywhere inside the repository)
set -euo pipefail

cd "$(git rev-parse --show-toplevel)"
ask() { local answer; read -r -p "$1" answer; printf '%s' "$answer"; }
die() { echo "error: $*" >&2; exit 1; }
# Commits what changed (nothing when rerun after a commit that wasn't tagged) and pushes main.
commit() { git add -u && git add "$notes"; git diff --cached --quiet || git commit -q -m "Version $version"; git push -q origin main; }

command -v gh >/dev/null || die "the GitHub CLI (gh) is needed"
[[ "$(git branch --show-current)" == main ]] || die "not on main"

current=$(sed -n 's/^config\/version="\(.*\)"/\1/p' project.godot)
echo "Current version: $current (last tag: $(git describe --tags --abbrev=0 2>/dev/null || echo none))"
version=$(ask "New version (X.Y.Z): ")
version=${version#v}
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || die "'$version' isn't X.Y.Z"
tag="v$version"
git rev-parse -q --verify "refs/tags/$tag" >/dev/null && die "tag $tag already exists"
git ls-remote --exit-code --tags origin "$tag" >/dev/null 2>&1 && die "tag $tag already exists on GitHub"

# Changes to tracked files go into the release commit; show them first.
if [[ -n "$(git status --porcelain --untracked-files=no)" ]]; then
    git status --short --untracked-files=no
    [[ "$(ask "These changes will be committed with the release. Continue? [y/N] ")" == [yY]* ]] || exit 1
fi

notes="docs/releases/$tag.md"
if [[ ! -f "$notes" ]]; then
    last=$(ls docs/releases/v*.md 2>/dev/null | sort -V | tail -1 || true)
    { echo "## What's new"; echo "- "; echo
      [[ -n "$last" ]] && sed -n '/^## Install/,$p' "$last"; } > "$notes"
    ${EDITOR:-nano} "$notes"
fi
grep -q '^- [^ ]' "$notes" || die "$notes has no entries under \"What's new\""
echo "----- $notes -----"; cat "$notes"; echo "-----"

sed -i "s/^config\/version=\".*\"/config\/version=\"$version\"/" project.godot

echo "Building and testing…"
dotnet build Arcanum.csproj -v q -nologo | tail -3
for t in tests/*/; do dotnet test "$t" -v q --nologo | grep -E "Passed!|Failed!" || die "tests failed in $t"; done

if [[ "$(ask "Test the export on GitHub first (about 5 minutes)? [y/N] ")" == [yY]* ]]; then
    commit
    gh workflow run release.yml --ref main
    sleep 5
    run=$(gh run list --workflow release.yml -L 1 --json databaseId --jq '.[0].databaseId')
    gh run watch "$run" --exit-status -i 30 >/dev/null || die "export test failed: gh run view $run --log-failed"
    echo "Export test passed."
else
    commit
fi

[[ "$(ask "Publish $tag now? [y/N] ")" == [yY]* ]] || { echo "Not published (the commit is pushed; rerun to tag it)."; exit 0; }
git tag "$tag" && git push -q origin "$tag"
sleep 5
run=$(gh run list --workflow release.yml -L 1 --json databaseId --jq '.[0].databaseId')
echo "Building the release…"
gh run watch "$run" --exit-status -i 30 >/dev/null || die "release build failed: gh run view $run --log-failed"
gh release view "$tag" --json url,assets --jq '.url, (.assets[].name)'
