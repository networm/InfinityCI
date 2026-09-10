@echo off
rem Publish self-contained single-file executables to publish\.
setlocal
cd /d "%~dp0.."
set RID=win-x64

echo Publishing server ...
dotnet publish src\InfinityCI.Server -c Release -r %RID% --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -o publish\server || goto :error

echo Publishing agent ...
dotnet publish src\InfinityCI.Agent -c Release -r %RID% --self-contained true ^
  -p:PublishSingleFile=true ^
  -o publish\agent || goto :error

echo Done: publish\server\InfinityCI.Server.exe, publish\agent\InfinityCI.Agent.exe
goto :eof

:error
echo Publish failed.
exit /b 1
