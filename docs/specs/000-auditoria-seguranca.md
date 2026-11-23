# Spec 000 — Auditoria de segurança do ResetExplode v0.16 (antes de executar)

| Campo | Valor |
|---|---|
| Base auditada | `GustavoReset/ResetExplode` @ `ce1d9b3` (main, 16/10/2026), fork `NetaniasOtniel/nossx-resetexplode` |
| Data | 22/11/2026 |
| Método | leitura estática do código-fonte (14.104 linhas C#), varredura de rede, processo, registro, remoção e carga dinâmica; checagem Authenticode dos binários versionados; inspeção do pacote NuGet do LibreHardwareMonitorLib |
| Nada foi executado | nem build, nem o app. Só `Get-AuthenticodeSignature` (leitura) e o download do `.nupkg` para inspeção |
| Veredito | **PORTÃO ACIONADO**: 2 caminhos de execução de binário não verificado dentro de um processo com privilégio de administrador (G1, G2). Nenhuma rede para destino desconhecido. Nenhum código ofuscado. |
| Estado (23/11/2026) | **G1 e G2 CORRIGIDOS** pelo [spec 001](001-correcao-seguranca.md): `a2547ef` (correção + 14 testes), `1789e73` (ajuste de teste), merge `0403bf6` na `main`. **A6 resolvido** em `d528e8f` (workflow só `workflow_dispatch`). Testes 14/14 com o runtime de `Program Files\dotnet` e a DLL oficial da Corsair 4.0.84. |

## 1. Contexto

O app declara `requireAdministrator` em [app.manifest](../../app.manifest) (linha 6): todo código dele, e tudo que ele carrega ou abre com `UseShellExecute`, roda **elevado**. Por isso o critério da auditoria é "o que entra no processo ou é lançado por ele, e de onde vem".

Arquivos citados no pedido que **não existem** na v0.16: `AdminDir.cs` (nenhuma classe ou referência com esse nome no repo) e o app não tem `AppInstaller` separado do catálogo winget, que está em [AppInstaller.cs](../../AppInstaller.cs). [App.xaml.cs](../../App.xaml.cs) é inofensivo: só um mutex de instância única e `FindWindow`/`SetForegroundWindow`.

## 2. Achados que acionam o portão

### G1 — DLL da Corsair carregada sem verificação, de pasta gravável pelo usuário (CRÍTICO)

- [CorsairSdk.cs:85-93](../../CorsairSdk.cs): `LoadDll()` procura `iCUESDK.x64_2019.dll` (ou `iCUESDK.dll`) na pasta do .exe, em `x64\`, em `%AppData%\ResetExplode` e, por fim, `NativeLibrary.TryLoad("iCUESDK.x64_2019.dll")` na ordem padrão de busca de DLL do Windows. **Nenhuma checagem de assinatura antes de carregar.**
- [MainWindow.Integ.cs:78](../../MainWindow.Integ.cs) e [:132](../../MainWindow.Integ.cs): isso acontece **sozinho na primeira vez que a aba Integração é aberta** (`_ = ConnectAllAsync()` → `_cue.LoadDll()`). Não precisa clicar em nada.
- [CorsairSdkInstaller.cs:118](../../CorsairSdkInstaller.cs): o botão "BAIXAR O SDK DA CORSAIR" aceita DLL **sem assinatura** (`TRUST_E_NOSIGNATURE`) só com um aviso e copia para `%AppData%\ResetExplode`.
- **Por que importa:** `%AppData%` é gravável por qualquer processo do usuário sem elevação. Um malware sem privilégio que plante esse nome de arquivo ali ganha execução como administrador na próxima vez que alguém abrir a aba. É um caminho clássico de escalada de privilégio (DLL planting).
- ✅ **Corrigido** em `a2547ef` (merge `0403bf6`), spec 001 §4.1–4.2: a DLL só sai de `Program Files` (arquivo, pasta e ancestrais sem direito de escrita para não-admin); `WinVerifyTrust` com revogação na cadeia inteira e assinante `O=Corsair Memory, Inc.` por igualdade exata; `LoadLibraryExW(..., LOAD_LIBRARY_SEARCH_SYSTEM32)` com o arquivo travado contra escrita entre a conferência e a carga; abrir a aba não carrega a DLL (só o botão **ATIVAR CONTROLE CORSAIR**); o instalador recusa DLL sem assinatura válida e grava só em `%ProgramFiles%\ResetExplode`. `%AppData%`, `x64\` e a ordem padrão de busca saíram.

### G2 — OpenRGB.exe de Downloads/Área de Trabalho aberto elevado sem verificação (ALTO)

- [MainWindow.Integ.cs:223-247](../../MainWindow.Integ.cs): `FindOpenRgb()` aceita `OpenRGB.exe` de `Program Files`, `%LocalAppData%\Programs`, e de **qualquer subpasta `OpenRGB*` em `Downloads` ou na Área de Trabalho**.
- [MainWindow.Integ.cs:264](../../MainWindow.Integ.cs): o botão "ABRIR O OPENRGB" faz `Process.Start(exe, "--server --startminimized")` com `UseShellExecute = true` a partir do processo elevado. **O filho herda o administrador. Sem checagem de assinatura.**
- **Por que importa:** qualquer `OpenRGB.exe` largado numa pasta `Downloads\OpenRGB-qualquercoisa\` vira código de administrador ao clicar no botão. Exige clique (menos grave que G1), mas é exatamente "binário não verificado executado".
- ✅ **Corrigido** em `a2547ef` (merge `0403bf6`), spec 001 §4.3: sem busca em `Downloads`/Área de Trabalho; o dono escolhe o `OpenRGB.exe` num seletor e o caminho fica salvo; antes de cada execução elevada, os **16 binários** da pasta são conferidos por SHA-256 contra o manifesto das releases oficiais 1.0 e 1.1 gravado no código (binário extra ou faltando também recusa); pasta gravável pelo usuário é copiada para `%ProgramFiles%\ResetExplode\OpenRGB\<versão>` e conferida de novo lá; o OpenRGB abre com `--config` numa pasta trancada de `%ProgramData%` (ele carrega plugins da pasta de configuração).

## 3. Rede: todos os destinos são conhecidos

| Destino | Arquivo:linha | Quando | O que sai da máquina |
|---|---|---|---|
| `api.github.com/repos/CorsairOfficial/cue-sdk` + anexos da release | [CorsairSdkInstaller.cs:36](../../CorsairSdkInstaller.cs), :62 | botão, com confirmação | nada além do User-Agent |
| `www.nvidia.com/Download/API/lookupValueSearch.aspx`, `gfwsl.geforce.com/...AjaxDriverService.php` | [DriverManager.cs:179](../../DriverManager.cs), :270, :278 | **automático ao abrir a aba Drivers** ([MainWindow.Driver.cs:19](../../MainWindow.Driver.cs)) | modelo da GPU (ids psid/pfid) |
| URL do instalador devolvida pela NVIDIA | [DriverManager.cs:192](../../DriverManager.cs), :297 | botão | — (host **não** é validado; a proteção é só a assinatura, ver A2) |
| `www.amd.com/...` e `drivers.amd.com/drivers/*.exe` | [DriverManager.cs:215-222](../../DriverManager.cs) | aba Drivers, placa AMD | modelo da GPU |
| `howmanyfps.com/api` | [HowManyFpsClient.cs:16](../../HowManyFpsClient.cs), :55-63 | só com chave de API configurada | CPU, GPU, jogo, resolução e a chave (em `Authorization` **e** `x-api-key`) |
| `speed.cloudflare.com/__down` e `/__up`, ping, DNS `www.cloudflare.com` | [NetworkTest.cs:39](../../NetworkTest.cs), :70, :100, :167 | botão do teste de rede | 25 MB de lixo aleatório no upload |
| `localhost:6742` (OpenRGB SDK), `localhost:54235` (Razer Chroma) | [LightBackends.cs:178](../../LightBackends.cs), :302, :344 | aba Integração | cores de LED |
| Páginas de suporte (NVIDIA, AMD, Intel, ASUS, MSI, Gigabyte, ASRock, Dell, Lenovo, HP, Acer, WCH, SiLabs, FTDI, Prolific, Arduino, openrgb.org) | [DriverManager.cs:151-392](../../DriverManager.cs), [MakerDrivers.cs:66-74](../../MakerDrivers.cs) | só abre no navegador | — |

Não há telemetria, atualização automática nem envio de relatório. O app **não faz nenhuma chamada de rede ao abrir**; a primeira sai ao entrar na aba Drivers.

## 4. Execução de processos

| Arquivo:linha | O que roda | Verificação | Risco |
|---|---|---|---|
| [DriverManager.cs:321](../../DriverManager.cs) | instalador NVIDIA/AMD baixado | `WinVerifyTrust` + assinante contém "NVIDIA"/"Advanced Micro Devices" ([:316](../../DriverManager.cs), [:325-347](../../DriverManager.cs)) | médio, ver A1/A2 |
| [DriverManager.cs:353](../../DriverManager.cs) | `powershell Checkpoint-Computer` (ponto de restauração) | comando fixo | baixo |
| [DriverManager.cs:443](../../DriverManager.cs) | `pnputil /export-driver *` | fixo | baixo |
| [AppInstaller.cs:100](../../AppInstaller.cs) | `winget install --id <id fixo do catálogo> --silent` | winget confere hash do instalador | baixo (ver A4) |
| [MainWindow.xaml.cs:236](../../MainWindow.xaml.cs) | `cmd /k winget install --id namazso.PawnIO` | id fixo | baixo |
| [MakerDrivers.cs:335](../../MakerDrivers.cs) | `pnputil` (add/remove/delete/scan) | fixo | ver §6 |
| [GameLauncher.cs:99](../../GameLauncher.cs), [WindowsTweaks.cs:222](../../WindowsTweaks.cs) | `powercfg` | fixo | baixo |
| [GameLauncher.cs:168](../../GameLauncher.cs), :182 | programas auxiliares e o .exe do jogo **com argumentos**, escolhidos pelo usuário | nenhuma | médio: abrem **elevados**. Só o caso sem argumentos usa `explorer.exe` para perder o admin (:181) |
| [MainWindow.Integ.cs:264](../../MainWindow.Integ.cs) | OpenRGB.exe | nenhuma | **G2** |

## 5. Escrita no registro

Todas com backup em `AppSettings.TweakBackup` ou em arquivo, e com rotina de restauração, exceto onde indicado.

| Chave | Arquivo:linha | Recurso |
|---|---|---|
| **HKLM** `SYSTEM\CurrentControlSet\Control\COM Name Arbiter\ComDB` (binário) | [UsbCleaner.cs:152-166](../../UsbCleaner.cs) | limpeza de portas COM; backup em memória + `RestoreComDb` |
| **HKLM** `SYSTEM\CurrentControlSet\Control\GraphicsDrivers\HwSchMode` | [WindowsTweaks.cs:89](../../WindowsTweaks.cs) | agendamento de GPU por hardware (exige reinício) |
| **HKLM/HKCU** `...\CurrentVersion\Run` e `...\Explorer\StartupApproved\*` | [StartupManager.cs:122-169](../../StartupManager.cs) | inicialização; remover grava backup em `%AppData%\ResetExplode\Startup` |
| HKCU `Software\Microsoft\GameBar\AutoGameModeEnabled`, `System\GameConfigStore\GameDVR_Enabled`, `...\GameDVR\AppCaptureEnabled` | [WindowsTweaks.cs:67-83](../../WindowsTweaks.cs) | Modo de Jogo / Game DVR |
| HKCU `Software\Microsoft\DirectX\UserGpuPreferences` (`DirectXUserGlobalSettings` e por .exe) | [WindowsTweaks.cs:100-111](../../WindowsTweaks.cs), [GameLauncher.cs:78-80](../../GameLauncher.cs) | janela otimizada / GPU dedicada por jogo |
| HKCU `...\AppCompatFlags\Layers` (por .exe) | [GameLauncher.cs:87-92](../../GameLauncher.cs) | desativar otimização de tela cheia |
| HKCU `Control Panel\Mouse` (`MouseSpeed`, `MouseThreshold1/2`) | [WindowsTweaks.cs:201-205](../../WindowsTweaks.cs) | aceleração do mouse |

Leitura só: `...\Uninstall` ([AppInstaller.cs:68](../../AppInstaller.cs)).

## 6. Remoção de driver, dispositivo e arquivo

| Arquivo:linha | Ação | Proteção |
|---|---|---|
| [UsbCleaner.cs:93-112](../../UsbCleaner.cs) | `SetupDiCallClassInstaller(DIF_REMOVE)` em dispositivos fantasmas | recusa qualquer dispositivo presente (:107) |
| [MakerDrivers.cs:145](../../MakerDrivers.cs) | `pnputil /remove-device "<id>"` + `/scan-devices` ("reinstalar") | o Windows redetecta |
| [MakerDrivers.cs:181-183](../../MakerDrivers.cs) | `pnputil /delete-driver <oemNN.inf> /uninstall /force` | exporta o pacote antes e **aborta se o backup falhar** (:182) |
| [MakerDrivers.cs:193](../../MakerDrivers.cs), :266 | `pnputil /add-driver ... /install` (restaurar backup / driver embutido) | o Windows exige assinatura do pacote |
| [MakerDrivers.cs:318-326](../../MakerDrivers.cs) | apaga `%LocalAppData%\Arduino15\*_index*.json*` e `staging\` | escopo fixo |
| [DiskBench.cs:104](../../DiskBench.cs), [MemoryBench.cs:160](../../MemoryBench.cs), [CorsairSdkInstaller.cs:93](../../CorsairSdkInstaller.cs), [MakerDrivers.cs:256](../../MakerDrivers.cs) | arquivos e pastas temporárias criadas pelo próprio app | escopo fixo |
| [DriverManager.cs:330](../../DriverManager.cs), :344 | apaga o instalador baixado se a assinatura falhar | — |
| [Optimizer.cs:110-130](../../Optimizer.cs) | `CloseMainWindow` e **`Process.Kill()`** em apps "sugeridos" sem janela (OneDrive, Teams, Dropbox, Google Drive, atualizadores Adobe/Office) | lista fixa; mata sem salvar |
| [Optimizer.cs:150](../../Optimizer.cs), :161 | `EmptyWorkingSet` em todos os processos + `NtSetSystemInformation(MemoryPurgeStandbyList)` | — |

## 7. Pasta `Drivers/` (embutida no .exe como recurso)

Os 33 binários assinados (`.cat`, `.sys`, `.dll`) de CH340, CP210x, FTDI e Arduino têm **assinatura Authenticode válida**, conferida com `Get-AuthenticodeSignature` em 22/11/2026: 26 por "Microsoft Windows Hardware Compatibility Publisher" (WHQL), 4 `silabser.sys` por "Silicon Laboratories Inc." e 4 `.cat` do Arduino por "Arduino LLC"/"Arduino srl". Os `.inf` não são assinados por natureza (vão pelo `.cat`). Só são extraídos e instalados pelo botão da aba RESET MAKER.

## 8. Escrita em hardware (fora do escopo desta fase, não será usada)

- [Nvml.cs:53-54](../../Nvml.cs), [:89-90](../../Nvml.cs): `nvmlDeviceSetPowerManagementLimit` e `nvmlDeviceSetGpuLockedClocks`, chamados pela aba RESET OC ([MainWindow.Oc.cs:156](../../MainWindow.Oc.cs), :207).
- O código não escreve em MSR, porta de E/S nem PCI diretamente; isso fica dentro do LibreHardwareMonitorLib, que no uso do app só lê.

## 9. Dependências

- **LibreHardwareMonitorLib 0.9.6** (MPL-2.0, commit `3d331e3`, publicado em 14/02/2026, pacote com assinatura NuGet `.signature.p7s`). Usa **PawnIO** (não traz WinRing0: zero ocorrências na DLL `net8.0`). Puxa `DiskInfoToolkit 1.1.2`, `RAMSPDToolkit-NDD 1.4.2` e `HidSharp 2.6.4` (terceiros, transitivos).
- `Microsoft.Diagnostics.Tracing.TraceEvent 3.2.8` (ETW para FPS), `System.Management`, `ProtectedData` (DPAPI da chave HowManyFPS), `Vortice.*` 3.8.3 (Direct3D para benchmarks).
- `Native\iCUESDK.x64_2019.dll`: copiado para a saída **se existir** ([ResetExplode.csproj](../../ResetExplode.csproj)); não está versionado.

## 10. Código ofuscado

Nenhum. `Convert.FromBase64String` aparece só em [AppSettings.cs:87](../../AppSettings.cs)/:96 para guardar a chave da HowManyFPS com DPAPI. Não há `Assembly.Load`, `Reflection.Emit`, strings hexadecimais nem payload codificado. A única carga dinâmica de código nativo é a do G1 (`NativeLibrary` + `GetDelegateForFunctionPointer`).

## 11. Achados abaixo do portão

| Id | Gravidade | Achado | Onde |
|---|---|---|---|
| A1 | médio | `WinVerifyTrust` roda com revogação desligada (`WTD_REVOKE_NONE` + `WTD_REVOCATION_CHECK_NONE`): certificado revogado passa | [CorsairSdkInstaller.cs:177](../../CorsairSdkInstaller.cs), :181 |
| A2 | médio | assinante conferido por `Subject.Contains("NVIDIA")`: qualquer certificado válido com "NVIDIA" no nome passa, e o host do download não é validado | [DriverManager.cs:192](../../DriverManager.cs), :342; [CorsairSdkInstaller.cs:130](../../CorsairSdkInstaller.cs) |
| A3 | médio | jogos e auxiliares com argumentos são abertos elevados | [GameLauncher.cs:168](../../GameLauncher.cs), :182 |
| A4 | baixo | `winget.exe` executado elevado a partir de `%LocalAppData%\Microsoft\WindowsApps` (pasta do usuário; é o alias oficial, mas o caminho é gravável sem elevação) | [AppInstaller.cs:59-60](../../AppInstaller.cs) |
| A5 | baixo | chave da HowManyFPS enviada em dois cabeçalhos ao mesmo tempo | [HowManyFpsClient.cs:60-61](../../HowManyFpsClient.cs) |
| A6 | operacional | o workflow `publicar.yml` **apaga e recria a release `latest`** a cada push na `main`. No fork, o Actions está habilitado no nível do repositório; se o workflow for ativado, o push da etapa 8 publica um .exe no fork. ✅ **Resolvido** em `d528e8f`: gatilho só `workflow_dispatch` (nenhum push nem tag publica) | [.github/workflows/publicar.yml:44-45](../../.github/workflows/publicar.yml) |
| A7 | operacional | o "Otimizar" mata processos com `Kill()` sem salvar | [Optimizer.cs:122](../../Optimizer.cs) |

## 12. Regras para as próximas etapas (enquanto G1 e G2 estiverem abertos)

> G1 e G2 fechados em 23/11/2026 (merge `0403bf6`): a regra 1 deixa de valer; as regras 2 e 3 continuam valendo para o diagnóstico.

1. ~~Não abrir a aba **Integração** (dispara o G1 sozinha) nem clicar em "ABRIR O OPENRGB".~~
2. Não usar: stress test, RESET OC, limpeza de portas COM, remoção/reinstalação de drivers, Otimizar, ajustes do Windows, gerenciador de inicialização, instalador de apps, download de driver.
3. A aba Drivers faz uma consulta automática à NVIDIA ao abrir: aceitável (destino conhecido, só envia o modelo da GPU), mas evitável no diagnóstico.

## 13. Correção proposta para G1 e G2 (histórico)

> O dono escolheu a opção 1 em 22/11/2026, com exigências mais fortes que esta proposta (Authenticode também para a pasta do .exe, hash do OpenRGB em vez de abrir sem elevação). A correção aplicada é a do [spec 001](001-correcao-seguranca.md); o texto abaixo fica como registro.

- **G1:** em `LoadDll`, procurar só na pasta do .exe (instalação controlada pelo admin) e em `%AppData%` **apenas se** `WinVerifyTrust == 0` e o assinante for a Corsair; remover o `NativeLibrary.TryLoad(nome)` da ordem padrão; no instalador, recusar DLL sem assinatura em vez de só avisar.
- **G2:** procurar o OpenRGB só em `Program Files`, e abrir sempre sem elevação (via `explorer.exe`, como o próprio app já faz no [GameLauncher.cs:181](../../GameLauncher.cs)).
- **Critério de aceite:** uma DLL qualquer com o nome `iCUESDK.x64_2019.dll` copiada para `%AppData%\ResetExplode` não é carregada (a aba mostra "DLL sem assinatura da Corsair"); um `OpenRGB.exe` em `Downloads\OpenRGB-x\` não é encontrado.
