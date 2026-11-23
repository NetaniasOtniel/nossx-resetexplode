# Etapa 5 — diagnóstico cruzado em repouso. Rodar ELEVADO (um único UAC). Só lê; não fecha nenhum programa.
# Uso: powershell -NoProfile -ExecutionPolicy Bypass -File tools\diagnostico.ps1 -Saida <pasta>
param(
    [Parameter(Mandatory = $true)][string]$Saida,
    [int]$Amostras = 12,
    [int]$Intervalo = 5,
    [int]$AquecimentoHwinfo = 45
)
$ErrorActionPreference = 'Continue'
$repo = Split-Path -Parent $PSScriptRoot
New-Item -ItemType Directory -Force -Path $Saida | Out-Null
$log = Join-Path $Saida 'script.txt'
function Log($m) { $l = '{0:HH:mm:ss} {1}' -f (Get-Date), $m; $l | Tee-Object -FilePath $log -Append }

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
Log "admin=$admin saida=$Saida"
if (-not $admin) { Log 'PARADO: precisa de administrador'; exit 1 }

# Regra do TPM desta máquina: só o log do vigia e Get-Tpm. Conta os eventos antes e depois para provar que a coleta não armou Clear.
$vigia = 'C:\ProgramData\VigiaTPM\eventos.jsonl'
$tpmAntes = if (Test-Path $vigia) { (Get-Content $vigia | Measure-Object -Line).Lines } else { -1 }
Log "VigiaTPM eventos antes: $tpmAntes"

