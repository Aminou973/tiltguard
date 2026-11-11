@echo off
rem TiltGuard one-click start (Windows): installs on first run, then starts the server and opens the dashboard.
setlocal
cd /d "%~dp0backend"
where python >nul 2>nul || (echo Python 3.10+ is required: https://www.python.org/downloads/ ^(tick "Add python.exe to PATH"^) & pause & exit /b 1)
if not exist .venv\Scripts\python.exe (
  echo [TiltGuard] First run: creating virtual environment and installing dependencies...
  python -m venv .venv || (pause & exit /b 1)
  .venv\Scripts\python -m pip install -q --upgrade pip
  .venv\Scripts\python -m pip install -q -r requirements.txt || (pause & exit /b 1)
)
.venv\Scripts\python -c "import db,sys; sys.exit(0 if db.kv_get('auth') else 1)" 2>nul || (
  echo [TiltGuard] Choose a password for the dashboard:
  .venv\Scripts\python server.py --set-password || (pause & exit /b 1)
)
echo [TiltGuard] Device key ^(paste it in the extension / NT8 add-on^):
.venv\Scripts\python server.py --key
start "" http://127.0.0.1:8940
.venv\Scripts\python server.py
pause
