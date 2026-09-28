@echo off
rem Double-clic pour lancer la configuration (demande les droits administrateur).
rem Les arguments sont transmis au script, ex. : Lancer.bat -Skip Office -AllTweaks
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Configurator.ps1" %*
if errorlevel 1 pause
