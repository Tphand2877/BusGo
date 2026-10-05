@echo off
title BusGo Production Host
echo ========================================================
echo   BusGo - Intercity Ticket Sales System (Production)
echo ========================================================
set ASPNETCORE_ENVIRONMENT=Production
set ASPNETCORE_URLS=http://+:5080
echo Starting application on http://localhost:5080...
echo Press Ctrl+C to stop.
echo ========================================================
dotnet bin\Release\publish\BusGo.dll
pause
