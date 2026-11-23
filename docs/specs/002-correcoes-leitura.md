# Spec 002 — Correções de leitura achadas no diagnóstico do PC

| Campo | Valor |
|---|---|
| Origem | [docs/diagnostico-pc.md](../diagnostico-pc.md) (23/11/2026), §4 |
| Branch | `fix/leituras` |
| Data | 23/11/2026 |
| Estado | implementado e medido em 23/11/2026 (coleta elevada `diag\20261123-101010-fix-elevado`); aceite na §6. **A comparação com o HWiNFO virou item pendente (§7)**: a memória compartilhada não foi ligada até 14:10, então todas as tensões da placa saem marcadas "estimado (escala da B360M H)" |
| Escopo | só o que o app **mostra**: nenhuma escrita em hardware, nenhum ajuste de ventoinha, nenhum sensor novo lido por registrador próprio |

## 1. Problema

O diagnóstico achou 12 divergências (D1–D12) entre o que o ResetExplode mostra e as referências independentes. Quatro delas fazem o app afirmar algo falso:

- +12 V aparece como 2,03 V.
- A virtualização aparece como desligada com o hypervisor ativo.
- A tela diz que os timings da RAM não estão disponíveis, quando o próprio app já tem o SPD.
- Os NVMe têm mínimo de 0 °C.

As demais são unidades trocadas, nomes genéricos ou linhas vazias.

## 2. Pesquisa: como o LHM atual e o HWiNFO tratam os mesmos sensores