# HWiNFO: modo só sensores + memória compartilhada ligados no INI (sem clicar). Não mexe em mais nada do INI.
$hwDir = 'C:\Program Files\HWiNFO64'
$hwExe = Join-Path $hwDir 'HWiNFO64.EXE'
if (-not (Test-Path $hwExe)) { Log "PARADO: $hwExe não existe"; exit 2 }
$ini = Join-Path $hwDir 'HWiNFO64.INI'
function Test-HwShm {
    try { $m = [System.IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting('Global\HWiNFO_SENS_SM2', [System.IO.MemoryMappedFiles.MemoryMappedFileRights]::Read); $m.Dispose(); $true } catch { $false }
}
$iniOriginal = $null
$hw = Get-Process -Name HWiNFO64 -ErrorAction SilentlyContinue | Select-Object -First 1
if ($hw) {
    # Já aberto (pelo dono ou pelo instalador): não fecha e não mexe no INI (só vale na próxima abertura).
    Log "HWiNFO64 já aberto (PID $($hw.Id)): reaproveitado; INI intocado"
} else {
    # Aberto por este script: liga só sensores + memória compartilhada no INI e DEVOLVE o INI original no fim.
    $iniOriginal = if (Test-Path $ini) { [IO.File]::ReadAllBytes($ini) } else { [byte[]]@() }
    $lst = [System.Collections.Generic.List[string]]::new()
    if (Test-Path $ini) { foreach ($l in (Get-Content $ini)) { $lst.Add($l) } }
    $si = $lst.FindIndex([Predicate[string]] { param($l) $l -match '^\[Settings\]' })
    if ($si -lt 0) { $lst.Insert(0, '[Settings]'); $si = 0 }
    foreach ($kv in @('SensorsOnly=1', 'SensorsSM=1')) {
        $k = $kv.Split('=')[0]
        $ix = $lst.FindIndex([Predicate[string]] { param($l) $l -match "^$k=" })
        if ($ix -ge 0) { $lst[$ix] = $kv } else { $lst.Insert($si + 1, $kv) }
    }
    Set-Content -Path $ini -Value $lst -Encoding ASCII
    Log "HWiNFO64.INI (temporário): $((Get-Content $ini) -join ' | ')"
    $hw = Start-Process -FilePath $hwExe -WorkingDirectory $hwDir -PassThru
    Log "HWiNFO64 aberto (PID $($hw.Id))"
}
# Espera a memória compartilhada (no HWiNFO já aberto, o dono liga em Sensores > Configurações > "Shared Memory Support").
$limite = (Get-Date).AddMinutes(10)
while (-not (Test-HwShm) -and (Get-Date) -lt $limite) { Start-Sleep -Seconds 5 }
if (Test-HwShm) { Log "memória compartilhada do HWiNFO disponível; aquecendo $AquecimentoHwinfo s"; Start-Sleep -Seconds $AquecimentoHwinfo }
else { Log 'AVISO: memória compartilhada do HWiNFO não apareceu em 10 min; coleta segue só com o ResetExplode' }
if ($null -ne $iniOriginal) {
    if ($iniOriginal.Length -gt 0) { [IO.File]::WriteAllBytes($ini, $iniOriginal) } else { Remove-Item $ini -ErrorAction SilentlyContinue }
    Log 'HWiNFO64.INI devolvido ao original (o HWiNFO já leu a configuração na abertura)'
}

# Coleta lado a lado: as mesmas classes da tela do ResetExplode + memória compartilhada do HWiNFO.
$diag = Join-Path $repo 'tools\Diagnostico\bin\Release\net8.0-windows\Diagnostico.exe'
Log "Diagnostico.exe: $diag"
& $diag $Saida $Amostras $Intervalo 2>&1 | Tee-Object -FilePath (Join-Path $Saida 'diagnostico-console.txt') | Out-Null
Log "Diagnostico terminou (código $LASTEXITCODE)"

# Referência de S.M.A.R.T. independente: smartctl (instalado em 18/10) e os contadores do próprio Windows.
$smart = 'C:\Program Files\smartmontools\bin\smartctl.exe'
if (Test-Path $smart) {
    $scan = & $smart --scan -j | ConvertFrom-Json
    foreach ($d in $scan.devices) {
        $nome = ($d.name -replace '[\\/:]', '_')
        & $smart -x -j $d.name | Set-Content -Path (Join-Path $Saida "smartctl-$nome.json") -Encoding UTF8
        Log "smartctl -x $($d.name) ($($d.type))"
    }
} else { Log 'smartctl ausente' }
Get-PhysicalDisk | ForEach-Object {
    $r = $_ | Get-StorageReliabilityCounter
    [pscustomobject]@{ Disco = $_.FriendlyName; Midia = "$($_.MediaType)"; Barramento = "$($_.BusType)"; Saude = "$($_.HealthStatus)"; Status = "$($_.OperationalStatus)"
        TempC = $r.Temperature; TempMaxC = $r.TemperatureMax; Desgaste = $r.Wear; ErrosLeituraNaoCorrigidos = $r.ReadErrorsUncorrected
        ErrosEscritaNaoCorrigidos = $r.WriteErrorsUncorrected; HorasLigado = $r.PowerOnHours; LatenciaLeituraMaxMs = $r.ReadLatencyMax; LatenciaEscritaMaxMs = $r.WriteLatencyMax }
} | ConvertTo-Json | Set-Content -Path (Join-Path $Saida 'windows-storage-reliability.json') -Encoding UTF8
Log 'Get-StorageReliabilityCounter gravado'

$tpmDepois = if (Test-Path $vigia) { (Get-Content $vigia | Measure-Object -Line).Lines } else { -1 }
Log "VigiaTPM eventos depois: $tpmDepois (diferença $($tpmDepois - $tpmAntes))"
if ($tpmDepois -gt $tpmAntes) { Get-Content $vigia -Tail ($tpmDepois - $tpmAntes) | Set-Content (Join-Path $Saida 'vigiatpm-novos.jsonl') }

# Abre o ResetExplode compilado do código-fonte para conferência visual (fica aberto, como o HWiNFO).
$re = Join-Path $repo 'bin\Release\net8.0-windows\ResetExplode.exe'
if (Test-Path $re) { $p = Start-Process -FilePath $re -PassThru; Log "ResetExplode aberto (PID $($p.Id))" }
Log 'FIM'
