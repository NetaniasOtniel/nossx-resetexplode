# Diagnóstico do PC (NOSSX-DESK) — ResetExplode v0.16 (fork) contra referências independentes

| Campo | Valor |
|---|---|
| Data | 23/11/2026, 08:43–09:56 |
| Build | `dotnet build -c Release` do fork (`main` em `ead9e59`), SDK 8.0.428, runtime 8.0.34; nada das Releases |
| Coleta principal | `tools\diagnostico.ps1` elevado (um UAC) → `diag\20261123-083338` (12 amostras × 5 s, mesmas classes da tela: `SystemInfoCollector`, `GpuInfoCollector`, `SensorMonitor`, `DiskHealth`) |
| Coleta da GPU | `Diagnostico.dll` sem elevação + `nvidia-smi -l 5` **na mesma janela** → `diag\20261123-095528-gpu-sem-elevacao` |
| Referências | `smartctl` 7.5 (`-x -j`), `Get-PhysicalDisk`/`Get-StorageReliabilityCounter`, `nvidia-smi` (driver 621.24), WMI `Win32_ComputerSystem.HypervisorPresent`, SPD dos módulos contra a tabela JEDEC DDR4, configuração `B360M_H` (mesmo chip IT8686E) do próprio LHM |
| HWiNFO64 8.60 | ⏳ **pendente**: aberto (PID 24300), mas com *Shared Memory Support* desligado; a coleta esperou 10 min e uma vigia segue esperando. As colunas "HWiNFO" abaixo serão preenchidas pela próxima coleta |
| Não rodado | stress test, RESET OC, limpeza de COM, remoção de drivers, Otimizar (spec 000 §12) |
| TPM | VigiaTPM 430 eventos antes e depois da coleta elevada (nenhum Clear armado); nada de `Win32_Tpm`/`Win32_DeviceGuard` |

