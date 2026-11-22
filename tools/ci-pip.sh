#!/bin/bash
# pip install with retries, for the CI workflows (RB1): a transient PyPI or CDN failure (a reset connection, a timeout, a 5xx) is not the
# change under test, and the step that installs numpy for tools/native-cross.py used to fail the job on the first one.
# Usage: tools/ci-pip.sh numpy
# pip's own --retries covers a dropped connection mid-download; the loop covers the index not answering at all.
for attempt in 1 2 3 4 5; do
  if python3 -m pip install --break-system-packages --disable-pip-version-check --retries 3 --timeout 30 "$@" >/dev/null; then
    exit 0
  fi
  echo "::warning::pip install $* failed (attempt $attempt of 5)"
  [ "$attempt" -lt 5 ] && sleep $((attempt * 10))
done
echo "::error::pip install $* failed 5 times"
exit 1
