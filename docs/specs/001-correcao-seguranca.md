# Spec 001 — Correção de segurança: G1 (DLL da Corsair) e G2 (OpenRGB)

| Campo | Valor |
|---|---|
| Origem | [Spec 000](000-auditoria-seguranca.md), §2 (portão acionado em 22/11/2026) |
| Decisão do dono | opção 1: corrigir G1 e G2 antes de qualquer execução do app |
| Branch | `fix/seguranca` |
| Data | 22–23/11/2026 |
| Estado | implementado; aceite na §6 |

## 1. Problema

O app roda com `requireAdministrator` ([app.manifest](../../app.manifest)). Dois caminhos punham código não verificado dentro desse processo, ou o abriam com a mesma elevação:

- **G1:** [CorsairSdk.cs](../../CorsairSdk.cs) carregava `iCUESDK.x64_2019.dll` sem conferir a assinatura. A DLL era buscada em `%AppData%\ResetExplode` e na ordem padrão do Windows, e o carregamento acontecia **sozinho ao abrir a aba Integração**. Além disso, [CorsairSdkInstaller.cs](../../CorsairSdkInstaller.cs) aceitava uma DLL sem assinatura mostrando só um aviso.
- **G2:** [MainWindow.Integ.cs](../../MainWindow.Integ.cs) procurava `OpenRGB.exe` em `Downloads\OpenRGB*` e na Área de Trabalho, e o abria elevado sem nenhuma conferência.

## 2. O que já existia (A2)

| Fonte | O que tem | Aproveitado? |
|---|---|---|
| `GustavoReset/ResetExplode` main | nenhum commit depois de `ce1d9b3` (16/10/2026); releases só `latest` e `v0.14.0` | — |
| `EnricoSpanier/ResetExplode` `c5d66be` (MIT, 22/10/2026) "Corsair: carregar a DLL só da pasta do programa e só se assinada pela Corsair" | `Authenticode.Check`: tira `%AppData%` e PATH da busca, confere `WinVerifyTrust` antes do `NativeLibrary.TryLoad` | **a abordagem, sim; o código, não.** O `Authenticode.cs` dele mantém a revogação desligada (`WTD_REVOKE_NONE`), confere o assinante por `Subject.Contains("Corsair")` e relê o arquivo em `X509Certificate.CreateFromSignedFile`, o que abre janela para troca entre conferir e ler o assinante. Também aceita a pasta do .exe mesmo quando ela é gravável pelo usuário (ex.: `C:\Projetos\...\bin\Release`), mantém o carregamento automático ao abrir a aba e não trata o G2 |
| `EnricoSpanier/ResetExplode` `AdminDir.cs` | pasta em `%ProgramData%` com ACL só de Administradores/SYSTEM, recusa pasta com dono estranho | **sim**, adaptado como `AdminFolder.Ensure` (sem apagar a pasta), citado no código |

Conclusão: não havia conferência sólida pronta. Escrevi a nova em [TrustedBinary.cs](../../TrustedBinary.cs), com crédito ao fork do Enrico no cabeçalho.

## 3. Fatos medidos antes de decidir

| Pergunta | Medição (22/11/2026) | Consequência |
|---|---|---|
| A DLL oficial da Corsair é assinada? (portão "a") | `iCUESDK_4.0.84.zip` (release v4.0.84 de `CorsairOfficial/cue-sdk`) → `iCUESDK/redist/x64/iCUESDK.x64_2019.dll`: `Get-AuthenticodeSignature` = **Valid**, `O="Corsair Memory, Inc."`, emissor DigiCert Trusted G4 Code Signing RSA4096 SHA384 2021 CA1 | dá para exigir assinatura; **o portão (a) não disparou** |
| O OpenRGB é assinado? | `OpenRGB.exe` das releases 1.0 e 1.1 (Windows 64): **NotSigned** | só o hash serve |
| O Codeberg publica checksums? | não (corpo da release 1.1 e lista de anexos sem SHA) | hashes calculados por nós a partir dos zips oficiais, com o SHA-256 do zip também registrado |
| O que vem no zip do OpenRGB? | 22 arquivos, **16 binários**: `OpenRGB.exe` + Qt6 (Core, Core5Compat, Gui, OpenGL, Widgets), `PawnIOLib.dll`, `hidapi-hotplug.dll`, `libusb-1.0.dll`, `dxcompiler.dll`, `dxil.dll` e plugins Qt (`platforms\`, `styles\`, `imageformats\`) | conferir só o .exe não basta: trocar `Qt6Core.dll` ao lado dele também dá admin |
| O OpenRGB carrega código de fora da pasta? | `OpenRGB.exe` 1.1 contém `Looking for plugins` e a opção `--config path  Use a custom path instead of the global configuration directory.` | sem `--config`, a pasta de plugins é `%AppData%\OpenRGB\plugins`, e qualquer DLL ali rodaria elevada |

Hashes do OpenRGB (zip oficial → SHA-256):

| Release | Zip | SHA-256 do zip | SHA-256 do `OpenRGB.exe` |
|---|---|---|---|
| 1.1 (16/10/2026) | `OpenRGB_1.1_Windows_64_8e16de2.zip` | `63291726c6fc84eb479ef2ee752e97e1187f6ce9c49fd27eee40ded588a8172a` | `e26ae71162795a8abcfb90c05c1572a00ba06ca63b886aca57392a0138963b1e` |
| 1.0 (12/09/2026) | `OpenRGB_1.0_Windows_64_81bbe18.zip` | `182a52a3c97c4c4ae52c80286b4260c9666c51c3447dbc416ee8945f66192e90` | `86a88b99f60a086e13e6f6ecfb8260a94dbcd598ddeabffda7070d78c97d7e95` |

Os outros 15 binários têm o mesmo hash nas duas versões; a lista completa está em [OpenRgbGuard.cs](../../OpenRgbGuard.cs).

## 4. Correção

### 4.1 Base comum: [TrustedBinary.cs](../../TrustedBinary.cs)

1. **Local só de administrador** (`CheckAdminOnly`, `CheckAdminOnlyTree`). Exige caminho absoluto, disco fixo NTFS e nenhum *reparse point* no caminho. Lê a ACL e recusa se o dono não for Administradores, SYSTEM ou TrustedInstaller, ou se qualquer outro SID tiver uma destas permissões que valham para o objeto:
   - no arquivo: gravar, acrescentar, apagar, trocar permissão ou tomar posse;
   - na pasta do arquivo: criar arquivo ou pasta, apagar filho, apagar, trocar permissão ou tomar posse;
   - nas pastas acima: apagar filho, apagar, trocar permissão ou tomar posse.

   Criar pasta nova em `C:\` é inofensivo e passa. O próprio usuário logado conta como "outro SID", porque é o mesmo SID dos processos dele sem elevação.
2. **Assinatura** (`VerifySignature`): `WinVerifyTrust` (`WINTRUST_ACTION_GENERIC_VERIFY_V2`) sobre o **handle já aberto**, com `WTD_REVOKE_WHOLECHAIN` + `WTD_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT`. O assinante é lido do estado da própria verificação (`WTHelperProvDataFromStateData` → `WTHelperGetProvSignerFromChain`), e o campo `O=` é comparado **por igualdade**. Sem internet, a checagem de revogação falha e a DLL é recusada com a mensagem "não foi possível conferir a revogação".
3. **Carga** (`LoadVerifiedLibrary`): primeiro o local, depois o arquivo é aberto com `FileShare.Read` (ninguém grava, apaga ou renomeia até o fim), depois a assinatura sobre esse handle, e por fim `LoadLibraryExW(path, 0, LOAD_LIBRARY_SEARCH_SYSTEM32)`: as dependências da DLL só são procuradas em System32.

### 4.2 G1 — Corsair

| Arquivo | Mudança |
|---|---|
| [CorsairSdk.cs](../../CorsairSdk.cs) | `DllCandidates()` = só caminhos absolutos em `%ProgramFiles%`: `ResetExplode\`, a pasta do próprio .exe **se** estiver em Program Files, e as pastas de instalação do iCUE (`Corsair\CORSAIR iCUE 5 Software` e variantes). Saíram `%AppData%`, `x64\` relativo e o `NativeLibrary.TryLoad(nome)`. Cada candidato passa por `LoadVerifiedLibrary` com `Publishers = { "Corsair Memory, Inc." }`. Se recusar, a aba mostra o motivo e não carrega |
| [CorsairSdkInstaller.cs](../../CorsairSdkInstaller.cs) | destino passou a ser `%ProgramFiles%\ResetExplode\iCUESDK.x64_2019.dll`. A DLL é extraída **já na pasta trancada** (`.novo`), conferida lá (local + assinatura) e só então renomeada. Sem assinatura, inválida, revogada ou de outro fabricante, o arquivo é apagado e a instalação falha. O "seguir só com um aviso" foi removido |
| [IntegrationPolicy.cs](../../IntegrationPolicy.cs) (novo) | decide o que cada gatilho conecta. `TabOpened` nunca inclui a Corsair; só `CorsairActivateClicked` inclui. A ativação vale para a sessão e não é gravada |
| [MainWindow.Integ.cs](../../MainWindow.Integ.cs), [MainWindow.xaml](../../MainWindow.xaml) | `InitInteg` conecta só OpenRGB e Razer (sockets em localhost). Botão novo **ATIVAR CONTROLE CORSAIR**: é o único caminho até `LoadDll` |

Os candidatos dentro de `Corsair\` saíram da documentação do iCUE, mas não foram conferidos nesta máquina porque o iCUE não está instalado. Se a DLL não estiver lá, o caminho oficial continua sendo o botão BAIXAR O SDK.

### 4.3 G2 — OpenRGB: [OpenRgbGuard.cs](../../OpenRgbGuard.cs)

1. **Seletor de arquivo** (`PickOpenRgb`): você escolhe o `OpenRGB.exe` e o caminho fica salvo em `AppSettings.OpenRgbPath`. A busca em `Downloads`/Área de Trabalho foi removida.
2. **Manifesto completo** (`VerifyFolder`): o SHA-256 do .exe identifica a release (1.0 ou 1.1). Depois, **todo** binário da pasta e das subpastas (`.dll .exe .sys .ocx .cpl .drv .scr .com .node .pyd .ax .efi`) precisa estar no manifesto dessa release, com o hash igual, e todos os 16 precisam estar presentes. Uma DLL faltando seria procurada no PATH.
3. **Pasta só de admin**: se a pasta escolhida puder ser alterada sem elevação (o caso de quem descompacta em Downloads), o app pede confirmação e **copia** os 16 binários conferidos para `%ProgramFiles%\ResetExplode\OpenRGB\<versão>`. Lá ele confere de novo (ACL da árvore + manifesto) e só executa a partir dessa cópia. Isso fecha a janela entre conferir e executar: depois da conferência, ninguém sem admin troca nada.
4. **A cada abertura** (`Launch`): confere de novo a ACL da árvore e os 16 hashes. Se algo não bater, recusa sem chamar `Process.Start`. Executa com `UseShellExecute = false` e com os argumentos `--server --startminimized --config %ProgramData%\ResetExplode\OpenRGB`, uma pasta trancada por `AdminFolder.Ensure`. Assim os plugins só podem vir de uma pasta de admin.

**Custo aceito:** o OpenRGB aberto pelo ResetExplode usa uma configuração própria, separada da que você usa ao abri-lo à mão (`%AppData%\OpenRGB`). Perfis criados de um lado não aparecem do outro.

### 4.4 Workflow (A1)

[publicar.yml](../../.github/workflows/publicar.yml) passou a ter só `workflow_dispatch`. Antes, cada push na `main` apagava e recriava a release `latest`. Registrado no spec 000 (A6).

## 5. Fora do escopo (continua aberto no spec 000)

- **A1/A2:** `DriverManager.VerifyPublisher` ainda usa o WinVerifyTrust antigo (sem revogação) e confere o assinante com `Subject.Contains` ao validar o instalador da NVIDIA/AMD. A mesma `TrustedBinary.VerifySignature` resolve, mas exige medir o `O=` real dos instaladores de cada fabricante antes de trocar.
- **A3** (jogos elevados), **A4** (winget em pasta do usuário), **A5** e **A7**.

## 6. Critérios de aceite

Testes em [tests/ResetExplode.Tests/SegurancaTests.cs](../../tests/ResetExplode.Tests/SegurancaTests.cs). Rodam sem elevação: `dotnet test tests/ResetExplode.Tests -c Release`.

| Id | Critério | Teste |
|---|---|---|
| G1-a | DLL sem assinatura é recusada (`TRUST_E_NOSIGNATURE`) e o instalador descarta em vez de avisar | `DllSemAssinatura_Recusada`, `Instalador_RecusaSemAssinatura` |
| G1-a | assinatura válida de outro fabricante é recusada pelo `O=` exato | `OutroFabricante_Recusado` |
| G1-b | uma DLL **validamente assinada**, copiada para `%TEMP%` com o nome `iCUESDK.x64_2019.dll`, não é carregada, e o motivo citado é a permissão | `CaminhoGravavel_Ignorado` |
| G1-b | todos os candidatos estão em Program Files; `%AppData%` reprova na ACL; o código não usa `AppSettings.Folder`, `NativeLibrary.TryLoad` nem o aviso antigo | `Candidatos_SoProgramFiles`, `Guarda_FonteCorsair` |
| G1-c | abrir a aba não conecta a Corsair; só `CorsairActivateClicked` conecta; criar a ponte não tenta carregar | `AbrirAba_NaoCarregaDll` |
| G1-c | na fonte, `InitInteg` usa `TabOpened` e não chama `LoadDll`; `LoadDll(` aparece uma vez e `ConnectCorsairAsync` só roda atrás de `plan.Corsair` | `Guarda_FonteDaAba` |
| G1 | controle positivo: binário assinado (coreclr.dll, Microsoft) em Program Files passa na ACL, na assinatura e carrega | `ControlePositivo_CarregaDeLocalAdmin` |
| G2-a | `OpenRGB.exe` com hash desconhecido é recusado, e `Launch` lança exceção antes do `Process.Start` | `OpenRgb_HashErrado_Recusado` |
| G2-b | .exe certo com DLL plantada ao lado (`version.dll`), DLL alterada ou DLL faltando é recusado; pasta do usuário reprova na ACL | `OpenRgb_PastaAdulterada_Recusada` |
| G2 | manifesto: 1.0 e 1.1, 16 binários cada, hashes bem formados | `Manifesto_Oficial` |
| G2 | na fonte, não há busca em Downloads/Área de Trabalho e a abertura passa por `OpenRgbGuard.Launch` | `Guarda_FonteOpenRgb` |

**Pendente de teste manual (com o app elevado):** abrir a aba Integração e conferir que `iCUESDK` não aparece nos módulos do processo; escolher um OpenRGB 1.1 em Downloads e ver a cópia para Program Files.
