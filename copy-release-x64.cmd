pwsh ./build-release.ps1

xcopy markdown-ru\bin\Release-x64\markdown-ru.* "C:\Program Files\Notepad++\plugins\markdown-ru" /c /s /r /d /y /i
xcopy Webview2Viewer\bin\Release\*.* "c:\Program Files\Notepad++\plugins\markdown-ru\lib" /c /s /r /d /y /i
xcopy PanelCommon\bin\Release\*.* "c:\Program Files\Notepad++\plugins\markdown-ru\lib" /c /s /r /d /y /i
