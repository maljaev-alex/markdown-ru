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

function Get-BuildPackage([string]$Name, [string]$Version) {
    $destination = Join-Path $CacheRoot "packages\$Name.$Version"
    if (-not (Test-Path -LiteralPath $destination)) {
        $id = $Name.ToLowerInvariant()
        $archive = Join-Path $CacheRoot "$Name.$Version.zip"
        Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/$id/$Version/$id.$Version.nupkg" -OutFile $archive
        Expand-Archive -LiteralPath $archive -DestinationPath $destination
    }
    return $destination
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
& $msbuild (Join-Path $projectRoot 'AnotherMarkdown.sln') /t:Rebuild /p:Configuration=Release /p:Platform=x64 /p:SkipLegacyDllExport=true "/p:PackagesDirectory=$CacheRoot\packages" "/p:TargetFrameworkRootPath=$references\build" "/p:OutDir=$buildDirectory\" "/p:BaseIntermediateOutputPath=$objDirectory\" /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw 'MSBuild failed.' }

$ref = Join-Path $references 'build\.NETFramework\v4.7.2'
$csc = Join-Path (Split-Path $msbuild -Parent) 'Roslyn\csc.exe'
if (-not $SkipTests) {
    $tests = Join-Path $CacheRoot 'TranslationTests.exe'
    $sources = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'AnotherMarkdown\Translation') -Filter '*.cs' | ForEach-Object FullName)
    $sources += Join-Path $projectRoot 'tests\TranslationTests.cs'
    $jsonAssembly = Join-Path $CacheRoot 'packages\Newtonsoft.Json.13.0.4\lib\net45\Newtonsoft.Json.dll'
    Copy-Item -LiteralPath $jsonAssembly -Destination $CacheRoot -Force
    & $csc /nologo /noconfig /nostdlib /langversion:7.3 /target:exe "/out:$tests" "/r:$ref\mscorlib.dll" "/r:$ref\System.dll" "/r:$ref\System.Core.dll" "/r:$jsonAssembly" $sources
    if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
    & $tests
    if ($LASTEXITCODE -ne 0) { throw 'Translation tests failed.' }
    $settingsTests = Join-Path $CacheRoot 'SettingsTests.exe'
    $settingsSources = @($sources | Where-Object { $_ -notlike '*TranslationTests.cs' })
    $settingsSources += @('AnotherMarkdown\Entities\Settings.cs', 'AnotherMarkdown\Forms\SettingsForm.cs', 'AnotherMarkdown\Forms\SettingsForm.Designer.cs', 'AnotherMarkdown\Forms\SettingsForm.Translation.cs', 'tests\SettingsTests.cs') | ForEach-Object { Join-Path $projectRoot $_ }
    & $csc /nologo /noconfig /nostdlib /langversion:7.3 /target:exe "/out:$settingsTests" "/r:$ref\mscorlib.dll" "/r:$ref\System.dll" "/r:$ref\System.Core.dll" "/r:$ref\System.Drawing.dll" "/r:$ref\System.Windows.Forms.dll" "/r:$jsonAssembly" $settingsSources
    if ($LASTEXITCODE -ne 0) { throw 'Settings test compilation failed.' }
    $testAssets = Join-Path $CacheRoot 'assets\markdown'
    New-Item -ItemType Directory -Path $testAssets -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $projectRoot 'assets\markdown\md.extensions.json') -Destination $testAssets -Force
    & $settingsTests
    if ($LASTEXITCODE -ne 0) { throw 'Settings tests failed.' }
}

& python (Join-Path $PSScriptRoot 'export_plugin.py') (Join-Path $buildDirectory 'AnotherMarkdown.dll') --ildasm (Join-Path $disassembler 'runtimes\win-x64\native\ildasm.exe') --ilasm (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\ilasm.exe') --work $CacheRoot
if ($LASTEXITCODE -ne 0) { throw 'Native export failed.' }

# Keep the published upstream JavaScript bundle intact; overlay our loader and DLLs.
$upstream = Join-Path $CacheRoot 'upstream-0.1.12'
if (-not (Test-Path -LiteralPath (Join-Path $upstream 'AnotherMarkdown.dll'))) {
    $upstreamZip = Join-Path $CacheRoot 'upstream-0.1.12.zip'
    Invoke-WebRequest 'https://github.com/ezyuzin/NppAnotherMarkdown/releases/download/0.1.12/AnotherMarkdown-0.1.12-x64.zip' -OutFile $upstreamZip
    Expand-Archive -LiteralPath $upstreamZip -DestinationPath $upstream -Force
}
$stage = Join-Path $CacheRoot ('package-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage | Out-Null
try {
    Get-ChildItem -LiteralPath $upstream | Copy-Item -Destination $stage -Recurse
    Copy-Item -LiteralPath (Join-Path $buildDirectory 'AnotherMarkdown.dll') -Destination $stage -Force
    foreach ($name in @('Webview2Viewer.dll', 'PanelCommon.dll')) {
        Copy-Item -LiteralPath (Join-Path $buildDirectory $name) -Destination (Join-Path $stage 'lib') -Force
    }
    Copy-Item -LiteralPath (Join-Path $projectRoot 'assets\loader.js') -Destination (Join-Path $stage 'assets\loader.js') -Force
    Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\translation-ru.md') -Destination (Join-Path $stage 'TRANSLATION-RU.md')
    $archive = Join-Path $OutputDirectory 'AnotherMarkdown-0.1.12-ru.1-x64.zip'
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
