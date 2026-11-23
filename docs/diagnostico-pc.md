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

## 6. Memória e estabilidade

Medido em 23/11/2026 depois de fechar os servidores de compilação desta sessão (`dotnet build-server shutdown` às 10:22:45). Foram 7 fotos, uma a cada 5 min, de 10:24 a 10:54, só com leitura (`Get-Process`, CIM, contadores). Nenhum programa foi fechado nem desinstalado. Nas fotos não havia nenhum processo de build, de teste ou do `Diagnostico`.

### 6.1 Uso real

| Hora | RAM em uso | Commit / limite | Pagefile em uso (pico) | Processos |
|---|---|---|---|---|
| 08:44 (coleta da etapa 5) | 31,6 de 31,9 GB (**96–99 %**) | 43,1 / 60,9 GB | — | — |
| **10:29 (oficial, 2 min depois do shutdown)** | **17,0 GB (53 %)** | **32,2 / 60,9 GB** | 9,4 GB (pico 22,6 GB) | 483 |
| 10:54 | 21,2 GB (66 %) | 38,1 / 60,9 GB | 9,4 GB | 546 |

- O pagefile fica em `C:\pagefile.sys`, gerenciado pelo sistema, com 29,7 GB alocados. O pico de **22,6 GB** em uso mostra que, em algum momento, o PC precisou de cerca de 50 GB de commit para 32 GB de RAM.
- **De 08:44 para 10:29 o commit caiu 11 GB.** Os processos desta sessão (build, testes, `Diagnostico`, ResetExplode, HWiNFO) explicam no máximo 1 a 2 GB disso: o ResetExplode e o HWiNFO continuam abertos, com 0,18 e 0,08 GB privados. O resto foi liberado por programas do próprio uso do PC entre as duas medições, e não é possível atribuir depois do fato.
- **De 10:29 para 10:54 o commit subiu 5,9 GB, e não houve vazamento.** Foram 77 processos novos, com 5,4 GB privados:
  - uma nova sessão do Claude Code às 10:43: 2 processos `claude` com 1,1 GB e **30 `node`** com 2,7 GB (os servidores MCP que cada sessão abre);
  - 11 processos do Chrome (1,1 GB);
  - Fotos e Ferramenta de Captura.

### 6.2 Culpados (foto das 10:29)

| Programa | Processos | Privado (commit) | Working set |
|---|---|---|---|
| Chrome | 29 | 4,63 GB | 4,26 GB |
| Claude (sessões abertas desde 22/11) | 17 | 3,80 GB | 2,01 GB |
| VS Code | 20 | 2,76 GB | 1,38 GB |
| WebView2 (apps que usam o Edge por dentro) | 40 | 1,94 GB | 1,23 GB |
| ChatGPT | 16 | 1,82 GB | 1,16 GB |
| `node` (servidores MCP das sessões do Claude) | 19 | 1,50 GB | 0,10 GB |
| Kaspersky (`avp`) | 2 | 0,72 GB | 0,34 GB |
| Superwhisper | 2 | 0,64 GB | 0,06 GB |
| EA app (`EACefSubProcess` + `EADesktop`) | 6 | 1,04 GB | 0,48 GB |
| `uv`/`python` (servidores MCP em Python) | 12 | 0,97 GB | 0,00 GB |

Os 15 processos com maior commit às 10:29:

| Processo (PID) | Commit |
|---|---|
| chrome (20100) | 0,89 GB |
| chrome (34092) | 0,85 GB |
| chrome (22316) | 0,80 GB |
| claude (27588) | 0,74 GB |
| avp (4636) | 0,70 GB |
| claude (23312) | 0,67 GB |
| Superwhisper (28540) | 0,64 GB |
| claude (6836) | 0,60 GB |
| ChatGPT (36072) | 0,58 GB |
| claude (34704) | 0,55 GB |
| claude (22500) | 0,53 GB |
| chrome (12952) | 0,51 GB |
| Code (3320) | 0,51 GB |
| explorer (11064) | 0,48 GB |
| chrome (47172) | 0,46 GB |

Os 15 com maior working set foram 4 processos do Chrome (de 0,50 a 0,97 GB), claude (27588, 0,69 GB), ResetExplode (0,62 GB), ChatGPT, Code, WebView2, EA, `avp`, explorer, WhatsApp e mais um `claude`. Nenhum passa de 1 GB.

**Vazamento: não há.** Em 30 minutos, o maior crescimento de um mesmo processo foi de **+104 MB** (claude 27588), seguido de +98 MB (chrome 20100) e +66 MB (ChatGPT). Nenhum cresceu de forma contínua acima de 100 MB, e o portão de 4 GB não foi acionado. O alto uso vem da **soma de muitos programas que ficam abertos**:
- várias sessões do Claude Code abertas desde 22/11, cada uma com seus servidores MCP;
- Chrome, VS Code, ChatGPT e EA app.

### 6.3 Eventos dos últimos 90 dias (24/08 a 23/11/2026)

