[CmdletBinding()]
param(
    [string]$CacheRoot = '',
    [string]$OutputDirectory = '',
    [switch]$SkipTests,
    [switch]$KeepStage
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
if (-not $CacheRoot) {
    $tempRoot = if (Test-Path -LiteralPath 'D:\Temp') { 'D:\Temp\agent' } else { [IO.Path]::GetTempPath() }
    $CacheRoot = Join-Path $tempRoot 'markdown-ru-build'
}
$CacheRoot = [IO.Path]::GetFullPath($CacheRoot)
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectRoot 'Release' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $CacheRoot, $OutputDirectory -Force | Out-Null

$archiveLock = Join-Path $PSScriptRoot 'tests\dependencies.lock.json'
$archiveTool = Join-Path $PSScriptRoot 'tests\verified_archive.py'
$lockHash = (Get-FileHash -LiteralPath $archiveLock -Algorithm SHA256).Hash.ToLowerInvariant()
$packageCacheName = 'packages-verified-' + $lockHash.Substring(0, 16)
$packagesDirectory = Join-Path $CacheRoot $packageCacheName

function Get-BuildArchive([string]$Id, [string]$Destination) {
    $verified = & python -B $archiveTool --lock $archiveLock --artifact $Id --cache-root $CacheRoot --destination $Destination
    if ($LASTEXITCODE -ne 0) { throw "Build archive verification failed: $Id" }
    return $verified
}

function Get-BuildPackage([string]$Name, [string]$Version) {
    return Get-BuildArchive "$Name.$Version" (Join-Path $packageCacheName "$Name.$Version")
}

if (-not $SkipTests) {
    & python -B (Join-Path $PSScriptRoot 'tests\test_verified_archive.py')
    if ($LASTEXITCODE -ne 0) { throw 'Build cache tests failed.' }
}

$null = Get-BuildPackage 'DiffPlex' '1.9.0'
$null = Get-BuildPackage 'Newtonsoft.Json' '13.0.4'
$null = Get-BuildPackage 'Markdig' '1.4.0'
$null = Get-BuildPackage 'System.Memory' '4.6.3'
$null = Get-BuildPackage 'System.Buffers' '4.6.1'
$null = Get-BuildPackage 'System.Numerics.Vectors' '4.6.1'
$null = Get-BuildPackage 'System.Runtime.CompilerServices.Unsafe' '6.1.2'
$markdownAssemblies = @(
    (Join-Path $packagesDirectory 'Markdig.1.4.0\lib\net462\Markdig.dll'),
    (Join-Path $packagesDirectory 'System.Memory.4.6.3\lib\net462\System.Memory.dll'),
    (Join-Path $packagesDirectory 'System.Buffers.4.6.1\lib\net462\System.Buffers.dll'),
    (Join-Path $packagesDirectory 'System.Numerics.Vectors.4.6.1\lib\net462\System.Numerics.Vectors.dll'),
    (Join-Path $packagesDirectory 'System.Runtime.CompilerServices.Unsafe.6.1.2\lib\net462\System.Runtime.CompilerServices.Unsafe.dll')
)
$markdownReferences = @($markdownAssemblies | ForEach-Object { "/r:$_" })
$null = Get-BuildPackage 'Microsoft.Web.WebView2' '1.0.3650.58'
$references = Get-BuildPackage 'Microsoft.NETFramework.ReferenceAssemblies.net472' '1.0.3'
$disassembler = Get-BuildPackage 'runtime.win-x64.Microsoft.NETCore.ILDAsm' '8.0.0'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$msbuild = & $vswhere -latest -products '*' -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'Visual Studio Build Tools with MSBuild is required.' }
$buildDirectory = Join-Path $CacheRoot 'build'
$objDirectory = Join-Path $CacheRoot 'obj'
& $msbuild (Join-Path $projectRoot 'AnotherMarkdown.sln') /t:Rebuild /p:Configuration=Release /p:Platform=x64 /p:SkipLegacyDllExport=true "/p:PackagesDirectory=$packagesDirectory" "/p:TargetFrameworkRootPath=$references\build" "/p:OutDir=$buildDirectory\" "/p:BaseIntermediateOutputPath=$objDirectory\" /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw 'MSBuild failed.' }
foreach ($assembly in $markdownAssemblies) {
    Copy-Item -LiteralPath $assembly -Destination $CacheRoot -Force
    Copy-Item -LiteralPath $assembly -Destination $buildDirectory -Force
}

