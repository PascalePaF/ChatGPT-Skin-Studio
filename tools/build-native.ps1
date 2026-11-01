[CmdletBinding()]
param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectRoot 'build\native' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $output.StartsWith((Join-Path $projectRoot 'build') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Native output must stay under the project build directory.' }
. (Join-Path $PSScriptRoot 'windows-toolchain.ps1')
$toolchain = Get-WindowsToolchain
$msvc = $toolchain.Msvc; $sdk = $toolchain.SdkRoot; $sdkVersion = $toolchain.SdkVersion
New-Item -ItemType Directory -Path $output -Force | Out-Null
$fxc = Join-Path $sdk "bin\$sdkVersion\x64\fxc.exe"
if (-not (Test-Path -LiteralPath $fxc -PathType Leaf)) { throw 'Windows SDK shader compiler is required.' }
$shaderSource = [IO.File]::ReadAllText((Join-Path $projectRoot 'native\renderer.cpp'))
$shaderMatch = [Text.RegularExpressions.Regex]::Match($shaderSource, '(?s)static const char\* shader = R"\((.*?)\)";')
if (-not $shaderMatch.Success) { throw 'Renderer shader source was not found.' }
$shaderFile = Join-Path $output 'renderer.hlsl'
[IO.File]::WriteAllText($shaderFile, $shaderMatch.Groups[1].Value, [Text.UTF8Encoding]::new($false))
foreach ($entry in @(@('VS','vs_4_0'),@('PS','ps_4_0'),@('Frost','ps_4_0'))) {
 & $fxc /nologo /WX /T $entry[1] /E $entry[0] /Fo (Join-Path $output ($entry[0]+'.shader')) $shaderFile
 if ($LASTEXITCODE -ne 0) { throw ('Shader compilation failed: '+$entry[0]) }
}
$previousInclude=$env:INCLUDE; $previousLib=$env:LIB
try {
 $env:INCLUDE=(Join-Path $msvc 'include'),(Join-Path $sdk "Include\$sdkVersion\ucrt"),(Join-Path $sdk "Include\$sdkVersion\shared"),(Join-Path $sdk "Include\$sdkVersion\um"),(Join-Path $sdk "Include\$sdkVersion\winrt"),(Join-Path $sdk "Include\$sdkVersion\cppwinrt") -join ';'
 $env:LIB=(Join-Path $msvc 'lib\x64'),(Join-Path $sdk "Lib\$sdkVersion\ucrt\x64"),(Join-Path $sdk "Lib\$sdkVersion\um\x64") -join ';'
 & (Join-Path $msvc 'bin\Hostx64\x64\cl.exe') /nologo /std:c++17 /EHsc /MT /O2 /DUNICODE /D_UNICODE /utf-8 (Join-Path $projectRoot 'native\renderer.cpp') ('/Fo'+(Join-Path $output 'SkinRenderer.obj')) ('/Fe'+(Join-Path $output 'SkinRenderer.exe')) /link /SUBSYSTEM:CONSOLE user32.lib windowsapp.lib d3d11.lib dxgi.lib d3dcompiler.lib dcomp.lib dwmapi.lib windowscodecs.lib mfplat.lib mfreadwrite.lib mfuuid.lib runtimeobject.lib
 if ($LASTEXITCODE -ne 0) { throw 'Native renderer compilation failed.' }
} finally { $env:INCLUDE=$previousInclude; $env:LIB=$previousLib }
Write-Output (Join-Path $output 'SkinRenderer.exe')
