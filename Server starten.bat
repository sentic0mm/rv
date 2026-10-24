@echo off
chcp 65001 >nul
title R.E.P.O. Valuables Server
cd /d "%~dp0"
set PY=python
where python >nul 2>nul || set PY=py
%PY% serve.py
pause