Os dados brutos ficam em `diag\` (fora do git: têm números de série).

## 1. Especificação real

| Item | Valor | Fonte |
|---|---|---|
| Placa-mãe | Gigabyte **B360M AORUS Gaming 3-CF**, BIOS F17a (11/06/2025), Super I/O **ITE IT8686E** | SMBIOS, LHM |
| CPU | Intel Core **i9-9900KF** (Coffee Lake-R, 8C/16T, base 3,6 GHz, sem iGPU), barramento 100 MHz | WMI, LHM |
| RAM | 4 × 8 GB DDR4 single rank, 2 canais (A: 2× Micron 8ATF1G64AZ-3G2J1; B: 2× SK Hynix HMA81GU6CJR8N-XN), **2666 MT/s em uso**, 1,20 V | WMI, LHM |
| RAM — SPD | os 4 módulos são **DDR4-3200 JEDEC** (tCKmin 0,625 ns; tAA = tRCD = tRP = 13,75 ns → 22-22-22 a 3200; 19-19-19 a 2666) | SPD via LHM |
| GPU | **RTX 3070** Zotac (GA104, 10DE:2484 · 19DA:2617), 8 GB GDDR6, VBIOS 94.04.3a.40.67, driver 621.24 (WHQL), PCIe 3.0 x16, ReBAR desativado | NVAPI/NVML, `nvidia-smi` |
| Disco 1 | "NVME SSD 1TB" fw 2008XKR, NVMe, 1.024,2 GB | `smartctl`, `Get-PhysicalDisk` |
| Disco 2 | ADATA IM2P33F3A 256 GB fw 92004171, NVMe, 256,1 GB | idem |
| SO | Windows 11 Pro build 26300, **hypervisor ativo** (VBS/Hyper-V) | WMI |

Observação de uso: durante a coleta a RAM estava em **96–99 %** (31,6 de 31,9 GB) e a memória virtual em 43 de 61 GB. Não é defeito de leitura, mas explica parte do consumo "em repouso" (CPU a 13–52 %, GPU a 16–53 %).

O limite de 2666 MT/s é do chipset B360 com CPU de 9ª geração (memória acima de 2666 só em Z370/Z390); os módulos aguentam 3200, mas nesta placa não há o que ligar.

## 2. Saúde dos discos (portão b: **não acionado**)

| | NVME SSD 1TB | ADATA 256 GB |
|---|---|---|
| S.M.A.R.T. geral (`smartctl`) | **PASSED**, critical_warning 0 | **PASSED**, critical_warning 0 |
| Desgaste (Percentage Used) | 7 % | 0 % |
| Reserva disponível / limite | 100 % / 10 % | 100 % / 50 % |
| Erros de mídia / entradas no log | 0 / 0 | 0 / 0 |
| Tempo acima de warning / crítico | 0 / 0 min | 0 / 0 min |
| Horas ligado / ciclos / desligamentos inseguros | 17.679 / 6.806 / 348 | 16.738 / 6.637 / 299 |
| Gravado (`smartctl`, decimal) | 105,6 TB | 4,97 TB |
| Temperatura (repouso) | 45 °C (limites 83/85 °C) | 49 °C (limites 83/85 °C) |
| Windows (`Get-PhysicalDisk`) | Healthy / OK | Healthy / OK |

Os 348 desligamentos inseguros no NVMe de 1 TB (e 299 no ADATA) indicam muitas quedas de energia ou travamentos ao longo da vida do PC, mas não deixaram erro de mídia.

## 3. Temperaturas em repouso (portão b: **não acionado**)

| Componente | Leitura | Avaliação |
|---|---|---|
| CPU Package | 46–50 °C (núcleos 41–48 °C), 51–73 W com carga de fundo 13–52 % | normal para 9900KF com carga leve; TjMax 100 °C, distância mínima 49 °C |
| GPU núcleo / hot spot | 54 °C / 66–67 °C, ventoinhas a 0 RPM (modo silencioso) | normal: modo de ventoinha parada em repouso (0 RPM), padrão das RTX 30; não medi o limiar em que elas ligam |
| Placa (IT8686E) | 39–52 °C nos 6 canais | normal (nomes dos canais: ver D1) |
| NVMe | 45 °C e 49 °C | normal |

## 4. ResetExplode × referência

Legenda: ✅ bate · ⚠️ diverge · ❌ ausente ou impossível · ⏳ falta a leitura do HWiNFO.

### 4.1 Sistema e RAM

| Item | ResetExplode mostra | Referência | | Id |
|---|---|---|---|---|
| Virtualização habilitada | **False** | `HypervisorPresent = True`: com hypervisor ativo o Windows zera `VirtualizationFirmwareEnabled`; VT-x está ligado | ⚠️ | D6 |
| Velocidade nominal da RAM | 2666 MT/s ("reportada pelo módulo") | SPD: DDR4-3200 (tCKmin 0,625 ns); o 2666 é o que a BIOS escreve no SMBIOS | ⚠️ | D3 |
| Timings | "O Windows não expõe" | o LHM do próprio app já lê o SPD: tAA/tRCD/tRP 13,75 ns, tRAS 32 ns, tRC 45,75 ns, tRFC1 350 ns (iguais nos 4 módulos) | ❌ | D2 |
| Timings em uso (registradores do controlador) | — | ⏳ HWiNFO "Memory Timings" | ⏳ | D2 |
| Perfil EXPO/XMP | "Sem como estimar" | correto: módulos JEDEC sem XMP, e o B360 limita a 2666 | ✅ | — |
| Velocidade em uso, canais, rank, tensão | 2666, 2 canais, single, 1,20 V | WMI/SPD | ✅ | — |
| Placa-mãe "Versão" | "Default string" | texto de preenchimento da Gigabyte no SMBIOS | ⚠️ (cosmético) | D11 |
| Soquete | "U3E1" | designador da Gigabyte; o soquete é LGA1151 | ⚠️ (cosmético) | D11 |

### 4.2 CPU

| Item | ResetExplode | Referência | | Id |
|---|---|---|---|---|
| Temperaturas por núcleo / Package | 41–50 °C | ⏳ HWiNFO | ⏳ | — |
| CPU Package / Cores (W) | 51–73 / 48–71 W | ⏳ HWiNFO | ⏳ | — |
| **CPU Memory** e **CPU Platform** | **0 W** fixo nas 12 amostras | o LHM ativa o sensor quando o MSR (0x619, 0x64D) é legível, mas no Coffee Lake de desktop o contador não avança ([IntelCpu.cs:478-499](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/3d331e3/LibreHardwareMonitorLib/Hardware/Cpu/IntelCpu.cs#L478-L499)); ⏳ HWiNFO para confirmar que ele não expõe esses domínios | ❌ | D8 |
| Tensões "CPU Core #1–8" | 0,98–1,32 V | são VIDs pedidos por núcleo (MSR 0x198), não a tensão medida; ⏳ HWiNFO ("Core VIDs") | ⏳ | D12 |
| CPU Core (tensão) | 0,99–1,244 V | bate com a entrada 0 da IT8686E (Vcore 1,176–1,38 V) | ✅ | — |
| Clocks | 3700–4701 MHz | ⏳ HWiNFO | ⏳ | — |

### 4.3 Placa-mãe (IT8686E)

A placa **não está na tabela do LHM** (nem na 0.9.6, commit `3d331e3`, nem no `master` de 26/10/2026): o chip cai no `default` ([SuperIOHardware.cs:2704-2723](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/3d331e3/LibreHardwareMonitorLib/Hardware/Motherboard/SuperIOHardware.cs#L2704-L2723)), que cria "Voltage #1–7" **sem escala e ocultas por padrão**, "Temperature #n" e "Fan #n". O ResetExplode ignora o "oculto por padrão" e mostra o valor cru do pino.

| Canal | ResetExplode | Com a escala da `B360M_H` (mesmo chip, [SuperIOHardware.cs:2072-2090](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/3d331e3/LibreHardwareMonitorLib/Hardware/Motherboard/SuperIOHardware.cs#L2072-L2090)) | HWiNFO | | Id |
|---|---|---|---|---|---|
| Voltage #1 | 1,236 V | Vcore 1,236 V | ⏳ | ⚠️ nome | D1 |
| Voltage #2 | **2,028 V** | +3,3 V = 2,028 × (1 + 29,4/45,3) = **3,344 V** | ⏳ | ❌ | D1 |
| Voltage #3 | **2,028 V** | +12 V = 2,028 × (1 + 10/2) = **12,17 V** | ⏳ | ❌ | D1 |
| Voltage #4 | **1,98 V** | +5 V = 1,98 × (1 + 15/10) = **4,95 V** | ⏳ | ❌ | D1 |
| Voltage #6 | 1,188 V | VCCSA 1,188 V | ⏳ | ⚠️ nome | D1 |
| Voltage #7 | 1,248 V | DIMM A/B 1,248 V (DDR4 nominal 1,2 V) | ⏳ | ⚠️ nome | D1 |
| +3V Standby / CMOS Battery | 3,384 / 3,168 V | já escalados pelo `default` | ⏳ | ✅ | — |
| Temperature #1–6 | 40 / 44 / 46 / 39 / 49 / 40 °C | `B360M_H`: #1 System, #2 PCH, #3 CPU, #5 VRM MOS; #4 e #6 sem nome | ⏳ | ⚠️ nomes | D1 |
| Fan #1–4 | 2273 / 0 / 1201 / 1709 RPM | `B360M_H`: #1 CPU Fan, #2 System Fan; demais sem nome | ⏳ | ⚠️ nomes | D1 |
| Controle "Fan #1–5" | **—** nas 12 amostras | linha sem valor nenhum | ⏳ | ❌ | D9 |

Os três valores escalados ficam dentro da tolerância ATX (±5 %), o que confirma que os divisores da `B360M_H` valem para esta placa; os nomes das temperaturas e ventoinhas precisam do HWiNFO.

### 4.4 GPU (mesma janela que o `nvidia-smi`)

| Item | ResetExplode (LHM) | `nvidia-smi` | |
|---|---|---|---|
| Temperatura | 54 °C | 54,3–54,8 °C | ✅ |
| Clock do núcleo | 270–525 MHz | 240–495 MHz | ✅ (amostras não sincronizadas ao ms) |
| Clock da memória | 405 / 810 MHz | 405 / 810 MHz (P8/P5) | ✅ |
| Consumo | 23,6–24,9 W ("GPU Package") | 23,0–24,8 W | ✅ |
| Ventoinhas | 0 RPM / 0 % | 0 % | ✅ |
| Memória usada | 2141–2258 MB | 1959–2059 MiB | ✅ (o LHM conta a reserva do driver, ~200 MB) |
| Link PCIe | "PCIe 2.0 x16 em repouso" (3.0 x16 máx.) | gen 1–2 em P8/P5 | ✅ |
| Hot spot, tensão do núcleo | 65,6–67 °C, 0,675 V | não exposto pelo `nvidia-smi`; ⏳ HWiNFO | ⏳ |

### 4.5 Discos

| Item | ResetExplode | Referência | | Id |
|---|---|---|---|---|
| Interface (aba Sistema) | **SCSI** | `Get-PhysicalDisk`: **NVMe** (o `DiskHealth` do próprio app já acerta via `MSFT_PhysicalDisk`) | ⚠️ | D4 |
| Capacidade (aba Sistema) | **953,86 GB** | 1.024,2 GB (o sensor "Total Space" do mesmo app mostra 1.024,21 GB) | ⚠️ | D5 |
| Total gravado / lido | **98.331 / 122.779 GB** | `smartctl`: 206.216.435 unidades × 512.000 B = **105,6 TB** gravados (o app mostra GiB com rótulo GB) | ⚠️ | D5 |
| Mínimo da temperatura, vida, dados, horas | **0 °C, 0 %, 0 GB, 0 h** já na 1ª amostra | impossível: o LHM grava 0 antes da primeira leitura S.M.A.R.T. (`GetValueOrDefault()`, [StorageDevice.cs:166-201](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/3d331e3/LibreHardwareMonitorLib/Hardware/Storage/StorageDevice.cs#L166-L201)) | ❌ | D7 |
| Limites de temperatura | 82 / 84 °C | `smartctl`: 83 / 85 °C (o LHM converte kelvin com 273,15 e trunca) | ⚠️ (1 °C, fica no LHM) | D10 |
| Desgaste, vida, horas, ciclos, reserva | 7 % / 93 % / 17.679 h / 6.806 / 100 % | `smartctl` idêntico | ✅ | — |

## 5. Veredito por módulo

| Módulo | Veredito |
|---|---|
| GPU | **confiável**: tudo o que tem referência bate com o `nvidia-smi` |
| Discos (saúde) | **confiável** no que importa (S.M.A.R.T., desgaste, reserva, horas); **unidades erradas** em capacidade e total gravado (D4, D5) e **mínimos impossíveis** (D7) |
| RAM | **incompleto e enganoso**: diz que não há timings quando o LHM tem o SPD (D2) e chama o 2666 do SMBIOS de "nominal do módulo" (D3) |
| Sistema | um **falso negativo** relevante: "Virtualização habilitada = False" com hypervisor ativo (D6) |
| Placa-mãe | **não confiável**: tensões cruas como se fossem tensões reais (+12 V aparece como 2,03 V), canais sem nome, linhas de controle sempre vazias (D1, D9) |
| CPU | temperaturas e consumo plausíveis, ⏳ confirmação do HWiNFO; dois sensores de consumo fixos em 0 W que não existem nesta CPU (D8) |

As correções estão no [spec 002](specs/002-correcoes-leitura.md).
