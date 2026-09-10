@echo off
rem Start an Infinity CI agent and connect it to the master.
rem Usage: start-agent.cmd [--Agent:EnrollToken=<token>] [--Agent:AgentName=name] [other Agent:* options]
rem First-time setup: create an enrollment token on the Agents page, then pass
rem it here once; the agent identity persists in agent-data\agent-id.txt.
setlocal
cd /d "%~dp0.."

echo Building InfinityCI.Agent ...
dotnet build src\InfinityCI.Agent -v q || goto :error

echo Starting agent (master: http://127.0.0.1:5001) ...
dotnet run --project src\InfinityCI.Agent --no-build -- %*
goto :eof

:error
echo Build failed.
exit /b 1
