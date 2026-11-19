@REM pwsh ./build-debug-x64.ps1

SET PLUGIN_DIR=C:\Program Files\Notepad++\plugins\markdown-ru

xcopy markdown-ru\bin\Debug-x64\* "%PLUGIN_DIR%\lib" /c /s /r /d /y /i
xcopy "%PLUGIN_DIR%\lib\markdown-ru.*" "%PLUGIN_DIR%\" /c /s /r /d /y /i
del "%PLUGIN_DIR%\lib\markdown-ru.*"
PAUSE