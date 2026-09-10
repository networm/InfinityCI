@echo off
rem Build and start the Infinity CI server (web: 5000, agent gRPC: 5001).
rem Requires the .NET 10 SDK: https://dotnet.microsoft.com/download/dotnet/10.0
setlocal
cd /d "%~dp0.."

echo Building InfinityCI.Server ...
dotnet build src\InfinityCI.Server -v q || goto :error

echo Starting server on http://127.0.0.1:5000 (gRPC :5001) ...
dotnet run --project src\InfinityCI.Server --no-build
goto :eof

:error
echo Build failed.
exit /b 1
