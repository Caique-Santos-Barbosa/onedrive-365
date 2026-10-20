# Compila as extensões do Explorer (x64) com o MSVC e copia para src\assets:
#   OneDrive365Shell.dll  selos de status (nuvem / no PC / sincronizando)
#   OneDrive365Menu.dll   menu "OneDrive 365" do Windows 11 (carregado pelo pacote sparse)
$ErrorActionPreference = 'Stop'
$vs = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'MSVC (Build Tools com C++) não encontrado' }
$out = Join-Path $PSScriptRoot 'out'
New-Item -ItemType Directory -Force $out | Out-Null
foreach ($dll in @(@{ Src = 'overlay'; Name = 'OneDrive365Shell' }, @{ Src = 'menu'; Name = 'OneDrive365Menu' })) {
    $cmd = "`"$vs\VC\Auxiliary\Build\vcvars64.bat`" >nul && cl /nologo /LD /O2 /EHsc /W4 /WX /std:c++17 /DUNICODE /D_UNICODE " +
           "`"$PSScriptRoot\$($dll.Src).cpp`" /Fo`"$out\\`" /Fe`"$out\$($dll.Name).dll`" " +
           "/link /DEF:`"$PSScriptRoot\$($dll.Src).def`" ole32.lib advapi32.lib user32.lib shell32.lib shlwapi.lib"
    cmd /c $cmd
    if ($LASTEXITCODE) { throw "Falha ao compilar $($dll.Name)" }
    Copy-Item "$out\$($dll.Name).dll" (Join-Path $PSScriptRoot '..\src\assets\') -Force
    "$($dll.Name).dll ok"
}
