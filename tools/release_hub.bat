@echo off
title Deadlock Playground - Release Hub
echo =======================================================
echo Starting Deadlock Playground Release Hub...
echo =======================================================
cd /d "%~dp0"
python release_hub\server.py
if %ERRORLEVEL% NEQ 0 (
    echo.
    echo Release Hub stopped with an error or was closed.
    pause
)
