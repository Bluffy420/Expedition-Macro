@echo off
set "PATH=C:\Users\Shreshth Bhardwaj\dotnet-sdk;%PATH%"
set "DOTNET_ROOT=C:\Users\Shreshth Bhardwaj\dotnet-sdk"
title Live Score Monitor
"C:\Users\Shreshth Bhardwaj\Desktop\expeditions-macro-src\tools\ScoreProbe\bin\Release\net10.0-windows10.0.19041.0\ScoreProbe.exe" "C:\Users\Shreshth Bhardwaj\AppData\Local\ExpeditionsMacro\camera-models\school-6cd2e33185"
echo.
echo Monitor exited. Press any key to close.
pause