$ref = Join-Path $references 'build\.NETFramework\v4.7.2'
$csc = Join-Path (Split-Path $msbuild -Parent) 'Roslyn\csc.exe'
if (-not $SkipTests) {
    $historyTests = Join-Path $CacheRoot 'FileVisitHistoryTests.exe'
    & $csc /nologo /noconfig /nostdlib /langversion:7.3 /target:exe "/out:$historyTests" "/r:$ref\mscorlib.dll" "/r:$ref\System.dll" "/r:$ref\System.Core.dll" (Join-Path $projectRoot 'AnotherMarkdown\FileVisitHistory.cs') (Join-Path $projectRoot 'tests\FileVisitHistoryTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'History test compilation failed.' }
    & $historyTests
    if ($LASTEXITCODE -ne 0) { throw 'History tests failed.' }
    $pathTests = Join-Path $CacheRoot 'LocalFilePathTests.exe'
    & $csc /nologo /noconfig /nostdlib /langversion:7.3 /target:exe "/out:$pathTests" "/r:$ref\mscorlib.dll" "/r:$ref\System.dll" "/r:$ref\System.Web.dll" (Join-Path $projectRoot 'Webview2Viewer\Web\HttpUtility2.cs') (Join-Path $projectRoot 'tests\LocalFilePathTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Local file path test compilation failed.' }
    & $pathTests
    if ($LASTEXITCODE -ne 0) { throw 'Local file path tests failed.' }
    $linkTests = Join-Path $CacheRoot 'LocalFileLinkTests.exe'
    & $csc /nologo /noconfig /nostdlib /langversion:7.3 /target:exe "/out:$linkTests" "/r:$ref\mscorlib.dll" "/r:$ref\System.dll" "/r:$ref\System.Web.dll" (Join-Path $projectRoot 'PanelCommon\Events\NavigateToEvent.cs') (Join-Path $projectRoot 'Webview2Viewer\Web\HttpUtility2.cs') (Join-Path $projectRoot 'Webview2Viewer\Web\LocalFileLink.cs') (Join-Path $projectRoot 'tests\LocalFileLinkTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Local file link test compilation failed.' }
    & $linkTests
    if ($LASTEXITCODE -ne 0) { throw 'Local file link tests failed.' }
    $tests = Join-Path $CacheRoot 'TranslationTests.exe'
    $sources = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'AnotherMarkdown\Translation') -Filter '*.cs' | ForEach-Object FullName)
    $sources += Join-Path $projectRoot 'tests\TranslationTests.cs'
    $jsonAssembly = Join-Path $packagesDirectory 'Newtonsoft.Json.13.0.4\lib\net45\Newtonsoft.Json.dll'
    Copy-Item -LiteralPath $jsonAssembly -Destination $CacheRoot -Force
    & $csc "/r:$ref\System.Numerics.dll" @markdownReferences /nologo /noconfig /nostdlib /langversion:7.3 /target:exe "/out:$tests" "/r:$ref\mscorlib.dll" "/r:$ref\System.dll" "/r:$ref\System.Core.dll" "/r:$ref\System.Net.Http.dll" "/r:$ref\System.Security.dll" "/r:$jsonAssembly" $sources
    if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
    & $tests
    if ($LASTEXITCODE -ne 0) { throw 'Translation tests failed.' }
    $launcherTests = Join-Path $CacheRoot 'CliLauncherTests.exe'
    $launcherSources = @($sources | Where-Object { $_ -notlike '*TranslationTests.cs' })
    $launcherSources += Join-Path $projectRoot 'tests\CliLauncherTests.cs'
    & $csc "/r:$ref\System.Numerics.dll" @markdownReferences /nologo /noconfig /nostdlib /langversion:7.3 /target:exe "/out:$launcherTests" "/r:$ref\mscorlib.dll" "/r:$ref\System.dll" "/r:$ref\System.Core.dll" "/r:$ref\System.Net.Http.dll" "/r:$ref\System.Security.dll" "/r:$jsonAssembly" $launcherSources
    if ($LASTEXITCODE -ne 0) { throw 'Launcher test compilation failed.' }
    & $launcherTests
    if ($LASTEXITCODE -ne 0) { throw 'Launcher tests failed.' }
    foreach ($apiTestName in @('ApiTests', 'ApiConnectionStoreTests', 'CliConnectionStoreTests', 'SocksApiTests', 'ParallelTranslationTests', 'MarkdownStructureTests', 'CodeCommentSpanTests', 'CodeAnnotationTests', 'ProtectedTranslationTests', 'DiscoveryCacheTests', 'ApiLiveTests')) {
        $apiTests = Join-Path $CacheRoot ($apiTestName + '.exe')
        $apiSources = @($sources | Where-Object { $_ -notlike '*TranslationTests.cs' })
        $apiSources += Join-Path $projectRoot ('tests\' + $apiTestName + '.cs')
        & $csc "/r:$ref\System.Numerics.dll" @markdownReferences /nologo /noconfig /nostdlib /langversion:7.3 /target:exe "/out:$apiTests" "/r:$ref\mscorlib.dll" "/r:$ref\System.dll" "/r:$ref\System.Core.dll" "/r:$ref\System.Net.Http.dll" "/r:$ref\System.Security.dll" "/r:$jsonAssembly" $apiSources
        if ($LASTEXITCODE -ne 0) { throw "$apiTestName compilation failed." }
        if ($apiTestName -ne 'ApiLiveTests') {
            & $apiTests
            if ($LASTEXITCODE -ne 0) { throw "$apiTestName failed." }
        }
    }
    $settingsTests = Join-Path $CacheRoot 'SettingsTests.exe'
    $settingsSources = @($sources | Where-Object { $_ -notlike '*TranslationTests.cs' })
    $settingsSources += @('AnotherMarkdown\PluginBranding.cs', 'AnotherMarkdown\PluginIcon.cs', 'AnotherMarkdown\Forms\PluginLogo.cs', 'AnotherMarkdown\Entities\Settings.cs', 'AnotherMarkdown\Forms\SettingsForm.cs', 'AnotherMarkdown\Forms\SettingsForm.Designer.cs', 'AnotherMarkdown\Forms\SettingsForm.Translation.cs', 'AnotherMarkdown\Forms\SettingsForm.Api.cs', 'tests\SettingsTests.cs') | ForEach-Object { Join-Path $projectRoot $_ }
    & $csc "/r:$ref\System.Numerics.dll" @markdownReferences /nologo /noconfig /nostdlib /langversion:7.3 /target:exe "/out:$settingsTests" "/r:$ref\mscorlib.dll" "/r:$ref\System.dll" "/r:$ref\System.Core.dll" "/r:$ref\System.Net.Http.dll" "/r:$ref\System.Security.dll" "/r:$ref\System.Drawing.dll" "/r:$ref\System.Windows.Forms.dll" "/r:$jsonAssembly" $settingsSources
    if ($LASTEXITCODE -ne 0) { throw 'Settings test compilation failed.' }
    $testAssets = Join-Path $CacheRoot 'assets\markdown'
    New-Item -ItemType Directory -Path $testAssets -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $projectRoot 'assets\markdown\md.extensions.json') -Destination $testAssets -Force
    & $settingsTests
    if ($LASTEXITCODE -ne 0) { throw 'Settings tests failed.' }
    $startupTests = Join-Path $CacheRoot 'SettingsStartupTests.exe'
    $startupSources = @($settingsSources | Where-Object { $_ -notlike '*SettingsTests.cs' })
    $startupSources += Join-Path $projectRoot 'tests\SettingsStartupTests.cs'
    & $csc "/r:$ref\System.Numerics.dll" @markdownReferences /nologo /noconfig /nostdlib /langversion:7.3 /target:exe "/out:$startupTests" "/r:$ref\mscorlib.dll" "/r:$ref\System.dll" "/r:$ref\System.Core.dll" "/r:$ref\System.Net.Http.dll" "/r:$ref\System.Security.dll" "/r:$ref\System.Drawing.dll" "/r:$ref\System.Windows.Forms.dll" "/r:$jsonAssembly" $startupSources
    if ($LASTEXITCODE -ne 0) { throw 'Settings startup test compilation failed.' }
    & $startupTests
    if ($LASTEXITCODE -ne 0) { throw 'Settings startup tests failed.' }
    foreach ($pluginTestName in @('IniSettingsTests', 'PreviewProgressTests', 'DockingFocusTests', 'EditingTests', 'EditorDraftTests', 'NavigationTests', 'SyncedSourceCaretTests')) {
        $pluginTests = Join-Path $buildDirectory ($pluginTestName + '.exe')
        & $csc "/r:$ref\System.Numerics.dll" @markdownReferences /nologo /noconfig /nostdlib /langversion:7.3 /target:exe /platform:x64 "/out:$pluginTests" "/r:$ref\mscorlib.dll" "/r:$ref\System.dll" "/r:$ref\System.Core.dll" "/r:$ref\System.Windows.Forms.dll" "/r:$ref\System.Drawing.dll" "/r:$buildDirectory\AnotherMarkdown.dll" "/r:$buildDirectory\PanelCommon.dll" (Join-Path $projectRoot ('tests\' + $pluginTestName + '.cs'))
        if ($LASTEXITCODE -ne 0) { throw "$pluginTestName compilation failed." }
        if ($pluginTestName -eq 'EditingTests') { & $pluginTests (Join-Path $CacheRoot 'editing-tests') }
        else { & $pluginTests }
        if ($LASTEXITCODE -ne 0) { throw "$pluginTestName failed." }
    }
}

& python (Join-Path $PSScriptRoot 'export_plugin.py') (Join-Path $buildDirectory 'AnotherMarkdown.dll') --ildasm (Join-Path $disassembler 'runtimes\win-x64\native\ildasm.exe') --ilasm (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\ilasm.exe') --work $CacheRoot
if ($LASTEXITCODE -ne 0) { throw 'Native export failed.' }

# Keep the published upstream JavaScript bundle intact; overlay our loader and DLLs.
$upstream = Get-BuildArchive 'upstream-0.1.12-x64' ('upstream-0.1.12-verified-' + $lockHash.Substring(0, 16))
$stage = Join-Path $CacheRoot ('package-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage | Out-Null
try {
    Get-ChildItem -LiteralPath $upstream | Copy-Item -Destination $stage -Recurse
    Copy-Item -LiteralPath (Join-Path $buildDirectory 'AnotherMarkdown.dll') -Destination $stage -Force
    foreach ($name in @('Webview2Viewer.dll', 'PanelCommon.dll', 'Markdig.dll', 'System.Memory.dll', 'System.Buffers.dll', 'System.Numerics.Vectors.dll', 'System.Runtime.CompilerServices.Unsafe.dll')) {
        Copy-Item -LiteralPath (Join-Path $buildDirectory $name) -Destination (Join-Path $stage 'lib') -Force
    }
    $licenseDirectory = Join-Path $stage 'licenses'
    New-Item -ItemType Directory -Path $licenseDirectory -Force | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $projectRoot 'licenses') -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $licenseDirectory -Force }
    Copy-Item -LiteralPath (Join-Path $projectRoot 'assets\loader.js') -Destination (Join-Path $stage 'assets\loader.js') -Force
    Copy-Item -LiteralPath (Join-Path $projectRoot 'assets\image-preview.js') -Destination (Join-Path $stage 'assets\image-preview.js') -Force
    Copy-Item -LiteralPath (Join-Path $projectRoot 'assets\image-preview.css') -Destination (Join-Path $stage 'assets\image-preview.css') -Force
    Copy-Item -LiteralPath (Join-Path $projectRoot 'assets\editor') -Destination (Join-Path $stage 'assets\editor') -Recurse
    Copy-Item -LiteralPath (Join-Path $projectRoot 'assets\markdown\md.extensions.json') -Destination (Join-Path $stage 'assets\markdown\md.extensions.json') -Force
    foreach ($style in @('style.css', 'style-dark.css')) {
        Copy-Item -LiteralPath (Join-Path $projectRoot ('assets\markdown\' + $style)) -Destination (Join-Path $stage ('assets\markdown\' + $style)) -Force
    }
    Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\translation-ru.md') -Destination (Join-Path $stage 'TRANSLATION-RU.md')
    Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination (Join-Path $stage 'README.md') -Force
    Copy-Item -LiteralPath (Join-Path $projectRoot 'CHANGELOG.md') -Destination (Join-Path $stage 'CHANGELOG.md') -Force
    $helpDirectory = Join-Path $stage 'help'
    New-Item -ItemType Directory -Path $helpDirectory -Force | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $projectRoot 'help') -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $helpDirectory -Force }
    $guideDirectory = Join-Path $stage 'docs'
    New-Item -ItemType Directory -Path $guideDirectory -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\translation-ru.md') -Destination $guideDirectory -Force
    Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\visual-editor.md') -Destination $guideDirectory -Force
    Copy-Item -LiteralPath (Join-Path $projectRoot 'editor') -Destination $stage -Recurse
    $editorBuildDirectory = Join-Path $stage 'build_tools'
    New-Item -ItemType Directory -Path $editorBuildDirectory -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $projectRoot 'build_tools\build-editor.cjs') -Destination $editorBuildDirectory
    $editorTestDirectory = Join-Path $stage 'tests'
    New-Item -ItemType Directory -Path $editorTestDirectory -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $projectRoot 'tests\visual-editor.browser.cjs') -Destination $editorTestDirectory
    Copy-Item -LiteralPath (Join-Path $projectRoot 'tests\navigation.browser.cjs') -Destination $editorTestDirectory
    Copy-Item -LiteralPath (Join-Path $projectRoot 'tests\link-addresses.browser.cjs') -Destination $editorTestDirectory
    $brandDirectory = Join-Path $stage 'assets\branding'
    New-Item -ItemType Directory -Path $brandDirectory -Force | Out-Null
    Copy-Item -Path (Join-Path $projectRoot 'AnotherMarkdown\Resources\translate-ru.*') -Destination $brandDirectory -Force
    if (-not $SkipTests) {
        $placementTests = Join-Path $buildDirectory 'ImageWindowPlacementTests.exe'
        & $csc /nologo /noconfig /nostdlib /langversion:7.3 /target:exe /platform:x64 "/out:$placementTests" "/r:$ref\mscorlib.dll" "/r:$ref\System.dll" "/r:$ref\System.Core.dll" "/r:$ref\System.Windows.Forms.dll" "/r:$ref\System.Drawing.dll" "/r:$buildDirectory\Newtonsoft.Json.dll" (Join-Path $projectRoot 'Webview2Viewer\ImageWindowPlacement.cs') (Join-Path $projectRoot 'tests\ImageWindowPlacementTests.cs')
        if ($LASTEXITCODE -ne 0) { throw 'Image placement test compilation failed.' }
        & $placementTests (Join-Path $CacheRoot 'image-placement-tests')
        if ($LASTEXITCODE -ne 0) { throw 'Image placement tests failed.' }
        $imageTests = Join-Path $buildDirectory 'ImagePreviewTests.exe'
        & $csc /nologo /noconfig /nostdlib /langversion:7.3 /target:exe /platform:x64 "/out:$imageTests" "/r:$ref\mscorlib.dll" "/r:$ref\System.dll" "/r:$ref\System.Core.dll" "/r:$ref\System.Windows.Forms.dll" "/r:$ref\System.Drawing.dll" "/r:$buildDirectory\Webview2Viewer.dll" "/r:$buildDirectory\PanelCommon.dll" "/r:$buildDirectory\Microsoft.Web.WebView2.Core.dll" "/r:$buildDirectory\Microsoft.Web.WebView2.WinForms.dll" "/r:$buildDirectory\Newtonsoft.Json.dll" (Join-Path $projectRoot 'tests\ImagePreviewTests.cs')
        if ($LASTEXITCODE -ne 0) { throw 'Image preview test compilation failed.' }
        & $imageTests (Join-Path $stage 'assets') (Join-Path $projectRoot 'tests\image-preview.browser.js') (Join-Path $CacheRoot 'image-preview-tests')
        if ($LASTEXITCODE -ne 0) { throw 'Image preview tests failed.' }
        $editorTests = Join-Path $buildDirectory 'EditorIntegrationTests.exe'
        & $csc /nologo /noconfig /nostdlib /langversion:7.3 /target:exe /platform:x64 "/out:$editorTests" "/r:$ref\mscorlib.dll" "/r:$ref\System.dll" "/r:$ref\System.Core.dll" "/r:$ref\System.Windows.Forms.dll" "/r:$ref\System.Drawing.dll" "/r:$buildDirectory\Webview2Viewer.dll" "/r:$buildDirectory\PanelCommon.dll" "/r:$buildDirectory\Microsoft.Web.WebView2.Core.dll" "/r:$buildDirectory\Microsoft.Web.WebView2.WinForms.dll" "/r:$buildDirectory\Newtonsoft.Json.dll" (Join-Path $projectRoot 'tests\EditorIntegrationTests.cs')
        if ($LASTEXITCODE -ne 0) { throw 'Editor integration test compilation failed.' }
        & $editorTests (Join-Path $stage 'assets') (Join-Path $CacheRoot 'editor-integration-tests')
        if ($LASTEXITCODE -ne 0) { throw 'Editor integration tests failed.' }
    }
    $archive = Join-Path $OutputDirectory 'AnotherMarkdown-0.1.12-ru.37-x64.zip'
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $archive -Force
    Write-Output "Package: $archive"
    Get-FileHash -LiteralPath $archive -Algorithm SHA256
}
finally {
    $resolvedStage = [IO.Path]::GetFullPath($stage)
    if ($KeepStage) { Write-Output "Stage: $resolvedStage" }
    elseif ($resolvedStage.StartsWith($CacheRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $resolvedStage -Recurse
    }
}