| Tema | LHM (0.9.6 = commit `3d331e3`; `master` de 26/10/2026) | HWiNFO | Fonte |
|---|---|---|---|
| Placa sem entrada na tabela | `Identification.cs` não tem "B360M AORUS Gaming 3" (só `B360M H` e `B360 AORUS GAMING 3 WIFI-CF`); o IT8686E cai no `default`, que cria "Voltage #1–7" **sem escala e ocultas por padrão** (`hidden = true`), "Temperature #n", "Fan #n" | o histórico de versões registra suporte ao IT8686E e "monitoramento melhorado nas placas GIGABYTE B360 e H310" (banco próprio por placa) | [SuperIOHardware.cs:2704-2723](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/3d331e3/LibreHardwareMonitorLib/Hardware/Motherboard/SuperIOHardware.cs#L2704-L2723), [Identification.cs:416-421](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/3d331e3/LibreHardwareMonitorLib/Hardware/Motherboard/Identification.cs#L416-L421), [HWiNFO: histórico de versões](https://www.hwinfo.com/version-history/) |
| Escala das tensões do IT8686E em placa Gigabyte B360 | `B360M_H` (mesmo chip): +3,3 V Ri/Rf 29,4/45,3; +12 V 10/2; +5 V 15/10; VCCSA e DIMM sem divisor; fórmula `V = v + (v − Vf)·Ri/Rf`, reaplicada a cada `Update` a partir de `sensor.Parameters` | lm-sensors (it87) usa o mesmo ×1,65 no +3,3 V do IT8686E; a issue do OHM sobre a B360 AORUS Gaming 3 mostra que outras ferramentas genéricas também não exibem os trilhos | [SuperIOHardware.cs:2072-2090](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/3d331e3/LibreHardwareMonitorLib/Hardware/Motherboard/SuperIOHardware.cs#L2072-L2090), [:6085](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/3d331e3/LibreHardwareMonitorLib/Hardware/Motherboard/SuperIOHardware.cs#L6085), [Level1Techs: it87 patch](https://forum.level1techs.com/t/i-patched-the-it87-temperature-monitoring-driver-need-some-help-testing-it/132675?page=2), [OHM #1488](https://github.com/openhardwaremonitor/openhardwaremonitor/issues/1488) |
| Tensões por núcleo da Intel | os sensores "CPU Core" e "CPU Core #n" saem do `IA32_PERF_STATUS` e, no código, são os `_coreVIDs` | o autor separa **Core VID** (tensão que o núcleo pede) de **Vcore** (medida pelo sensor da placa); até o Raptor Lake, todos os núcleos compartilham uma única tensão | [IntelCpu.cs:504-514](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/3d331e3/LibreHardwareMonitorLib/Hardware/Cpu/IntelCpu.cs#L504-L514), fórum HWiNFO [#6848](https://www.hwinfo.com/forum/threads/whats-the-difference-between-core-vid-vs-vcore.6848/), [#454](https://www.hwinfo.com/forum/threads/core-vid-vs-vcore.454/), [#10090](https://www.hwinfo.com/forum/threads/vcore-vs-vr-vout-vcore.10090/) |
| Domínios RAPL DRAM e Platform | o sensor é ativado se o MSR (0x619, 0x64D) for **legível**, mesmo com o contador parado | pendente de HWiNFO (§7); no Coffee Lake de desktop ele não costuma listar esses domínios | [IntelCpu.cs:478-499](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/3d331e3/LibreHardwareMonitorLib/Hardware/Cpu/IntelCpu.cs#L478-L499) |
| Valores do NVMe antes da primeira leitura S.M.A.R.T. | `s.Smart.Temperature.GetValueOrDefault()` e os demais convertem "sem dado" em 0, e o 0 vira o mínimo | pendente de HWiNFO (§7) | [StorageDevice.cs:166-201](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/3d331e3/LibreHardwareMonitorLib/Hardware/Storage/StorageDevice.cs#L166-L201) |
| Unidades do "Data Written" | `HostWrites` vem do DiskInfoToolkit em **GiB** (unidades × 512.000 / 2³⁰), enquanto "Total Space" vem em GB decimal (`ByteToGigabyte`) | o "Total Host Writes" do HWiNFO não segue nem GiB nem GB decimal (pelos números de usuários, ≈ unidades/2048); o NVMe define a unidade como 512.000 bytes; o TBW dos fabricantes é decimal | [StorageDevice.cs:191-209](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/3d331e3/LibreHardwareMonitorLib/Hardware/Storage/StorageDevice.cs#L191-L209), [AnandTech: 970 EVO Plus no HWiNFO](https://forums.anandtech.com/threads/samsung-970-evo-plus-500gb-writes-really-high.2573817), [MakeUseOf](https://www.makeuseof.com/what-windows-was-writing-to-ssd-the-number-shocked-me/), [PCSP](https://pcserverandparts.com/news/used-ssd-health-check-2026/) |
| Virtualização com hypervisor ativo | — | com Hyper-V/VBS ligado, `VirtualizationFirmwareEnabled`, `VMMonitorModeExtensions` e `SecondLevelAddressTranslationExtensions` vêm `False`; o certo é olhar `Win32_ComputerSystem.HypervisorPresent` | [Microsoft Q&A](https://learn.microsoft.com/en-us/answers/questions/3860866/virtualizationfirmwareenabled-shows-false-even-whe), [Scripting Blog](https://devblogs.microsoft.com/scripting/use-powershell-to-detect-if-hypervisor-is-present/), [claude-code #27316](https://github.com/anthropics/claude-code/issues/27316) |
| HWiNFO fora do Pro | o log CSV por linha de comando é só do Pro; a memória compartilhada (`SensorsSM=1`) existe no gratuito | — | [fórum HWiNFO #8549](https://hwinfo.com/forum/threads/supported-command-line-parameters-in-hwinfo64-pro.8549/), [#7337](https://www.hwinfo.com/forum/threads/shared-memory-support-turning-on-by-itself.7337/) |

## 3. Divergências, causa e correção

| Id | Problema (medido) | Causa (arquivo:linha) | Correção |
|---|---|---|---|
| D1 | Placa: +3,3/+12/+5 V aparecem como 2,03/2,03/1,98 V; canais "Voltage/Temperature/Fan #n" | LHM sem entrada para a placa (§2). O app mostra até os sensores marcados como ocultos por padrão: [SensorMonitor.cs:43-59](../../SensorMonitor.cs) não olha `IsDefaultHidden` | Novo `BoardProfiles.cs`. Para a **B360M AORUS Gaming 3** com IT8686E, depois do `Open()` o app aplica nome e Ri/Rf pela API pública do LHM (`sensor.Name`, `sensor.Parameters[i].Value`), com os valores da `B360M_H`: Vcore, +3,3 V, +12 V, +5 V, VCCSA, DIMM, e as temperaturas System, PCH, CPU e VRM MOS, mais CPU Fan e System Fan. Em placa sem perfil, sensor `IsDefaultHidden` não aparece |
| D2 | RAM: "Timings: O Windows não expõe" | [SystemInfoCollector.cs:220](../../SystemInfoCollector.cs) é texto fixo; o LHM do app já lê o SPD (`/memory/dimm/n/timing/*`) | `SystemInfoCollector.ApplySpd(ram, rows)` roda depois que os sensores abrem ([MainWindow.xaml.cs:190](../../MainWindow.xaml.cs), e o mesmo no `tools/Diagnostico`). Ele mostra os timings do SPD em ns e em ciclos (arredondamento JEDEC) na velocidade nominal e na velocidade em uso, e avisa que os timings configurados pela BIOS não são lidos |
| D3 | "Velocidade nominal reportada pelo módulo: 2666" para módulos DDR4-3200 | [SystemInfoCollector.cs:181](../../SystemInfoCollector.cs) e [:243](../../SystemInfoCollector.cs) usam `Win32_PhysicalMemory.Speed`, que é o que a BIOS escreve no SMBIOS | O nominal vem do SPD (`2000 / tCKAVGmin`). O valor do WMI vira "Velocidade máxima informada pela BIOS" |
| D4 | Interface dos NVMe: "SCSI" | [SystemInfoCollector.cs:278](../../SystemInfoCollector.cs) usa `Win32_DiskDrive.InterfaceType`; o [DiskHealth.cs:96](../../DiskHealth.cs) já faz certo com `MSFT_PhysicalDisk.BusType` | Mesmo mapeamento do `DiskHealth`, com o `InterfaceType` só como último recurso |
| D5 | Unidades misturadas: capacidade 953,86 "GB" e 1.024,21 GB para o mesmo disco; total gravado 98.331 "GB" contra 105,6 TB | `Bytes()` divide por 1024 ([SystemInfoCollector.cs:38-46](../../SystemInfoCollector.cs)); o "Data Read/Written" do LHM vem em GiB (§2) | Disco é sempre decimal, como no rótulo, no `smartctl` e no TBW: a capacidade usa base 1000, e o "Data Read/Written" de `Storage` é convertido de GiB para GB ao entrar na linha. O `DiskHealth` mostra TB acima de 1.000 GB. A RAM continua binária |
| D6 | "Virtualização habilitada: False" com hypervisor ativo | [SystemInfoCollector.cs:91](../../SystemInfoCollector.cs) | `HypervisorPresent = True` → "Sim (hypervisor do Windows ativo)"; os dois falsos → "Não (desligada na BIOS ou sem suporte)" |
| D7 | Mínimo 0 °C, 0 %, 0 GB, 0 h nos NVMe | O `SensorRow` copia o `Min`/`Max` do LHM ([SensorMonitor.cs:60](../../SensorMonitor.cs), [SensorRow.cs:30-36](../../SensorRow.cs)), que guardou o 0 de antes da leitura S.M.A.R.T. | O `SensorRow` passa a acumular o próprio mínimo e máximo a partir dos valores que o app leu. Isso também tira dos extremos os valores crus de antes da escala do D1 |
| D8 | "CPU Memory" e "CPU Platform" fixos em 0 W | §2 (IntelCpu.cs:478-499) | Sensor de consumo da CPU só entra na lista depois de ler um valor > 0 |
| D9 | Controle "Fan #1–5" sempre "—" | LHM sem leitura de PWM nessa configuração; o app cria a linha mesmo sem valor | Linha nova só entra quando o sensor já tem valor (regra geral) |
| D10 | Limites de temperatura do NVMe 82/84 contra 83/85 °C do `smartctl` | Conversão de kelvin com 273,15 e truncamento no DiskInfoToolkit/LHM | **Não corrigido**: 1 °C, dentro do LHM; fica registrado |
| D11 | "Versão: Default string" da placa | Placeholder do SMBIOS repassado por `S()` ([SystemInfoCollector.cs:35-36](../../SystemInfoCollector.cs)) | Placeholders conhecidos ("Default string", "To be filled by O.E.M.", "System Product Name", "Not Specified") viram "—" |
| D12 | Tensões "CPU Core" e "CPU Core #1–8" parecem medidas | São VIDs (§2) | Renomear para "VID (pedido pela CPU)" e "VID núcleo #n". A tensão medida é o "Vcore" da placa (D1) |

## 4. Implementação

1. `BoardProfiles.cs` (novo). Contém a tabela de perfis por nome de placa e chip, e `Apply(IComputer)`, que roda logo depois do `Open()` em `SensorMonitor.OpenAsync`. A lógica de escolha é pura e testável: `Find(board, chip)` e `Volts(raw, ri, rf, vf)`.
2. `SensorMonitor.Walk`:
   - pula `IsDefaultHidden` que não tenha perfil;
   - só cria a linha quando há valor;
   - consumo da CPU só entra com valor > 0;
   - aplica o nome de exibição dos VIDs;
   - converte "Data Read/Written" de `Storage` para GB decimal.
3. `SensorRow.Update(value)` passa a acumular o próprio mínimo e máximo.
4. `SystemInfoCollector`:
   - `ApplySpd`;
   - nominal da RAM pelo SPD;
   - virtualização pelo `HypervisorPresent`;
   - barramento pelo `MSFT_PhysicalDisk`;
   - capacidade decimal;
   - placeholders.
5. `MainWindow.xaml.cs` chama `ApplySpd` depois do `Task.WhenAll`; o `tools/Diagnostico` faz o mesmo.
6. `DiskHealth`: total gravado e lido em GB ou TB decimais.

## 5. Fora do escopo

- D10 (dentro do LHM).
- Ler os timings **configurados** no controlador de memória (registradores do MCHBAR). É leitura nova de hardware, então pede spec próprio.
- Nomear as temperaturas #4 e #6 e as ventoinhas #3 e #4 enquanto o HWiNFO não confirmar a que conector cada uma corresponde.
- Enviar o perfil da placa para o LHM: o upstream aceita PR, mas isso é decisão do dono.

## 6. Critérios de aceite

Os testes de unidade ficam em `tests/ResetExplode.Tests/LeiturasTests.cs`. A medição na máquina usa o `tools\diagnostico.ps1` com o build da branch, contra as referências da tabela.

Estados: **validado** (bate com a referência da linha); **validado por referência alternativa** (bate com `smartctl`, SPD/JEDEC, SMBIOS ou `nvidia-smi`, sem o HWiNFO); **estimado** (plausível, mas sem referência de 2 %); **pendente de HWiNFO** (só o HWiNFO responde).

| Id | Critério | Como mede | Resultado (coleta elevada de 23/11/2026 14:10, build da branch) | Estado |
|---|---|---|---|---|
| D1 | +3,3 V, +12 V e +5 V dentro de ±5 % do nominal e iguais ao HWiNFO (±1 %); nomes Vcore/VCCSA/DIMM; nenhuma "Voltage #n" crua | coleta elevada; teste `Perfil_B360M_Aorus` (escolha do perfil + fórmula com os valores crus medidos) | +3,3 V = 3,344; +12 V = 12,24; +5 V = 5,04 (mín. 4,98); VCCSA 1,200; DIMM A/B 1,248; +3V Standby 3,384; CMOS 3,168; Vcore 0,684–1,284 (o VID máximo foi 1,283). Todos dentro do ATX/JEDEC; 0 linhas "Voltage #n" | **estimado (escala da B360M H)** em todas as 8 tensões; a igualdade de ±1 % com o HWiNFO está **pendente de HWiNFO** |
| D2 | aba RAM mostra 22-22-22-52 @ 3200 e 19-19-19-43 @ 2666 a partir do SPD; HWiNFO "Memory Timings" mostra os configurados | relatório JSON; teste `Spd_CiclosJedec` | "SPD 22-22-22-52 @ 3200 MT/s; na velocidade em uso equivalem a 19-19-19-43 @ 2666" nos 4 módulos (tAA = tRCD = tRP = 13,75 ns, tRAS = 32 ns, iguais nos Micron 8ATF1G64AZ-3G2J1 e nos SK Hynix HMA81GU6CJR8N-XN) | SPD: **validado por referência alternativa** (fórmula JEDEC; o sufixo -3G2/-XN dos part numbers é DDR4-3200). Timings **configurados**: **pendente de HWiNFO** |
| D3 | nominal "DDR4-3200 (SPD)"; o 2666 aparece só como "informada pela BIOS" | relatório JSON | "DDR4-3200 (3200 MT/s, lido do SPD)" e "Velocidade máxima informada pela BIOS (SMBIOS): 2666 MT/s" | **validado por referência alternativa** (SPD + SMBIOS) |
| D4 | Interface "NVMe" nos dois discos | relatório JSON contra `Get-PhysicalDisk` | "NVMe" nos dois | **validado** |
| D5 | Capacidade 1,02 TB e 256,05 GB (base 1000, como o `DiskHealth`); total gravado 105,6 TB e 4,97 TB, iguais ao `smartctl` (±0,1 %) | relatório JSON contra `smartctl`; teste `Unidades_Disco` | capacidade 1,02 TB e 256,05 GB (= `user_capacity` do `smartctl`); gravado 105.640,1 GB contra 105.640,81 GB e 4.970,35 GB contra 4.970,42 GB (`smartctl` lido ~1 min depois; diferença < 0,001 %) | **validado por referência alternativa** (`smartctl`) |
| D6 | "Sim (hypervisor do Windows ativo)" | relatório JSON contra `HypervisorPresent` | "Sim (hypervisor do Windows ativo: Hyper-V/VBS)" | **validado** |
| D7 | nenhum mínimo 0 em temperatura, vida, dados e horas dos NVMe | CSV da coleta; teste `SensorRow_MinMaxProprio` | nenhum mínimo 0 de leitura ausente; o único 0 é "Percentage Used" do ADATA, que o `smartctl` também dá como 0 | **validado por referência alternativa** (`smartctl`) |
| D8 | "CPU Memory" e "CPU Platform" fora da lista; Package e Cores presentes | CSV da coleta; HWiNFO | 0 linhas "CPU Memory/Platform"; Package 15,6 W (12,9–22,2 W) e Cores 9,1 W com a CPU a 4 % | listagem **validada**; o valor do Package (plausível para o i9-9900KF em repouso) é **pendente de HWiNFO** |
| D9 | nenhuma linha "—" nas 12 amostras | CSV da coleta | 0 linhas "—" em 12 amostras | **validado** |
| D11 | sem "Default string" | relatório JSON; teste `Placeholders_Smbios` | 0 ocorrências; "Versão" da placa sai "—" | **validado** |
| D12 | "VID núcleo #n" no lugar de "CPU Core #n" (tensão) | CSV da coleta; HWiNFO "Core VIDs" | "VID (pedido pela CPU)" e "VID núcleo #1–#8" (0,659–1,283 V); 0 linhas "CPU Core #n" de tensão | nomes **validados** (fonte `IntelCpu.cs:504-514`); valores **pendentes de HWiNFO** |
| todos | GPU continua batendo com o `nvidia-smi` (regressão) | coleta da GPU | igual ao `nvidia-smi` na coleta sem elevação das 10:08 com o mesmo build | **validado por referência alternativa** |

O VigiaTPM foi lido antes (430 eventos) e depois (431) da coleta. O evento novo é o `clear_cancelado` da tarefa periódica às 12:51, no mesmo horário do dia anterior (22/11 12:51), fora da janela em que o diagnóstico rodou (14:10–14:11).

## 7. Pendência: validação com o HWiNFO

A memória compartilhada do HWiNFO (`Global\HWiNFO_SENS_SM2`) não apareceu entre 10:10 e 14:10 de 23/11/2026: o HWiNFO estava aberto (PID 24300), mas com "Shared Memory Support" desligado, e o script não altera o INI de um HWiNFO aberto pelo dono. Por decisão do dono, a branch entrou na `main` pelo caminho (b):

- as 8 tensões do perfil `B360M AORUS Gaming 3` têm `Estimated: true` e aparecem como "estimado (escala da B360M H)", com a origem do divisor na dica (teste `Perfil_Estimado`);
- os nomes de temperatura e ventoinha vindos da B360M H não carregam escala e ficam sem a marca;
- o que tem referência fora do HWiNFO foi validado por ela (tabela acima).

Para fechar a pendência: ligar Sensores > Configurações > "Shared Memory Support" no HWiNFO, rodar `tools\diagnostico.ps1` com o build da `main` e comparar canal a canal. O canal que ficar dentro de 2 % passa a `Estimated: false` no `BoardProfiles.cs`. O que ficar fora continua estimado e recebe divisor próprio, num spec novo. Na mesma coleta: os timings configurados (D2), o consumo do Package (D8), os VIDs (D12), os domínios RAPL e os limites de temperatura do NVMe (D10, §5).
