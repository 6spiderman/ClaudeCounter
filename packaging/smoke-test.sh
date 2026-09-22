#!/usr/bin/env bash
#
# Launches a published ClaudeCounter (Linux) and checks it survives startup.
# Mirrors packaging/smoke-test.ps1: unit tests cannot catch a crash in the
# Avalonia wiring - the app has to actually run. This project has already hit
# two real ones this way (a flyout that could never reopen once closed, and a
# spurious "crash" logged on ordinary exit) that no unit test would catch.
#
# Needs a display and a D-Bus session bus, neither of which exist on a bare
# CI runner - run this under `xvfb-run` with `dbus-run-session`, e.g.:
#
#   xvfb-run -a dbus-run-session -- packaging/smoke-test.sh dist/linux-x64/ClaudeCounter
#
# Moves any existing ClaudeCounter data aside so the run is a genuine first
# run and cannot corrupt a real session, launches it, and fails if the
# process died or logged an [ERROR] line. Whatever the run created is
# deleted and the original data (including any autostart entry) restored,
# whatever happens.

set -uo pipefail

EXE_PATH="${1:?usage: smoke-test.sh <path to ClaudeCounter>}"
SECONDS_TO_WATCH="${2:-12}"

if [ ! -x "$EXE_PATH" ]; then
  echo "Not found or not executable: $EXE_PATH" >&2
  exit 1
fi
EXE_PATH="$(readlink -f "$EXE_PATH")"

# ClaudeCounter has no single-instance guard on Linux yet (see the Linux-port
# plan), so unlike the Windows script this cannot tell "a second copy" apart
# from "the first copy" by process count alone - just refuse to run if one is
# already up, so this script's own teardown does not kill someone else's run.
if pgrep -f "$EXE_PATH" > /dev/null; then
  echo "An instance of $EXE_PATH is already running - close it first." >&2
  exit 1
fi

DATA_DIR="$HOME/.local/share/ClaudeCounter"
CONFIG_DIR="$HOME/.config/ClaudeCounter"
AUTOSTART_FILE="$HOME/.config/autostart/claudecounter.desktop"
STASH="$(mktemp -d)"
LOG_PATH="$DATA_DIR/logs/claudecounter.log"

restore() {
  pkill -f "$EXE_PATH" 2> /dev/null
  sleep 0.5

  rm -rf "$DATA_DIR" "$CONFIG_DIR" "$AUTOSTART_FILE"
  [ -d "$STASH/data" ] && mv "$STASH/data" "$DATA_DIR" && echo "Restored $DATA_DIR"
  [ -d "$STASH/config" ] && mv "$STASH/config" "$CONFIG_DIR" && echo "Restored $CONFIG_DIR"
  [ -f "$STASH/autostart.desktop" ] && mkdir -p "$(dirname "$AUTOSTART_FILE")" && \
    mv "$STASH/autostart.desktop" "$AUTOSTART_FILE" && echo "Restored $AUTOSTART_FILE"
  rm -rf "$STASH"
}
trap restore EXIT

[ -d "$DATA_DIR" ] && mv "$DATA_DIR" "$STASH/data" && echo "Stashed $DATA_DIR"
[ -d "$CONFIG_DIR" ] && mv "$CONFIG_DIR" "$STASH/config" && echo "Stashed $CONFIG_DIR"
[ -f "$AUTOSTART_FILE" ] && mv "$AUTOSTART_FILE" "$STASH/autostart.desktop" && echo "Stashed $AUTOSTART_FILE"

echo "Launching: $EXE_PATH"
DOTNET_ROLL_FORWARD=LatestMajor "$EXE_PATH" &
PID=$!

echo "PID $PID - watching for ${SECONDS_TO_WATCH}s..."
end=$((SECONDS + SECONDS_TO_WATCH))
while [ "$SECONDS" -lt "$end" ]; do
  kill -0 "$PID" 2> /dev/null || break
  sleep 0.5
done

exit_code=0
if ! kill -0 "$PID" 2> /dev/null; then
  wait "$PID"
  echo "FAIL: the process exited with code $?" >&2
  if [ -f "$LOG_PATH" ]; then
    echo "--- log ---"
    cat "$LOG_PATH"
  else
    echo "No log was written - it died before logging anything." >&2
  fi
  exit_code=1
else
  echo "Still running after ${SECONDS_TO_WATCH}s."
  kill "$PID"
  wait "$PID" 2> /dev/null
  sleep 0.5

  if [ -f "$LOG_PATH" ]; then
    if grep -q '\[ERROR\]' "$LOG_PATH"; then
      echo "FAIL: errors in the log:" >&2
      grep '\[ERROR\]' "$LOG_PATH" >&2
      exit_code=1
    else
      echo "No errors logged."
      echo "--- log ---"
      cat "$LOG_PATH"
    fi
  else
    echo "FAIL: no log file was created." >&2
    exit_code=1
  fi
fi

if [ "$exit_code" -ne 0 ]; then
  echo "Smoke test failed." >&2
  exit 1
fi
echo "Smoke test passed."
