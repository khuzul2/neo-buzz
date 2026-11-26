#!/usr/bin/env bash
# The Antigravity loop (docs/tasks/README.md): wait for your turn on GitHub, or change a task's label.
#   tools/ag-wait.sh workhorse | advisor     waits (checks every 5 minutes), then prints "TASK <issue> <label>"
#   tools/ag-wait.sh swap <issue> <old> <new>
set -u
R=khuzul2/neo-buzz
case "${1:-}" in
  workhorse) want='["ag:ready","ag:plan-fix","ag:go","ag:fix","ag:approved"]' ;;
  advisor)   want='["ag:plan-review","ag:review"]' ;;
  swap)
    [ $# -eq 4 ] || { echo "usage: $0 swap <issue> <old label> <new label>" >&2; exit 2; }
    gh api -X DELETE "repos/$R/issues/$2/labels/$3" >/dev/null && \
    gh api -X POST "repos/$R/issues/$2/labels" -f "labels[]=$4" >/dev/null && echo "#$2: $3 -> $4"
    exit $? ;;
  *) echo "usage: $0 workhorse | advisor | swap <issue> <old> <new>" >&2; exit 2 ;;
esac
while :; do
  t=$(gh api "repos/$R/issues?state=open&labels=owner:antigravity&per_page=50" \
      --jq "[.[] | . as \$i | .labels[] | select(.name as \$n | $want | index(\$n)) | \"\(\$i.number) \(.name)\"] | sort | first // empty" 2>/dev/null)
  [ -n "$t" ] && { echo "TASK $t"; exit 0; }
  sleep 300
done
