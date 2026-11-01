param([switch]$SourceFidelity, [switch]$ContrastExperiment)
$ErrorActionPreference='Stop'
$projectRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
& (Join-Path $projectRoot 'tools\build-native.ps1')
. (Join-Path $projectRoot 'tools\windows-toolchain.ps1')
$toolchain=Get-WindowsToolchain
$msvc=$toolchain.Msvc; $sdk=$toolchain.SdkRoot; $sdkVersion=$toolchain.SdkVersion
$output=Join-Path $projectRoot 'build\validation\v1.1.4'; [IO.Directory]::CreateDirectory($output) | Out-Null
$oldInclude=$env:INCLUDE; $oldLib=$env:LIB
try {
 $env:INCLUDE=(Join-Path $msvc 'include'),(Join-Path $sdk "Include\$sdkVersion\ucrt"),(Join-Path $sdk "Include\$sdkVersion\shared"),(Join-Path $sdk "Include\$sdkVersion\um"),(Join-Path $sdk "Include\$sdkVersion\winrt") -join ';'
 $env:LIB=(Join-Path $msvc 'lib\x64'),(Join-Path $sdk "Lib\$sdkVersion\ucrt\x64"),(Join-Path $sdk "Lib\$sdkVersion\um\x64") -join ';'
 & (Join-Path $msvc 'bin\Hostx64\x64\cl.exe') /nologo /std:c++17 /EHsc /MT /O2 /utf-8 (Join-Path $PSScriptRoot 'ReadabilityChecks.cpp') ('/Fo'+(Join-Path $output 'ReadabilityChecks.obj')) ('/Fe'+(Join-Path $output 'ReadabilityChecks.exe')) /link d3d11.lib d3dcompiler.lib user32.lib
 if ($LASTEXITCODE -ne 0) { throw 'Readability test compilation failed.' }
 $arguments=@((Join-Path $projectRoot 'build\native')); $report='readability-checks.txt'
 # The forced-contrast experiment was rejected because it changed real text. Fidelity is the release criterion.
 if($SourceFidelity -or -not $ContrastExperiment){$arguments+='--fidelity';$report='source-fidelity-checks.txt'}
 & (Join-Path $output 'ReadabilityChecks.exe') @arguments | Tee-Object -FilePath (Join-Path $output $report)
 if ($LASTEXITCODE -ne 0) { throw 'Readability GPU checks failed.' }
} finally { $env:INCLUDE=$oldInclude; $env:LIB=$oldLib }
