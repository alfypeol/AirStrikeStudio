@echo off
cd /d "%~dp0"
python -m pip install pyinstaller
python -m PyInstaller --onefile --windowed --name ScriptStudio scrstudio.py
if errorlevel 1 goto failed
echo.
echo Done: dist\ScriptStudio.exe
pause
exit /b 0
:failed
echo.
echo BUILD FAILED - see the messages above.
pause
exit /b 1
