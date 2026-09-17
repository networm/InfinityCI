@echo off
rem Build the Infinity CI server Docker image.
rem Usage: scripts\docker-build.cmd [tag]  (default: latest; extra tags allowed: build.cmd latest v1.0)
setlocal enabledelayedexpansion
cd /d "%~dp0.."

if "%~1"=="" (
    set "TAGS=latest"
) else (
    set "TAGS=%*"
)

set "IMAGE=infinityci-server"
set "DOCKER_ARGS="
for %%t in (%TAGS%) do (
    echo Building image %IMAGE%:%%t ...
    set "DOCKER_ARGS=!DOCKER_ARGS! -t %IMAGE%:%%t"
)

docker build !DOCKER_ARGS! . || goto :error
echo Done. Run with: docker run -d -p 5000:5000 -p 5001:5001 -v infinityci-data:/app/data %IMAGE%:latest
goto :eof

:error
echo Docker build failed.
exit /b 1
