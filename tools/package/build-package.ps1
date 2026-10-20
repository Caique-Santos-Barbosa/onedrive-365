# Gera src\assets\OneDrive365.msix (pacote sparse assinado) + menu.cer (certificado público) + menu-version.txt.
# O certificado de assinatura fica no repositório de certificados do usuário (CurrentUser\My), nunca no git.
$ErrorActionPreference = 'Stop'
$root = Join-Path $PSScriptRoot '..\..'
$assets = Join-Path $root 'src\assets'
$sdk = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin\*\x64' -Directory | Sort-Object Name | Select-Object -Last 1
$makeappx = Join-Path $sdk.FullName 'makeappx.exe'
$signtool = Join-Path $sdk.FullName 'signtool.exe'
$subject = 'CN=HDT Energy OneDrive 365'

# Versão sempre crescente: o Windows só troca o pacote registrado se a versão mudar
$now = Get-Date
$version = "1.3.$((New-TimeSpan -Start '2026-01-01' -End $now).Days).$([int]$now.TimeOfDay.TotalMinutes)"

$work = Join-Path ([IO.Path]::GetTempPath()) "od365-pkg-$PID"
New-Item -ItemType Directory -Force $work | Out-Null
(Get-Content (Join-Path $PSScriptRoot 'AppxManifest.xml') -Raw).Replace('$VERSION$', $version) |
    Set-Content (Join-Path $work 'AppxManifest.xml') -Encoding utf8

# Logo do pacote: nuvem do Windows (imageres.dll, -1043) em 150 px
Add-Type -AssemblyName System.Drawing
Add-Type -Namespace OD365 -Name Ico -MemberDefinition '[DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern uint PrivateExtractIcons(string f,int i,int cx,int cy,System.IntPtr[] h,int[] ids,uint n,uint fl);'
$h = New-Object IntPtr[] 1
[void][OD365.Ico]::PrivateExtractIcons("$env:WINDIR\System32\imageres.dll", -1043, 150, 150, $h, $null, 1, 0)
[Drawing.Icon]::FromHandle($h[0]).ToBitmap().Save((Join-Path $work 'logo.png'), [Drawing.Imaging.ImageFormat]::Png)

$msix = Join-Path $assets 'OneDrive365.msix'
& $makeappx pack /d $work /p $msix /nv /o | Out-Null
if ($LASTEXITCODE) { throw 'makeappx falhou' }

$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $subject -and $_.NotAfter -gt (Get-Date).AddDays(30) } | Select-Object -First 1
if (-not $cert) {
    $cert = New-SelfSignedCertificate -Type Custom -Subject $subject -KeyUsage DigitalSignature -FriendlyName 'OneDrive 365 (pacote do menu)' `
        -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(5) `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
}
& $signtool sign /fd SHA256 /sha1 $cert.Thumbprint /s My $msix | Out-Null
if ($LASTEXITCODE) { throw 'signtool falhou' }
Export-Certificate -Cert $cert -FilePath (Join-Path $assets 'menu.cer') | Out-Null
Set-Content (Join-Path $assets 'menu-version.txt') $version -NoNewline
Remove-Item -Recurse -Force $work
"pacote $version assinado por $subject"
