#!/usr/bin/env bash
# Syncs the GitHub milestones of the current repository with docs/milestones/*.md, the single source
# of truth (one file per milestone or gate, named <ID>-<slug>.md, e.g. M0-foundation.md, G1-...):
#   title       = the file's "# " heading
#   description = its metadata line (phase, OpenSpec change, dependencies) and the file path
#   state       = closed once every exit criterion is ticked and its OpenSpec change (if any) is
#                 archived under openspec/changes/archive/; open otherwise
# Existing milestones are matched by ID (M0…M8, G1, G2), so a renamed heading updates in place.
# Nothing is ever deleted; milestones with an ID that has no file are reported.
#
# Usage:    scripts/sync-github-milestones.sh [--dry-run]
# Requires: GitHub CLI (`gh auth login`, or GH_TOKEN in CI), run from inside the repo clone.
set -euo pipefail
cd "$(git rev-parse --show-toplevel)"

dry_run=false
[[ "${1:-}" == "--dry-run" ]] && dry_run=true

# number <TAB> title <TAB> state <TAB> description
existing="$(gh api 'repos/{owner}/{repo}/milestones?state=all&per_page=100' \
  --jq '.[] | [.number, .title, .state, (.description // "")] | @tsv')"

# Roadmap order, taken from the table in docs/milestones/README.md, so milestone numbers follow it;
# files missing from that table are synced last.
files=()
while read -r name; do
  files+=("docs/milestones/$name")
done < <(grep -oE '\(([MG][0-9]+-[^)]*\.md)\)' docs/milestones/README.md | tr -d '()')
for file in docs/milestones/[MG][0-9]*-*.md; do
  [[ " ${files[*]} " == *" $file "* ]] || { echo "note: $file is not in docs/milestones/README.md" >&2; files+=("$file"); }
done

ids=()
for file in "${files[@]}"; do
  id="$(basename "$file" | cut -d- -f1)"
  ids+=("$id")
  title="$(sed -n '1s/^# //p' "$file")"
  meta="$(grep -m1 '^\*\*' "$file" | sed 's/\*//g; s/`//g')"
  description="$meta · $file"

  state=open
  change="$(grep -o -m1 'openspec/changes/[a-z0-9-]*' "$file" || true)"
  if ! grep -q '^- \[ \]' "$file"; then
    if [[ -z "$change" ]] || { [[ ! -d "$change" ]] && compgen -G "openspec/changes/archive/*-${change##*/}" >/dev/null; }; then
      state=closed
    fi
  fi

  match="$(awk -F'\t' -v id="$id" '$2 ~ ("^(Gate )?" id " ") { print; exit }' <<<"$existing")"
  if [[ -z "$match" ]]; then
    action=create
  else
    IFS=$'\t' read -r number current_title current_state current_description <<<"$match"
    if [[ "$current_title" == "$title" && "$current_state" == "$state" && "$current_description" == "$description" ]]; then
      action=same
    else
      action=update
    fi
  fi

  printf '%-6s %-7s %s\n' "$action" "$state" "$title"
  $dry_run && continue
  case "$action" in
    create) gh api 'repos/{owner}/{repo}/milestones' \
              -f title="$title" -f description="$description" -f state="$state" >/dev/null ;;
    update) gh api -X PATCH "repos/{owner}/{repo}/milestones/$number" \
              -f title="$title" -f description="$description" -f state="$state" >/dev/null ;;
  esac
done

# Milestones that look like ours but have no file: report, never delete.
while IFS=$'\t' read -r _ title _ _; do
  [[ -z "$title" ]] && continue
  if [[ "$title" =~ ^(Gate\ )?([MG][0-9]+)\  ]] && [[ ! " ${ids[*]} " == *" ${BASH_REMATCH[2]} "* ]]; then
    echo "extra  -       $title (no docs/milestones file; left as is)"
  fi
done <<<"$existing"
