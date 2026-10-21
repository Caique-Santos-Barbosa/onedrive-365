# Uso: pwsh tools\tests\regressao.ps1  (com o app instalado e logado; o teste de cor cria e apaga a própria pasta _icone_teste)
# Bateria de regressão passiva do OneDrive 365 (não mexe em mouse/teclado; desfaz o que muda).
$ErrorActionPreference = 'Continue'
$app = "$env:LOCALAPPDATA\OneDrive 365"; $log = "$app\logs\app.log"
$out = Join-Path ([IO.Path]::GetTempPath()) "onedrive365-regressao"
New-Item -ItemType Directory -Force $out | Out-Null
$results = [System.Collections.ArrayList]::new()
function Check($name, [scriptblock]$test) {
  try { $r = & $test; $ok = [bool]$r[0]; $detail = $r[1] } catch { $ok = $false; $detail = $_.Exception.Message }
  [void]$results.Add([pscustomobject]@{ Teste = $name; OK = $(if ($ok) { 'ok' } else { 'FALHOU' }); Detalhe = $detail })
}
function Pipe($m) { $p = New-Object IO.Pipes.NamedPipeClientStream('.', "HDTDrive.$env:USERNAME", [IO.Pipes.PipeDirection]::Out); $p.Connect(3000); $w = New-Object IO.StreamWriter($p); $w.WriteLine($m); $w.Flush(); $p.Dispose() }
function LogSince($n) { (Get-Content $log)[$n..((Get-Content $log).Count - 1)] -join "`n" }

Check 'app rodando' { $p = Get-Process OneDrive365 -ErrorAction SilentlyContinue; @(([bool]$p), "pid $($p.Id)") }
Check 'unidade M: montada' { $ok = Test-Path 'M:\Meu OneDrive'; @($ok, ((Get-ChildItem M:\ | ForEach-Object Name) -join ', ')) }
Check 'listagem do Meu OneDrive' { $n = (Get-ChildItem 'M:\Meu OneDrive' -ErrorAction Stop | Measure-Object).Count; @(($n -gt 0), "$n itens") }
Check 'SharePoint acessível' { $ok = Test-Path 'M:\SharePoint'; @($ok, ((Get-ChildItem 'M:\SharePoint' -ErrorAction Stop | ForEach-Object Name) -join ', ')) }
Check 'reserva de espaço no cache' { $c = (Get-CimInstance Win32_Process -Filter "Name='rclone.exe'" | Where-Object { $_.CommandLine -match ' mount ' }).CommandLine; @(($c -match 'vfs-cache-min-free-space 2G'), 'flag presente na montagem') }
Check 'leitura de arquivo pequeno' {
  $f = Get-ChildItem 'M:\Meu OneDrive' -File -Recurse -Depth 1 -ErrorAction SilentlyContinue | Where-Object { $_.Length -gt 0 -and $_.Length -lt 200KB } | Select-Object -First 1
  if (-not $f) { return @(($true), 'sem arquivo pequeno na raiz (pulado)') }
  $sw = [Diagnostics.Stopwatch]::StartNew(); $b = [IO.File]::ReadAllBytes($f.FullName); @(($b.Length -eq $f.Length), "$($f.Name): $($b.Length) bytes em $($sw.ElapsedMilliseconds) ms")
}
Check 'selos de status completos' { $bad = Get-ChildItem "$app\shell" -Directory | Where-Object { (Get-ChildItem $_.FullName -Filter 'overlay-*.ico').Count -lt 3 }; @(-not $bad, "pastas: $((Get-ChildItem "$app\shell" -Directory).Count), sem ícone: $($bad.Count)") }
Check 'janela principal (todas as páginas)' {
  $n = (Get-Content $log).Count; $fail = @()
  foreach ($pg in 'home', 'activity', 'notifications', 'folders', 'sharepoint', 'preferences', 'about') {
    $png = "$out\pg-$pg.png"; Remove-Item $png -ErrorAction SilentlyContinue; Pipe "screenshot:$pg|$png"
    for ($i = 0; $i -lt 60 -and -not ((Test-Path $png) -and (Get-Item $png).Length -gt 10000); $i++) { Start-Sleep -Milliseconds 250 }   # o app cria o arquivo e depois grava a imagem
    if (-not (Test-Path $png) -or (Get-Item $png).Length -lt 10000) { $fail += $pg }
  }
  @(($fail.Count -eq 0), $(if ($fail) { "falharam: $($fail -join ',')" } else { '7 páginas renderizadas' }))
}
Check 'painel da bandeja' { $png = "$out\flyout.png"; Remove-Item $png -ErrorAction SilentlyContinue; Pipe "flyout-shot:$png"; for ($i = 0; $i -lt 40 -and -not ((Test-Path $png) -and (Get-Item $png).Length -gt 5000); $i++) { Start-Sleep -Milliseconds 250 }; @(((Test-Path $png) -and (Get-Item $png).Length -gt 5000), 'renderizado') }
Check 'menu do Explorer (DLL) entrega na pipe' {
  $n = (Get-Content $log).Count
  $exe = (Join-Path $PSScriptRoot '..\..\shell\out\test_menu.exe'); $dll = (Join-Path $PSScriptRoot '..\..\shell\out\OneDrive365Menu.dll')
  if (-not (Test-Path $exe)) { return @(($false), 'test_menu.exe ausente') }
  & $exe $dll 'M:\Meu OneDrive' | Out-Null
  @(($LASTEXITCODE -eq 0), 'subitens listados')
}
Check 'cor de pasta nova aparece (amarela -> cor)' {
  # A pasta de teste é criada aqui e apagada no fim pela nuvem (rclone), sem passar pelo M: e sem o aviso de exclusão
  New-Item -ItemType Directory -Force 'M:\Meu OneDrive\_icone_teste\cor' | Out-Null
  $n = (Get-Content $log).Count; Pipe 'setcolor:green|M:\Meu OneDrive\_icone_teste\cor'; Start-Sleep 2
  $ok = (LogSince $n) -match 'Cor da pasta .*cor: green em (\d+) ms'; $ms = $matches[1]
  Pipe 'setcolor:yellow|M:\Meu OneDrive\_icone_teste\cor'; Start-Sleep 1
  & "$app\bin\rclone.exe" --config "$app\rclone.conf" purge 'onedrive:_icone_teste' 2>$null
  @(($ok -and (Test-Path "$app\overlay") -and -not ((LogSince $n) -match 'remontando|rclone mount parado')), "aplicada em $ms ms, sem remontar")
}
Check 'erros no log desde o início' { $errs = Select-String $log -Pattern ' ERRO ' | Where-Object { $_.Line -match (Get-Date -Format 'yyyy-MM-dd') } | Select-Object -Last 5; @(($true), "$(@($errs).Count) erro(s) hoje (informativo)") }
$results | Format-Table -AutoSize | Out-String -Width 220
