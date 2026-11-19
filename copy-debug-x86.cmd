pwsh ./build-debug-x86.ps1
SET PLUGIN_DIR=C:\Program Files (x86)\Notepad++\plugins\markdown-ru

xcopy markdown-ru\bin\Debug\* "%PLUGIN_DIR%\lib" /c /s /r /d /y /i
xcopy "%PLUGIN_DIR%\lib\markdown-ru.*" "%PLUGIN_DIR%\" /c /s /r /d /y /i
del "%PLUGIN_DIR%\lib\markdown-ru.*"
