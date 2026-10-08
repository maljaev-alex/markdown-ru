[CmdletBinding()]
param(
    [string]$CacheRoot = '',
    [string]$OutputDirectory = '',
    [switch]$SkipTests
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

$ref = Join-Path $references 'build\.NETFramework\v4.7.2'
$csc = Join-Path (Split-Path $msbuild -Parent) 'Roslyn\csc.exe'
if (-not $SkipTests) {
    $tests = Join-Path $CacheRoot 'TranslationTests.exe'
    $sources = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'AnotherMarkdown\Translation') -Filter '*.cs' | ForEach-Object FullName)
    $sources += Join-Path $projectRoot 'tests\TranslationTests.cs'
    $jsonAssembly = Join-Path $packagesDirectory 'Newtonsoft.Json.13.0.4\lib\net45\Newtonsoft.Json.dll'
    Copy-Item -LiteralPath $jsonAssembly -Destination $CacheRoot -Force
    & $csc /nologo /noconfig /nostdlib /langversion:7.3 /target:exe "/out:$tests" "/r:$ref\mscorlib.dll" "/r:$ref\System.dll" "/r:$ref\System.Core.dll" "/r:$ref\System.Net.Http.dll" "/r:$ref\System.Security.dll" "/r:$jsonAssembly" $sources
    if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
    & $tests
    if ($LASTEXITCODE -ne 0) { throw 'Translation tests failed.' }
    $launcherTests = Join-Path $CacheRoot 'CliLauncherTests.exe'
    $launcherSources = @($sources | Where-Object { $_ -notlike '*TranslationTests.cs' })
    $launcherSources += Join-Path $projectRoot 'tests\CliLauncherTests.cs'
    & $csc /nologo /noconfig /nostdlib /langversion:7.3 /target:exe "/out:$launcherTests" "/r:$ref\mscorlib.dll" "/r:$ref\System.dll" "/r:$ref\System.Core.dll" "/r:$ref\System.Net.Http.dll" "/r:$ref\System.Security.dll" "/r:$jsonAssembly" $launcherSources
    if ($LASTEXITCODE -ne 0) { throw 'Launcher test compilation failed.' }
    & $launcherTests
    if ($LASTEXITCODE -ne 0) { throw 'Launcher tests failed.' }
    foreach ($apiTestName in @('ApiTests', 'ApiConnectionStoreTests', 'SocksApiTests', 'ApiLiveTests')) {
        $apiTests = Join-Path $CacheRoot ($apiTestName + '.exe')
        $apiSources = @($sources | Where-Object { $_ -notlike '*TranslationTests.cs' })
        $apiSources += Join-Path $projectRoot ('tests\' + $apiTestName + '.cs')
        & $csc /nologo /noconfig /nostdlib /langversion:7.3 /target:exe "/out:$apiTests" "/r:$ref\mscorlib.dll" "/r:$ref\System.dll" "/r:$ref\System.Core.dll" "/r:$ref\System.Net.Http.dll" "/r:$ref\System.Security.dll" "/r:$jsonAssembly" $apiSources
        if ($LASTEXITCODE -ne 0) { throw "$apiTestName compilation failed." }
        if ($apiTestName -ne 'ApiLiveTests') {
            & $apiTests
            if ($LASTEXITCODE -ne 0) { throw "$apiTestName failed." }
        }
    }
    $settingsTests = Join-Path $CacheRoot 'SettingsTests.exe'
    $settingsSources = @($sources | Where-Object { $_ -notlike '*TranslationTests.cs' })
    $settingsSources += @('AnotherMarkdown\PluginBranding.cs', 'AnotherMarkdown\PluginIcon.cs', 'AnotherMarkdown\Forms\PluginLogo.cs', 'AnotherMarkdown\Entities\Settings.cs', 'AnotherMarkdown\Forms\SettingsForm.cs', 'AnotherMarkdown\Forms\SettingsForm.Designer.cs', 'AnotherMarkdown\Forms\SettingsForm.Translation.cs', 'AnotherMarkdown\Forms\SettingsForm.Api.cs', 'tests\SettingsTests.cs') | ForEach-Object { Join-Path $projectRoot $_ }
    & $csc /nologo /noconfig /nostdlib /langversion:7.3 /target:exe "/out:$settingsTests" "/r:$ref\mscorlib.dll" "/r:$ref\System.dll" "/r:$ref\System.Core.dll" "/r:$ref\System.Net.Http.dll" "/r:$ref\System.Security.dll" "/r:$ref\System.Drawing.dll" "/r:$ref\System.Windows.Forms.dll" "/r:$jsonAssembly" $settingsSources
    if ($LASTEXITCODE -ne 0) { throw 'Settings test compilation failed.' }
    $testAssets = Join-Path $CacheRoot 'assets\markdown'
    New-Item -ItemType Directory -Path $testAssets -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $projectRoot 'assets\markdown\md.extensions.json') -Destination $testAssets -Force
    & $settingsTests
    if ($LASTEXITCODE -ne 0) { throw 'Settings tests failed.' }
    $iniTests = Join-Path $buildDirectory 'IniSettingsTests.exe'
    & $csc /nologo /noconfig /nostdlib /langversion:7.3 /target:exe /platform:x64 "/out:$iniTests" "/r:$ref\mscorlib.dll" "/r:$ref\System.dll" "/r:$ref\System.Core.dll" "/r:$ref\System.Windows.Forms.dll" "/r:$ref\System.Drawing.dll" "/r:$buildDirectory\AnotherMarkdown.dll" (Join-Path $projectRoot 'tests\IniSettingsTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'INI test compilation failed.' }
    & $iniTests
    if ($LASTEXITCODE -ne 0) { throw 'INI tests failed.' }
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
    foreach ($name in @('Webview2Viewer.dll', 'PanelCommon.dll')) {
        Copy-Item -LiteralPath (Join-Path $buildDirectory $name) -Destination (Join-Path $stage 'lib') -Force
    }
    Copy-Item -LiteralPath (Join-Path $projectRoot 'assets\loader.js') -Destination (Join-Path $stage 'assets\loader.js') -Force
    Copy-Item -LiteralPath (Join-Path $projectRoot 'assets\markdown\md.extensions.json') -Destination (Join-Path $stage 'assets\markdown\md.extensions.json') -Force
    Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\translation-ru.md') -Destination (Join-Path $stage 'TRANSLATION-RU.md')
    Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination (Join-Path $stage 'README.md') -Force
    Copy-Item -LiteralPath (Join-Path $projectRoot 'CHANGELOG.md') -Destination (Join-Path $stage 'CHANGELOG.md') -Force
    $guideDirectory = Join-Path $stage 'docs'
    New-Item -ItemType Directory -Path $guideDirectory -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\translation-ru.md') -Destination $guideDirectory -Force
    $brandDirectory = Join-Path $stage 'assets\branding'
    New-Item -ItemType Directory -Path $brandDirectory -Force | Out-Null
    Copy-Item -Path (Join-Path $projectRoot 'AnotherMarkdown\Resources\translate-ru.*') -Destination $brandDirectory -Force
    $archive = Join-Path $OutputDirectory 'AnotherMarkdown-0.1.12-ru.8-x64.zip'
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $archive -Force
    Write-Output "Package: $archive"
    Get-FileHash -LiteralPath $archive -Algorithm SHA256
}
finally {
    $resolvedStage = [IO.Path]::GetFullPath($stage)
    if ($resolvedStage.StartsWith($CacheRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $resolvedStage -Recurse -Force
    }
}
