$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot 'MagicViewWallpaper.App.exe'
$icon = Join-Path $PSScriptRoot 'Assets\AppIcon.ico'
if (-not (Test-Path -LiteralPath $exe)) { throw "Application not found: $exe" }

$shell = New-Object -ComObject WScript.Shell
$startMenu = Join-Path ([Environment]::GetFolderPath('StartMenu')) 'Programs\MagicView Wallpaper.lnk'
$desktop = Join-Path ([Environment]::GetFolderPath('Desktop')) 'MagicView Wallpaper.lnk'

foreach ($shortcutPath in @($startMenu, $desktop)) {
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $exe
    $shortcut.WorkingDirectory = $PSScriptRoot
    $shortcut.IconLocation = "$icon,0"
    $shortcut.Description = 'MagicView dynamic and static wallpaper manager'
    $shortcut.Save()
}

Add-Type -AssemblyName PresentationFramework
[System.Windows.MessageBox]::Show("Shortcuts were installed to the Start menu and Desktop.`n`nPin to taskbar: right-click the Desktop shortcut, select Show more options, then Pin to taskbar.`nPin to Start: search for MagicView Wallpaper in Start, right-click it, then select Pin to Start.", 'MagicView Wallpaper') | Out-Null
