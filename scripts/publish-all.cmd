@echo off
rem Build release archives for all platforms (server + agent per platform).
rem Requires PowerShell; output lands in publish\dist\.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish-all.ps1" %*
