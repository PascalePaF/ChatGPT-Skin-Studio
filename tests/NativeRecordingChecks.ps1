param([Parameter(Mandatory=$true)][string]$Publish,[Parameter(Mandatory=$true)][string]$Evidence)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing.Common
$Publish=[IO.Path]::GetFullPath($Publish);$Evidence=[IO.Path]::GetFullPath($Evidence)
$folder=Join-Path $Evidence ('encoder-'+[Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($folder) | Out-Null
$pattern=Join-Path $folder 'quadrants.png';$movie=Join-Path $folder 'wallpaper.mp4';$requestFile=Join-Path $folder 'request.json';$statusFile=Join-Path $folder 'status.json'
$image=[Drawing.Bitmap]::new(2560,1440);$paint=[Drawing.Graphics]::FromImage($image)
$paint.FillRectangle([Drawing.Brushes]::Red,0,0,1280,720);$paint.FillRectangle([Drawing.Brushes]::Lime,1280,0,1280,720)
$paint.FillRectangle([Drawing.Brushes]::Blue,0,720,1280,720);$paint.FillRectangle([Drawing.Brushes]::Yellow,1280,720,1280,720)
$image.Save($pattern,[Drawing.Imaging.ImageFormat]::Png);$paint.Dispose();$image.Dispose()
$stopName='Local\CGSS-EncoderCheck-'+[Guid]::NewGuid().ToString('N')
$signal=[Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset,$stopName)
$request=@{mode='record';sourceKind='image';sourcePath=$pattern;recordPath=$movie;recordSeconds=2;maxSeconds=20;parentPid=$PID;parentBirth=(Get-Process -Id $PID).StartTime.ToFileTimeUtc().ToString();stopEvent=$stopName;statusPath=$statusFile;fit='contain'}
$request | ConvertTo-Json | Set-Content -LiteralPath $requestFile -Encoding utf8
$helper=$null
try {
 $helper=Start-Process -FilePath (Join-Path $Publish 'native\SkinRenderer.exe') -ArgumentList ('"'+$requestFile+'"') -WindowStyle Hidden -PassThru
 if(-not $helper.WaitForExit(25000)){throw '编码器未在期限内结束。'}
 $state=Get-Content -LiteralPath $statusFile -Raw | ConvertFrom-Json
 if($helper.ExitCode -ne 0 -or $state.state -ne 'completed' -or $state.frames -lt 15){throw ('编码失败：'+($state | ConvertTo-Json -Compress))}
 $report=Join-Path $folder 'decode.json'
 $probe=Start-Process -FilePath (Join-Path $Publish 'ChatGptSkinStudio.exe') -ArgumentList @('--inspect-media',('"'+$movie+'"'),('"'+$report+'"')) -WindowStyle Hidden -PassThru -Wait
 if($probe.ExitCode -ne 0){throw '生成视频不能重新解码。'}
 $decoded=Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
 if($decoded.State.SourceWidth -ne 2560 -or $decoded.State.SourceHeight -ne 1440 -or [Math]::Abs($decoded.State.Duration-2) -gt .2){throw '视频尺寸或时长错误。'}
 $frame=[Drawing.Bitmap]::new((Join-Path $decoded.Session 'frame.png'))
 try {
  $samples=@(@(640,360,255,0,0),@(1920,360,0,255,0),@(640,1080,0,0,255),@(1920,1080,255,255,0))
  $errors=@()
  foreach($sample in $samples){$color=$frame.GetPixel($sample[0],$sample[1]);$error=[Math]::Max([Math]::Abs($color.R-$sample[2]),[Math]::Max([Math]::Abs($color.G-$sample[3]),[Math]::Abs($color.B-$sample[4])));$errors+=$error;if($error -gt 35){throw 'H.264颜色或上下方向错误。'}}
 } finally {$frame.Dispose()}
 [ordered]@{Result='pass';Source='Generated red/green/blue/yellow quadrants';Width=2560;Height=1440;Duration=$decoded.State.Duration;Frames=$state.frames;MaximumColorChannelError=($errors | Measure-Object -Maximum).Maximum;Video=$movie;DecodeReport=$report} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Evidence 'encoder-evidence.json') -Encoding utf8
 Write-Output 'PASS native H.264 encode, 2560x1440, duration, independent decode, RGB order and vertical orientation.'
} finally {
 $signal.Set() | Out-Null
 if($helper){if(-not $helper.HasExited -and -not $helper.WaitForExit(3000)){$helper.Kill()};$helper.Dispose()}
 $signal.Dispose()
}
