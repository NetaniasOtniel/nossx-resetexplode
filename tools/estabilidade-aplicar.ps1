# Spec 003 — aplica E5–E8 e D3–D4 (docs/specs/003-estabilidade.md §5). Rodar ELEVADO. Não reinicia, não fecha nada.
# Uso: powershell -NoProfile -ExecutionPolicy Bypass -File tools\estabilidade-aplicar.ps1 -Log <arquivo>
param([Parameter(Mandatory = $true)][string]$Log)
$ErrorActionPreference = 'Continue'
$lines = [Collections.Generic.List[string]]::new()
function L($m) { $s = '{0:HH:mm:ss} {1}' -f (Get-Date), $m; $lines.Add($s); Write-Output $s }

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
L "admin=$admin"
if (-not $admin) { L 'PARADO: precisa de administrador'; [IO.File]::WriteAllLines($Log, $lines); exit 1 }

$DISK = '0012ee47-9041-4b5d-9b77-535fba8b1442'; $PCIE = '501a4d13-42af-4429-9fd1-a8218c268e20'; $ASPM = 'ee12f906-d277-404b-b6da-e5fa1a576df5'
$nvme = [ordered]@{
    'Primary NVMe Idle Timeout'                         = 'd639518a-e56d-4345-8af2-b9f32fb26109'
    'Secondary NVMe Idle Timeout'                       = 'd3d55efd-c1ff-424e-9dc3-441be7833010'
    'Primary NVMe Power State Transition Latency Tol.'  = 'fc95af4d-40e7-4b6d-835a-56d131dbc80e'
    'Secondary NVMe Power State Transition Latency Tol.'= 'dbc9e238-6de9-49e3-92cd-8c2b4946b472'
    'NVMe NOPPME'                                       = 'fc7372b6-ab2d-43ee-8797-15e9841f2cca'
}
function Q($sub, $set) {
    $q = & powercfg.exe /qh SCHEME_CURRENT $sub $set 2>&1
    $ac = ($q | Select-String 'Alternadas Atuais|Current AC Power Setting Index' | Select-Object -First 1).Line
    $dc = ($q | Select-String 'nuas Atuais|Current DC Power Setting Index' | Select-Object -First 1).Line
    '{0}/{1}' -f $(if ($ac) { ($ac -split ':')[-1].Trim() } else { '?' }), $(if ($dc) { ($dc -split ':')[-1].Trim() } else { '?' })
}

L ('plano ativo: ' + (& powercfg.exe /getactivescheme))

# E8 — expor os ocultos (só visibilidade em Opções de energia)
foreach ($g in $nvme.Values) { & powercfg.exe -attributes $DISK $g -ATTRIB_HIDE | Out-Null }
& powercfg.exe -attributes $PCIE $ASPM -ATTRIB_HIDE | Out-Null
L 'E8: ajustes de NVMe e ASPM expostos (-ATTRIB_HIDE)'

# E5–E7 — tolerâncias 0 e NOPPME 0 (CA e CC). E1–E4 já estão como pedido: só conferidos.
foreach ($g in @($nvme['Primary NVMe Power State Transition Latency Tol.'], $nvme['Secondary NVMe Power State Transition Latency Tol.'], $nvme['NVMe NOPPME'])) {
    & powercfg.exe /setacvalueindex SCHEME_CURRENT $DISK $g 0
    & powercfg.exe /setdcvalueindex SCHEME_CURRENT $DISK $g 0
}
& powercfg.exe /setactive SCHEME_CURRENT
L 'E5–E7 aplicados e plano reativado'

# D3–D4 — despejo dedicado no D: (BitLocker ligado; interno; não é o disco do C:)
$cc = 'HKLM:\SYSTEM\CurrentControlSet\Control\CrashControl'
$vol = Get-Volume -DriveLetter D -ErrorAction SilentlyContinue
$part = Get-Partition -DriveLetter D -ErrorAction SilentlyContinue
$sys = (Get-Partition -DriveLetter C).DiskNumber
if ($vol -and $vol.DriveType -eq 'Fixed' -and $vol.FileSystem -eq 'NTFS' -and $part.DiskNumber -ne $sys -and $vol.SizeRemaining -gt 4GB) {
    New-ItemProperty -Path $cc -Name DedicatedDumpFile -PropertyType String -Value 'D:\dedicateddump.sys' -Force | Out-Null
    New-ItemProperty -Path $cc -Name DumpFileSize -PropertyType DWord -Value 1024 -Force | Out-Null
    L 'D3–D4: DedicatedDumpFile=D:\dedicateddump.sys, DumpFileSize=1024 MB (vale após reiniciar)'
} else { L "D3–D4 NÃO aplicados: D: não atende (tipo=$($vol.DriveType) fs=$($vol.FileSystem) disco=$($part.DiskNumber) sistema=$sys)" }

# Verificação (sem reiniciar)
L ('ASPM (E1)                 = ' + (Q $PCIE $ASPM))
L ('DISKIDLE (E2)             = ' + (Q $DISK '6738e2c4-e8a5-4a42-b16a-e040e769756e'))
foreach ($k in $nvme.Keys) { L ("{0,-50} = {1}" -f $k, (Q $DISK $nvme[$k])) }
$p = Get-ItemProperty $cc
L ("CrashControl: CrashDumpEnabled={0} MinidumpDir={1} AutoReboot={2} Overwrite={3} DedicatedDumpFile={4} DumpFileSize={5}" -f $p.CrashDumpEnabled, $p.MinidumpDir, $p.AutoReboot, $p.Overwrite, $p.DedicatedDumpFile, $p.DumpFileSize)
L ('Pagefile: AutomaticManaged=' + (Get-CimInstance Win32_ComputerSystem).AutomaticManagedPagefile + ' · ' + ((Get-CimInstance Win32_PageFileUsage | ForEach-Object { '{0} {1} MB' -f $_.Name, $_.AllocatedBaseSize }) -join ', '))
L 'FIM'
[IO.File]::WriteAllLines($Log, $lines, [Text.UTF8Encoding]::new($false))
