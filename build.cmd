@echo off
REM Double-click me on Windows to build both eq2advanced plugin DLLs against ACT.
REM This is the build that produces a LOADABLE plugin -- see build.ps1 for why
REM the GitHub Actions artifact is only a compile check.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
pause
