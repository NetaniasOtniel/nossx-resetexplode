# Spec 003 — Estabilidade: telas azuis de page-in e despejo que não é gravado

| Campo | Valor |
|---|---|
| Origem | [docs/diagnostico-pc.md](../diagnostico-pc.md) §6 (23/11/2026) |
| Branch | `fix/estabilidade` |
| Data | 23/11/2026 |
| Estado | especificado; backup em [docs/estabilidade-backup.txt](../estabilidade-backup.txt); aplicação na §5, depois do merge da `fix/leituras` |
| Escopo | só configuração do Windows (plano de energia e despejo de memória). Nada de BIOS, de reiniciar, de teste de RAM ou de fechar programas |

## 1. Problema

Houve **4 telas azuis em 90 dias**, todas por falha ao trazer de volta uma página que estava no disco. Em nenhuma delas o Windows conseguiu gravar o despejo. Sem despejo, a causa não pode ser provada, e cada nova queda se perde do mesmo jeito.

## 2. Evidências (medidas em 23/11/2026)

| Data | Bugcheck | Parâmetro | Despejo |
|---|---|---|---|
| 27/09 14:49 | 0x154 UNEXPECTED_STORE_EXCEPTION | p1 ffffb98dd0f2a000 | volmgr 161: falha na criação do arquivo de despejo (BugCheckProgress 0x41) |
| 03/10 13:12 | 0x7A KERNEL_DATA_INPAGE_ERROR | p1 0x20 | volmgr 161 (0x41) |
| 18/10 21:04 | 0x7A KERNEL_DATA_INPAGE_ERROR | p1 ffffb7c044a55230 | volmgr 161 (0x40049) |
| 18/10 21:17 | 0x3B | p1 **0xC0000006** (STATUS_IN_PAGE_ERROR) | volmgr 161 (0x40049) |

- O `C:` e o `pagefile.sys` (gerenciado pelo sistema, 29,7 GB alocados, pico de 22,6 GB) ficam no disco 0, **"NVME SSD 1TB"**: controlador Silicon Motion **SM2263** (`PCI\VEN_126F&DEV_2263`), NVMe 1.3, firmware 2008XKR. É um modelo sem DRAM, que usa parte da RAM do PC (HMB).
- S.M.A.R.T. do disco 0 (`smartctl` 7.5): PASSED, critical_warning 0, 0 erros de mídia, 0 entradas no log de erros, 100 % de reserva, 7 % de desgaste, 17.679 h, **348 desligamentos inseguros**.
- O disco 0 anuncia **um único estado de energia** (PS0, 9 W). Não tem estado não operacional nem APST, então nunca entra em economia própria.
- O disco 1 (`D:`) é um ADATA IM2P33F3A 256 GB (`VEN_1CC1&DEV_33F3`), interno, NTFS, com 81,5 GB livres. Ele tem dois estados não operacionais: PS3 (3 + 3 ms) e PS4 (5 + 43 ms).
- Os dois discos usam o driver da Microsoft, `stornvme` 10.0.26100.10068. Não há driver de fabricante, então os ajustes do plano de energia valem para eles.
- Não há nenhum WHEA, `stornvme`, NTFS, Resource-Exhaustion nem "disk" do disco 0 em 90 dias. Os únicos "disk 51" vieram de um pendrive USB em 16/09. A RAM não tem ECC, então um erro de memória não apareceria como WHEA.
- Há BitLocker ligado no `C:` e no `D:` (`System.Volume.BitLockerProtection = 1`), e o filtro de despejo `dumpfve.sys` está ativo.

## 3. Hipótese

As quatro quedas e as quatro falhas de despejo têm a mesma causa provável: **o caminho até o disco 0 deixa de responder por instantes**. Esse disco guarda o `C:`, o pagefile e o destino do despejo. Quando ele some:
- a próxima leitura de página falha (0x7A, 0x3B com `C0000006` e 0x154);
- a gravação do despejo, que vai para o mesmo disco, também falha (volmgr 161).

A pressão de RAM (96–99 % às 08:44, pagefile com pico de 22,6 GB) aumenta a quantidade de leituras de página e, com ela, a exposição a esse soluço.

