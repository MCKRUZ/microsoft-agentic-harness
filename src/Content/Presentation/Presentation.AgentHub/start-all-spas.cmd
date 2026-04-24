@echo off
pushd "%~dp0..\Presentation.Dashboard"
start "Dashboard Vite" cmd.exe /c "npm run dev"
popd
cd /d "%~dp0..\Presentation.WebUI"
npm run dev
