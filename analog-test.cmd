@echo off
title BladeCtl analog engine test
echo This turns the analog engine ON for 30 seconds and records where every keystroke comes from.
echo Type a sentence on the Huntsman during the countdown. Doubled letters = the keyboard is still typing on its own.
echo.
"%~dp0BladeCtl.exe" --cmd analog on
timeout /t 6 /nobreak >nul
"%~dp0tools\BladeProbe.exe" --kbd-watch 30
"%~dp0BladeCtl.exe" --cmd analog off
echo.
echo Done - engine switched back off. Results: kbd-watch.txt next to BladeCtl.exe.
pause
