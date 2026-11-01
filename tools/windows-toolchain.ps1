function Get-WindowsToolchain {
    $compilerDirectory = $null
    if ($env:VCToolsInstallDir -and (Test-Path -LiteralPath $env:VCToolsInstallDir)) {
        $compilerDirectory = $env:VCToolsInstallDir.TrimEnd('\')
    }
    $programFiles32 = ${env:ProgramFiles(x86)}
    $vswhere = Join-Path $programFiles32 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not $compilerDirectory -and (Test-Path -LiteralPath $vswhere)) {
        $installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
        if ($installation) {
            $compilerDirectory = Get-ChildItem -LiteralPath (Join-Path $installation 'VC\Tools\MSVC') -Directory |
                Sort-Object Name -Descending | Select-Object -First 1 -ExpandProperty FullName
        }
    }
    $sdkRoot = Join-Path $programFiles32 'Windows Kits\10'
    $sdkVersion = if (Test-Path -LiteralPath (Join-Path $sdkRoot 'Include')) {
        Get-ChildItem -LiteralPath (Join-Path $sdkRoot 'Include') -Directory |
            Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'cppwinrt\winrt\Windows.Graphics.Capture.h') } |
            Sort-Object Name -Descending | Select-Object -First 1 -ExpandProperty Name
    }
    if (-not $compilerDirectory -or -not $sdkVersion) {
        throw 'Install Visual Studio C++ desktop tools and a Windows SDK with C++/WinRT headers.'
    }
    [pscustomobject]@{ Msvc = $compilerDirectory; SdkRoot = $sdkRoot; SdkVersion = $sdkVersion }
}