| Data | Evento | Código / parâmetros | Dispositivo |
|---|---|---|---|
| 16/09 17:41 | disk 51 (×8): "erro durante uma operação de paginação" | — | `Harddisk3` = **pendrive SanDisk Cruzer Blade (USB)**, conectado e desconectado 5 vezes em 3 s (Partition/Diagnostic 1006) |
| 27/09 14:49 | Tela azul + falha ao gravar o despejo (volmgr 161) | **0x154** UNEXPECTED_STORE_EXCEPTION | — |
| 03/10 13:12 | Tela azul + falha ao gravar o despejo | **0x7A** KERNEL_DATA_INPAGE_ERROR | — |
| 18/10 21:04 | Tela azul + falha ao gravar o despejo | **0x7A** KERNEL_DATA_INPAGE_ERROR | — |
| 18/10 21:17 | Tela azul + falha ao gravar o despejo | **0x3B**, p1 = `0xC0000006` (STATUS_IN_PAGE_ERROR) | — |
| set–out | Kernel-Power 41 (desligamento sem aviso) | 7 em setembro, 4 em outubro | — |
| 22–23/11 | WER "LiveKernelEvent 193" (×10) | o Windows reenviando sempre o mesmo relatório de 31/10 (`WATCHDOG-20261031-2203.dmp`, travamento do driver de vídeo) | GPU |

- **WHEA-Logger: nenhum evento em 90 dias** (nem de memória, nem de disco, nem de CPU). O portão (a) não foi acionado. Como a RAM não tem ECC, erro de memória não apareceria como WHEA de qualquer forma.
- **Resource-Exhaustion-Detector: nenhum evento.** O Windows não chegou a ficar sem memória virtual.
- **Erros de NTFS e `stornvme`: nenhum.** Os únicos `disk` são os do pendrive.
- **Nenhum minidump existe** (`C:\Windows\Minidump` vazio, sem `MEMORY.DMP`), embora o Windows esteja configurado para gravar minidump (`CrashDumpEnabled = 3`). Nas 4 telas azuis o volmgr 161 diz que **a gravação do despejo falhou**.

### 6.4 Relação com as telas azuis

As quatro telas azuis têm a mesma assinatura: **falha ao trazer de volta uma página que estava no disco**.
- O 0x7A é o erro de leitura da página.
- No 0x3B, o código é `0xC0000006`, que é o mesmo erro.
- O 0x154 é falha no repositório de memória comprimida, que também depende de leitura em disco.

Nas quatro, o Windows **também não conseguiu gravar o despejo**, que vai para o mesmo disco. O `C:` e o `pagefile.sys` ficam no **NVME SSD 1TB** (disco 0, firmware 2008XKR).

Isso aponta para o **caminho até o disco do sistema sumindo por instantes** (controlador NVMe, economia de energia do link, encaixe ou alimentação do M.2), e não para um defeito de RAM:
- uma falha de RAM costuma dar bugchecks variados (0x1A, 0x50, 0x0A), e não quatro da mesma família de leitura de página. Ressalva: esta RAM não tem ECC, e sem ECC falha de memória **não gera WHEA**, então a ausência de WHEA não descarta a RAM sozinha;
- o S.M.A.R.T. desse NVMe está limpo (0 erros de mídia, 0 entradas no log de erros, 100 % de reserva), então não há sinal de setor ruim;
- os **348 desligamentos inseguros** registrados pelo próprio NVMe combinam com quedas de energia ou travamentos totais.

**A RAM cheia não causa as telas azuis, mas aumenta a exposição.** Com 96–99 % de uso e até 22,6 GB no pagefile, o Windows lê páginas do disco o tempo todo. Se o NVMe some por um instante, a próxima leitura de página falha e vira 0x7A. Com a RAM folgada, o mesmo soluço do disco poderia passar sem tela azul.

Sem minidump não dá para fechar a causa. As ações abaixo servem para coletar a prova na próxima ocorrência e reduzir a exposição até lá.

### 6.5 Ações recomendadas (nada foi executado)

1. **Reduzir o commit, sem desinstalar nada.** Fechar as sessões do Claude Code que não estão em uso: cada uma mantém cerca de 15 servidores MCP (`node`, `uv`, `python`), e havia 17 processos `claude` vindos de 22/11. Fechar também o ChatGPT e o EA app quando não estiverem em uso. Só isso devolve de 5 a 8 GB.
2. **Desligar a economia de energia do link PCIe para testar** (Opções de energia → PCI Express → Gerenciamento de energia do estado do link: Desativado). O próprio NVMe não tem estados de energia profundos (o `smartctl` mostra um único estado de 9 W e nenhum APST), então o que sobra para testar é o link (ASPM). Fica a critério do dono, porque muda configuração.
3. **Na próxima parada, abrir o gabinete:** reencaixar o NVMe de 1 TB e conferir o parafuso e o dissipador do M.2. Os 348 desligamentos inseguros pedem também conferir a fonte e o estabilizador ou nobreak.
4. **Garantir que o próximo despejo seja gravado:** manter o pagefile no `C:`, sistema gerenciado como está, e acompanhar se aparecem arquivos em `C:\Windows\Minidump`. Se a próxima tela azul vier de novo sem despejo, o problema está no caminho até o disco.
5. **Firmware do NVMe:** o "NVME SSD 1TB" é um modelo genérico com controlador **Silicon Motion** (PCI vendor 0x126F, NVMe 1.3, firmware 2008XKR). Sem a marca de quem montou o SSD, não há atualização de firmware confiável para aplicar. Se as telas azuis voltarem, trocar esse NVMe por um de marca é a ação definitiva, porque ele carrega o sistema e o pagefile.
6. **Teste de RAM** (Diagnóstico de Memória do Windows ou MemTest86, uma noite): não há indício contra a RAM, mas há 4 módulos de dois fabricantes misturados. O teste descarta essa hipótese. Fica para quando o PC puder ficar parado.
