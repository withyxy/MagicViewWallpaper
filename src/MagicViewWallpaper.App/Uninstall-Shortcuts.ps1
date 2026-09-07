$startMenu = Join-Path ([Environment]::GetFolderPath('StartMenu')) 'Programs\MagicView Wallpaper.lnk'
$desktop = Join-Path ([Environment]::GetFolderPath('Desktop')) 'MagicView Wallpaper.lnk'
Remove-Item -LiteralPath $startMenu -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $desktop -Force -ErrorAction SilentlyContinue
Add-Type -AssemblyName PresentationFramework
[System.Windows.MessageBox]::Show('Start menu and Desktop shortcuts were removed.', 'MagicView Wallpaper') | Out-Null
