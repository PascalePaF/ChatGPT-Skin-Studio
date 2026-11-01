param([switch]$SkipInstaller, [string]$PublishDirectory)
$ErrorActionPreference = 'Stop'
$root = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
& (Join-Path $PSScriptRoot 'build-icon.ps1')
& (Join-Path $PSScriptRoot 'build-native.ps1')
$project = Join-Path $root 'src\ChatGptSkinStudio\ChatGptSkinStudio.csproj'
$publish = if ($PublishDirectory) { [IO.Path]::GetFullPath($PublishDirectory) } else { Join-Path $root 'build\publish\win-x64' }
if (-not $publish.StartsWith((Join-Path $root 'build') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Publish directory must stay under the project build directory.' }
& dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false "-p:PathMap=$root=/_/src" -o $publish --nologo
if ($LASTEXITCODE -ne 0) { throw 'Windows 发布失败。' }
foreach ($name in @('README.md', 'LICENSE', 'THIRD-PARTY-NOTICES.txt', 'CHANGELOG.md', '使用说明.txt')) {
    Copy-Item -LiteralPath (Join-Path $root $name) -Destination (Join-Path $publish $name) -Force
}
[xml]$projectXml = Get-Content -LiteralPath $project -Raw
$runtimeVersion = [string]$projectXml.Project.PropertyGroup.RuntimeFrameworkVersion
$assets = Get-Content -LiteralPath (Join-Path (Split-Path -Parent $project) 'obj\project.assets.json') -Raw | ConvertFrom-Json
$packageRoots = @($assets.packageFolders.PSObject.Properties.Name)
$runtimeNotices = @(
    @{ Package = 'microsoft.netcore.app.runtime.win-x64'; File = 'LICENSE.TXT'; Target = 'dotnet\LICENSE.txt' },
    @{ Package = 'microsoft.netcore.app.runtime.win-x64'; File = 'THIRD-PARTY-NOTICES.TXT'; Target = 'dotnet\THIRD-PARTY-NOTICES.txt' },
    @{ Package = 'microsoft.windowsdesktop.app.runtime.win-x64'; File = 'LICENSE'; Target = 'windowsdesktop\LICENSE.txt' }
)
foreach ($notice in $runtimeNotices) {
    $source = $null
    foreach ($packageRoot in $packageRoots) {
        $candidate = Join-Path $packageRoot ($notice.Package + '\' + $runtimeVersion + '\' + $notice.File)
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { $source = $candidate; break }
    }
    if (-not $source) { throw ('找不到运行时许可文件：' + $notice.Package + '/' + $runtimeVersion + '/' + $notice.File) }
    $destination = Join-Path $publish ('licenses\' + $notice.Target)
    [System.IO.Directory]::CreateDirectory((Split-Path -Parent $destination)) | Out-Null
    Copy-Item -LiteralPath $source -Destination $destination -Force
}
$publishedDocs = Join-Path $publish 'docs'
[System.IO.Directory]::CreateDirectory($publishedDocs) | Out-Null
foreach ($name in @('RESEARCH.md','BUILD.md','KNOWN_ISSUES.md')) {
    Copy-Item -LiteralPath (Join-Path $root ('docs\' + $name)) -Destination (Join-Path $publishedDocs $name) -Force
}
if ($SkipInstaller) { Write-Output $publish; return }
$compiler = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
    (Get-Command ISCC.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source)
) | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) } | Select-Object -First 1
if (-not $compiler) { throw '需要已安装的 Inno Setup 6 编译器，或将 ISCC.exe 加入 PATH。' }
$signature = Get-AuthenticodeSignature -LiteralPath $compiler
if ($signature.Status -ne 'Valid') { throw '安装包编译器的签名未通过校验。' }
$dist = Join-Path $root 'dist'
[System.IO.Directory]::CreateDirectory($dist) | Out-Null
& $compiler /Q "/DPublishDir=$publish" "/DOutputDir=$dist" (Join-Path $root 'installer\setup.iss')
if ($LASTEXITCODE -ne 0) { throw '安装包生成失败。' }
$installer = Join-Path $dist 'ChatGptSkinStudio-1.1.4-Windows-x64-Setup.exe'
$hash = Get-FileHash -LiteralPath $installer -Algorithm SHA256
[System.IO.File]::WriteAllText((Join-Path $dist 'SHA256SUMS.txt'), $hash.Hash.ToLowerInvariant() + '  ' + [System.IO.Path]::GetFileName($installer) + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
Write-Output $installer
