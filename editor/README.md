# Markdown visual editor

CKEditor 5 48.0.0 with the official Markdown/GFM processor. Only Markdown-compatible
features are enabled. `src/source.js` preserves unchanged source spans and opaque
blocks; `src/index.js` handles revision checking, three-way merging and per-document drafts.
The native host accepts writes only for the currently active buffer and matching base text.

The committed `assets/editor` bundle is included by the native packaging script.
To rebuild it, install the locked dependencies outside the checkout (Node.js 22+):

```powershell
$editorDeps = 'D:\Temp\agent\markdown-ru-editor-deps'
New-Item -ItemType Directory -Path $editorDeps
Copy-Item -LiteralPath .\editor\package.json, .\editor\package-lock.json -Destination $editorDeps
npm ci --prefix $editorDeps --cache 'D:\Temp\agent\markdown-ru-npm-cache'
node .\build_tools\build-editor.cjs $editorDeps
```

Browser regressions run headlessly. Keep browser downloads and test profiles outside the checkout:

```powershell
$env:PLAYWRIGHT_BROWSERS_PATH = 'D:\Temp\agent\markdown-ru-browsers'
node "$editorDeps\node_modules\playwright-core\cli.js" install chromium --only-shell
node .\tests\visual-editor.browser.cjs $editorDeps 'D:\Temp\agent\markdown-ru-browser-tests'
node .\tests\navigation.browser.cjs $editorDeps 'D:\Temp\agent\markdown-ru-navigation-tests'
```

Link-address checks default to the installed Chrome channel:

```powershell
node .\tests\link-addresses.browser.cjs $editorDeps 'D:\Temp\agent\markdown-ru-link-tests'
```

Set `AM_BROWSER_CHANNEL=chrome` to use an installed Chrome for all three suites,
or `AM_BROWSER_CHANNEL=chromium` to use the downloaded Playwright browser for all of them.

`build_tools/build-translation.ps1` also runs native encoding/export/draft tests and
an integration harness using the installed WebView2 Runtime. Tests use local fixtures;
they do not send documents to translation services.

CKEditor is distributed here under GPL-2.0-or-later (`licenseKey: 'GPL'`), with its
license and notices in `assets/editor`. The original MIT license of the native plugin
does not replace CKEditor's license. CKEditor upstream source is available at
https://github.com/ckeditor/ckeditor5/tree/v48.0.0; npm package integrity and dependency
versions are pinned in `package-lock.json`. This directory and the build script are
included in the release package so the integration bundle can be rebuilt.