Três hipóteses foram descartadas ou enfraquecidas pela medição:
- **NVMe entrando em estado profundo:** o disco 0 só tem PS0.
- **Economia de energia do link PCIe pelo Windows:** o ASPM já está **Desligado** (CA 0 e CC 0) no plano ativo. Pode existir ASPM controlado pela BIOS, mas a BIOS está fora do escopo.
- **Problema do HMB do Windows 11 24H2:** a falha documentada foi em SSDs WD/SanDisk, com evento `stornvme` 11. Esse evento não existe aqui.

Continuam possíveis: o encaixe ou a alimentação do M.2, o firmware do SM2263, a fonte (os 348 desligamentos inseguros) e a RAM (sem ECC, nunca testada).

Por isso a mudança tem dois objetivos:
1. tirar os estados de economia que ainda existem no caminho de armazenamento, para não somar mais uma variável;
2. **fazer o próximo despejo ser gravado num disco que não é o do `C:`**, porque é ele que vai dizer a causa.

## 4. Pesquisa

| Tema | O que diz | Fonte |
|---|---|---|
| Ajustes de NVMe do StorNVMe | O driver escolhe o estado não operacional mais profundo cujo ENLAT+EXLAT seja ≤ a tolerância (primária para o F1, secundária para os mais profundos), depois do timeout de ociosidade. Valores do Equilibrado: 200/100 ms, 15/50 ms, 2000/1000 ms e 100 ms. Alteração: `powercfg -setacvalueindex/-setdcvalueindex <plano> sub_disk <GUID> <ms>` seguido de `-setactive` | [Microsoft Learn: Power management for storage hardware devices – NVMe](https://learn.microsoft.com/en-us/windows-hardware/design/component-guidelines/power-management-for-storage-hardware-devices-nvme) |
| Expor os ocultos | `powercfg -attributes 0012ee47-… <GUID> -ATTRIB_HIDE` | [Microsoft Q&A](https://learn.microsoft.com/en-us/answers/questions/4325186/ive-noticed-something-weird-about-my-new-nvme-ssd) |
| Perfil contra queda de NVMe | timeouts e tolerâncias em 0, com NOPPME desligado. Em notebook, o NVMe ficou cerca de 10 °C mais quente e gastou cerca de 1 W a mais; esses ajustes só valem com o `stornvme` da Microsoft | [Zhihu](https://zhuanlan.zhihu.com/p/1998424542985593649), [Fukutoh](https://www.fukutoh.co.jp/nvme-ssd%E3%81%AE%E7%9C%81%E9%9B%BB%E5%8A%9B%E8%A8%AD%E5%AE%9A%E3%81%AE%E7%BD%A0/), [Guru3D](https://forums.guru3d.com/threads/importance-of-pagefile-for-gaming-smoothness.447687/page-10), [Bitsum: GUIDs de energia](https://bitsum.com/known-windows-power-guids/) |
| ASPM | `SUB_PCIEXPRESS ASPM` (`501a4d13…`/`ee12f906…`): 0 = Desligado, 1 = Moderada, 2 = Máxima | [Microsoft Learn: Link state power management](https://learn.microsoft.com/en-us/windows-hardware/customize/power-settings/pci-express-settings-link-state-power-management) |
| Minidump e pagefile | o minidump exige pagefile no volume de boot (2 MB no texto de solução de problemas; 1 MB na tabela de dimensionamento); um arquivo de despejo dedicado pode ficar em qualquer volume que aceite pagefile | [Memory dump file options](https://learn.microsoft.com/en-us/troubleshoot/windows-server/performance/memory-dump-file-options), [Configure system failure and recovery options](https://learn.microsoft.com/en-us/troubleshoot/windows-client/performance/configure-system-failure-and-recovery-options), [Page file size for 64-bit Windows](https://learn.microsoft.com/en-us/troubleshoot/windows-client/performance/how-to-determine-the-appropriate-page-file-size-for-64-bit-versions-of-windows) |
| DedicatedDumpFile | `HKLM\…\CrashControl\DedicatedDumpFile` (REG_SZ, caminho completo) e `DumpFileSize` (REG_DWORD, MB). Vale depois de reiniciar; se o arquivo for pequeno demais, o despejo falha. A Microsoft usa esse mecanismo como padrão no Validation OS | [Microsoft Learn: Server Core memory dump](https://learn.microsoft.com/en-us/windows-server/administration/server-core/server-core-memory-dump), [Validation OS: crash dump](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/validation-os-configure-usb-crash-dump?view=windows-11), [KB 957517](https://mskb.pkisolutions.com/kb/957517) |
| 0x7A e HMB no 24H2 | 0x7A repetido costuma anteceder morte do disco; o caso do HMB no 24H2 (WD/SanDisk) aparece como `stornvme` 11 e se resolve com firmware | [Industrial Monitor Direct](https://industrialmonitordirect.com/blogs/knowledgebase/troubleshooting-kernel-data-inpage-error-bsod-loops-on-nvme-ssds), [Techeia](https://www.techeia.com/blog/fix-kernel-data-inpage-error-windows-11) |
| Silicon Motion especificamente | não achei relato atual nem recomendação oficial para queda de NVMe com SM2263 no Windows 11; fica registrado como ausência de fonte | — |

## 5. Mudanças

Todas no plano ativo **Equilibrado** (`381b4222-f694-41f0-9685-ff5bb260df2e`), em CA e CC. O valor original vem de [docs/estabilidade-backup.txt](../estabilidade-backup.txt).

| # | Configuração | Original (CA / CC) | Novo (CA / CC) | Por quê |
|---|---|---|---|---|
| E1 | PCI Express → Link State Power Management (`SUB_PCIEXPRESS ASPM`) | 0 / 0 (Desligado) | **sem mudança** (já está como pedido) | — |
| E2 | Disco → Desligar disco rígido após (`DISKIDLE`) | 0 / 0 (Nunca) | **sem mudança** | — |
| E3 | Disco → Primary NVMe Idle Timeout | 0 / 0 ms | **sem mudança** | — |
| E4 | Disco → Secondary NVMe Idle Timeout | 0 / 0 ms | **sem mudança** | — |
| E5 | Disco → **Primary** NVMe Power State Transition Latency Tolerance | 15 / 50 ms | **0 / 0** | com tolerância 0, nenhum estado não operacional é elegível, e o NVMe fica em operação (regra do StorNVMe, §4) |
| E6 | Disco → **Secondary** NVMe Power State Transition Latency Tolerance | 100 / 100 ms | **0 / 0** | idem para os estados mais profundos (o PS4 do ADATA tem 48 ms) |
| E7 | Disco → NVMe NOPPME | 1 / 0 | **0 / 0** | perfil "sem economia" da pesquisa |
| E8 | Atributo "oculto" dos ajustes E3–E7 e do ASPM | ocultos | **visíveis** em Opções de energia | pedido do dono: expor com `powercfg -attributes … -ATTRIB_HIDE` |
| D1 | CrashControl: `CrashDumpEnabled` 3, `MinidumpDir` `%SystemRoot%\Minidump`, `AutoReboot` 1, `Overwrite` 1 | já assim | **sem mudança** | — |
| D2 | Pagefile no `C:`, gerenciado pelo sistema (≥ 1 GB) | já assim (29.696 MB) | **sem mudança** | — |
| D3 | `CrashControl\DedicatedDumpFile` (REG_SZ) | não existe | **`D:\dedicateddump.sys`** | grava o despejo no disco 1 mesmo que o caminho até o disco 0 tenha caído; no boot seguinte o Windows copia para `%SystemRoot%\Minidump` |
| D4 | `CrashControl\DumpFileSize` (REG_DWORD, MB) | não existe | **1024** | um minidump tem de 64 a 256 KB; 1 GB dá folga para o cabeçalho e para mudar para despejo de kernel depois, sem pesar nos 81,5 GB livres do `D:` |

Decisão sobre o D3: **aplicar.** A Microsoft documenta o recurso e o usa como padrão no Validation OS. O `D:` atende a todas as condições:
- é interno, fixo, NTFS e não removível;
- não é o disco do sistema, então não depende do disco 0;
- tem espaço de sobra;
- tem BitLocker ligado, então o despejo continua criptografado pelo `dumpfve.sys`, como no `C:`. **Nenhuma proteção é desligada.**

Limites do D3, registrados aqui:
- Numa placa B360 os dois slots M.2 provavelmente ficam atrás do mesmo chipset (não medi a topologia PCIe). Se a falha for no chipset, e não no disco 0, o `D:` também some, e o despejo falha de novo. Isso por si só já seria informação útil.
- Com E5 e E6, o ADATA (`D:`) também deixa de entrar em PS3/PS4. Em desktop o custo é baixo: alguns décimos de watt e alguns graus no SSD.

## 6. Como reverter

Os mesmos comandos, num prompt **elevado**. Os valores originais estão no backup.

```bat
:: E5–E7: tolerâncias e NOPPME de volta ao original do plano Equilibrado
powercfg /setacvalueindex SCHEME_CURRENT 0012ee47-9041-4b5d-9b77-535fba8b1442 fc95af4d-40e7-4b6d-835a-56d131dbc80e 15
powercfg /setdcvalueindex SCHEME_CURRENT 0012ee47-9041-4b5d-9b77-535fba8b1442 fc95af4d-40e7-4b6d-835a-56d131dbc80e 50
powercfg /setacvalueindex SCHEME_CURRENT 0012ee47-9041-4b5d-9b77-535fba8b1442 dbc9e238-6de9-49e3-92cd-8c2b4946b472 100
powercfg /setdcvalueindex SCHEME_CURRENT 0012ee47-9041-4b5d-9b77-535fba8b1442 dbc9e238-6de9-49e3-92cd-8c2b4946b472 100
powercfg /setacvalueindex SCHEME_CURRENT 0012ee47-9041-4b5d-9b77-535fba8b1442 fc7372b6-ab2d-43ee-8797-15e9841f2cca 1
powercfg /setdcvalueindex SCHEME_CURRENT 0012ee47-9041-4b5d-9b77-535fba8b1442 fc7372b6-ab2d-43ee-8797-15e9841f2cca 0
powercfg /setactive SCHEME_CURRENT
:: E8: ocultar de novo
for %G in (d639518a-e56d-4345-8af2-b9f32fb26109 d3d55efd-c1ff-424e-9dc3-441be7833010 fc95af4d-40e7-4b6d-835a-56d131dbc80e dbc9e238-6de9-49e3-92cd-8c2b4946b472 fc7372b6-ab2d-43ee-8797-15e9841f2cca) do powercfg -attributes 0012ee47-9041-4b5d-9b77-535fba8b1442 %G +ATTRIB_HIDE
powercfg -attributes 501a4d13-42af-4429-9fd1-a8218c268e20 ee12f906-d277-404b-b6da-e5fa1a576df5 +ATTRIB_HIDE
:: D3–D4: tirar o despejo dedicado (vale depois de reiniciar; depois apagar D:\dedicateddump.sys)
reg delete "HKLM\SYSTEM\CurrentControlSet\Control\CrashControl" /v DedicatedDumpFile /f
reg delete "HKLM\SYSTEM\CurrentControlSet\Control\CrashControl" /v DumpFileSize /f
```

Para voltar o plano inteiro ao padrão de fábrica, use `powercfg -restoredefaultschemes`. **Cuidado:** esse comando desfaz também qualquer outro ajuste que o dono tenha feito nos planos.

## 7. Critério de sucesso

| Id | Critério | Como mede | Prazo |
|---|---|---|---|
| S1 | **zero BugCheck** (Kernel-Power 41 com BugcheckCode ≠ 0 e WER 1001 BlueScreen) | `Get-WinEvent` do Sistema, a partir da reinicialização que ativa o D3 | 30 dias |
| S2 | **ou** o próximo BugCheck gera um minidump legível em `%SystemRoot%\Minidump` (abre no WinDbg com `!analyze -v` e mostra o parâmetro de I/O do 0x7A), sem volmgr 161 | arquivo e evento | na próxima ocorrência |
| S3 | as mudanças E5–E7 e D3–D4 continuam aplicadas | `powercfg /qh` e `reg query` contra a §5 | na verificação |
| S4 | o `D:\dedicateddump.sys` existe com cerca de 1 GB depois de reiniciar | `Get-Item` | primeira reinicialização |

Se o S2 acontecer com o 0x7A mostrando `0xC000009C` ou `0xC0000185` como status, ou se voltar a dar volmgr 161 mesmo com o D3, o próximo passo é **trocar o NVMe do `C:`** (spec próprio). Se o minidump apontar outro componente, a hipótese da §3 cai e o spec é reaberto.
