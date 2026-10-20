# Gera dist\OneDrive365.exe (autocontido: .NET + rclone com patch + WinFsp + extensão de selos)
# Requer: .NET 8 SDK, Go 1.26+, Git, Visual Studio Build Tools (C++).
$ErrorActionPreference = 'Stop'
$rcloneVersion = 'v1.75.1'
$winfspUrl = 'https://github.com/winfsp/winfsp/releases/download/v2.1/winfsp-2.1.25156.msi'
$root = $PSScriptRoot
$assets = Join-Path $root 'src\assets'
New-Item -ItemType Directory -Force $assets | Out-Null

# rclone com o patch do OneDrive 365 (atributos para ícones de pasta + ChangeNotify no union)
if (-not (Test-Path "$assets\rclone.exe")) {
    $src = Join-Path ([IO.Path]::GetTempPath()) "rclone-$rcloneVersion-src"
    if (-not (Test-Path $src)) { git clone -q --depth 1 --branch $rcloneVersion https://github.com/rclone/rclone $src }
    git -C $src apply --whitespace=nowarn (Join-Path $root 'tools\rclone-patch\onedrive365.patch')
    Push-Location $src
    $env:CGO_ENABLED = '0'
    go build -tags cmount -trimpath -ldflags "-s -w -X github.com/rclone/rclone/fs.Version=$rcloneVersion-onedrive365" -o "$assets\rclone.exe" .
    Pop-Location
    if ($LASTEXITCODE) { throw 'Falha ao compilar o rclone' }
}
if (-not (Test-Path "$assets\winfsp.msi")) {
    Invoke-WebRequest $winfspUrl -OutFile "$assets\winfsp.msi"
    if ((Get-AuthenticodeSignature "$assets\winfsp.msi").Status -ne 'Valid') { throw 'Assinatura do WinFsp inválida' }
}
if (-not (Test-Path "$assets\OneDrive365Shell.dll") -or -not (Test-Path "$assets\OneDrive365Menu.dll")) { & "$root\shell\build.ps1" }
& "$root\tools\package\build-package.ps1"   # versão nova do pacote do menu a cada build
# ícone do exe = o do OneDrive.exe instalado; sem OneDrive, o desenho próprio
if (-not (& "$root\tools\extract-onedrive-icon.ps1") -and -not (Test-Path "$assets\app.ico")) { & "$root\tools\make-icon.ps1" }
if (-not (Test-Path "$assets\drive.ico")) { & "$root\tools\make-drive-icon.ps1" }
if (-not (Test-Path "$assets\overlay-sync.ico")) { & "$root\tools\make-overlay-icons.ps1" }
if (-not (Test-Path "$assets\menuicons\share.ico")) { & "$root\tools\make-menu-icons.ps1" }

dotnet publish "$root\src" -c Release -o "$root\dist" -nologo -v q
if ($LASTEXITCODE) { throw 'Falha no publish' }
Get-Item "$root\dist\OneDrive365.exe" | Select-Object Name, @{n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }


