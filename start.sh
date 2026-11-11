#!/usr/bin/env bash
# TiltGuard one-click start (macOS / Linux): installs on first run, then starts the server and opens the dashboard.
set -e
cd "$(dirname "$0")/backend"
command -v python3 >/dev/null || { echo "Python 3.10+ is required"; exit 1; }
if [ ! -x .venv/bin/python ]; then
  echo "[TiltGuard] First run: creating virtual environment and installing dependencies..."
  python3 -m venv .venv
  .venv/bin/python -m pip install -q --upgrade pip
  .venv/bin/python -m pip install -q -r requirements.txt
fi
.venv/bin/python -c "import db,sys; sys.exit(0 if db.kv_get('auth') else 1)" 2>/dev/null || {
  echo "[TiltGuard] Choose a password for the dashboard:"; .venv/bin/python server.py --set-password; }
echo "[TiltGuard] Device key (paste it in the extension / NT8 add-on):"; .venv/bin/python server.py --key
( sleep 2; (xdg-open http://127.0.0.1:8940 || open http://127.0.0.1:8940) >/dev/null 2>&1 ) &
exec .venv/bin/python server.py
