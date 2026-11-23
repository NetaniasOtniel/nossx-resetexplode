using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using Xunit;

namespace ResetExplode.Tests;

/// <summary>Critérios de aceite do spec 001 (docs/specs/001-correcao-seguranca.md). Rodam sem elevação.</summary>
public sealed class SegurancaTests : IDisposable
{
    private readonly string _tmp = Directory.CreateTempSubdirectory("re-seg-").FullName; // %TEMP%: gravável pelo usuário

    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { } }

    private static readonly string[] Microsoft = { "Microsoft Corporation" };

    /// <summary>
    /// DLL nativa da Microsoft com assinatura embutida, dentro de Program Files\dotnet (só admin grava). Se o teste rodar num
    /// SDK instalado no perfil do usuário, usa o coreclr.dll do runtime de Program Files; sem nenhum, o teste falha (não pula).
    /// </summary>
    private static string CoreClr
    {
        get
        {
            string mine = Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "coreclr.dll");
            if (TrustedBinary.CheckAdminOnly(mine) == null) return mine;
            string shared = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "shared", "Microsoft.NETCore.App");
            var found = Directory.Exists(shared)
                ? Directory.GetDirectories(shared).Select(d => Path.Combine(d, "coreclr.dll")).Where(File.Exists).OrderByDescending(p => p).FirstOrDefault()
                : null;
            return found ?? throw new InvalidOperationException("nenhum coreclr.dll em Program Files\\dotnet para o controle positivo");
        }
    }

    private static string RepoFile(string name)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "ResetExplode.csproj"))) return Path.Combine(d.FullName, name);
        throw new FileNotFoundException("raiz do repositório não encontrada");
    }

    [DllImport("kernel32.dll")] private static extern bool FreeLibrary(IntPtr h);

    // ------------------------------------------------------------------ G1-a: DLL sem assinatura recusada

    [Fact(DisplayName = "G1-a · DLL sem assinatura é recusada pelo WinVerifyTrust")]
    public void DllSemAssinatura_Recusada()
    {
        string unsigned = typeof(CorsairSdk).Assembly.Location; // ResetExplode.dll compilado aqui: PE sem assinatura
        var r = TrustedBinary.VerifySignature(unsigned, CorsairSdk.Publishers);
        Assert.False(r.Ok);
        Assert.Equal(TrustedBinary.TRUST_E_NOSIGNATURE, r.Code);
        Assert.Contains("sem assinatura", r.Message);
    }

    [Fact(DisplayName = "G1-a · instalador do SDK recusa DLL sem assinatura (sem 'seguir com aviso')")]
    public void Instalador_RecusaSemAssinatura()
    {
        string copy = Path.Combine(_tmp, CorsairSdk.DllFile);
        File.Copy(typeof(CorsairSdk).Assembly.Location, copy);
        var ex = Assert.Throws<InvalidOperationException>(() => CorsairSdkInstaller.Verify(copy));
        Assert.Contains("descartado", ex.Message);
    }

    [Fact(DisplayName = "G1-a · assinatura válida de outro fabricante é recusada (O= exato, não Contains)")]
    public void OutroFabricante_Recusado()
    {
        var r = TrustedBinary.VerifySignature(CoreClr, CorsairSdk.Publishers);
        Assert.False(r.Ok);
        Assert.Equal(0, r.Code); // a assinatura em si é válida…
        Assert.Equal("Microsoft Corporation", r.Organization);
        Assert.Contains("outro fabricante", r.Message); // …mas não é da Corsair
    }

    [Fact(DisplayName = "G1 · controle positivo: binário assinado, em local só de admin, carrega")]
    public void ControlePositivo_CarregaDeLocalAdmin()
    {
        Assert.Null(TrustedBinary.CheckAdminOnly(CoreClr));
        Assert.True(TrustedBinary.VerifySignature(CoreClr, Microsoft).Ok);
        IntPtr h = TrustedBinary.LoadVerifiedLibrary(CoreClr, Microsoft, out var why);
        Assert.True(h != IntPtr.Zero, why);
        FreeLibrary(h);
    }

    // ------------------------------------------------------------------ G1-b: caminho gravável pelo usuário ignorado

    [Fact(DisplayName = "G1-b · DLL assinada e válida, mas em pasta gravável pelo usuário, é recusada pela regra de CAMINHO")]
    public void CaminhoGravavel_Ignorado()
    {
        string planted = Path.Combine(_tmp, CorsairSdk.DllFile);
        File.Copy(CoreClr, planted);
        // Dono explícito = o usuário do teste (com ou sem elevação), para a regra de caminho recusar sempre pelo mesmo motivo.
        var acl = new FileInfo(planted).GetAccessControl();
        acl.SetOwner(WindowsIdentity.GetCurrent().User!);
        new FileInfo(planted).SetAccessControl(acl);
        Assert.True(TrustedBinary.VerifySignature(planted, Microsoft).Ok); // a assinatura continua boa na cópia: só o caminho reprova

        string expected = $"o dono de {planted} não é Administradores/SYSTEM/TrustedInstaller";
        Assert.Equal(expected, TrustedBinary.CheckAdminOnly(planted));
        Assert.Equal(IntPtr.Zero, TrustedBinary.LoadVerifiedLibrary(planted, Microsoft, out var why));
        Assert.Equal(expected, why);

        using var sdk = new CorsairSdk();
        Assert.Equal($"Recusei carregar a DLL da Corsair. {planted}: {expected}", sdk.LoadDll(new[] { planted }));
        Assert.False(sdk.DllLoaded);
    }

    // ------------------------------------------------------------------ G1-a isolado: regra de ASSINATURA com o caminho já aprovado
    // Sem elevação não dá para criar pasta com dono Administradores; a regra de caminho é injetada (sobrecarga internal) e
    // aprova só a pasta temporária do teste. O contador prova que ela rodou e aprovou, então quem recusa é o WinVerifyTrust.

    private (Func<string, string?> rule, Func<int> calls) AllowOnlyTmp()
    {
        int n = 0;
        string dir = Path.GetFullPath(_tmp);
        return (p => { n++; return string.Equals(Path.GetDirectoryName(Path.GetFullPath(p)), dir, StringComparison.OrdinalIgnoreCase) ? null : "fora da pasta permitida do teste"; },
                () => n);
    }

    [Fact(DisplayName = "G1-a isolado · DLL SEM ASSINATURA em caminho permitido é recusada pela regra de assinatura")]
    public void CaminhoPermitido_SemAssinatura_RecusadaPelaAssinatura()
    {
        string dll = Path.Combine(_tmp, CorsairSdk.DllFile);
        File.Copy(typeof(CorsairSdk).Assembly.Location, dll); // ResetExplode.dll: PE sem assinatura
        var (rule, calls) = AllowOnlyTmp();

        Assert.Equal(IntPtr.Zero, TrustedBinary.LoadVerifiedLibrary(dll, CorsairSdk.Publishers, rule, out var why));
        Assert.Equal(1, calls());
        Assert.Equal("sem assinatura digital", why); // mensagem do TRUST_E_NOSIGNATURE, não a de caminho

        using var sdk = new CorsairSdk();
        Assert.Equal($"Recusei carregar a DLL da Corsair. {dll}: sem assinatura digital", sdk.LoadDll(new[] { dll }, rule));
        Assert.False(sdk.DllLoaded);
        Assert.Equal(2, calls());
    }

    [Fact(DisplayName = "G1-a isolado · DLL assinada por OUTRO FABRICANTE (Microsoft) em caminho permitido é recusada pelo critério Corsair")]
    public void CaminhoPermitido_OutroFabricante_RecusadaPeloAssinante()
    {
        string dll = Path.Combine(_tmp, CorsairSdk.DllFile);
        File.Copy(CoreClr, dll); // coreclr.dll renomeado: assinatura Microsoft válida
        var (rule, calls) = AllowOnlyTmp();
        Assert.True(TrustedBinary.VerifySignature(dll, Microsoft).Ok); // a assinatura em si é válida

        Assert.Equal(IntPtr.Zero, TrustedBinary.LoadVerifiedLibrary(dll, CorsairSdk.Publishers, rule, out var why));
        Assert.Equal(1, calls());
        Assert.Equal("assinado por outro fabricante (O=Microsoft Corporation)", why);

        using var sdk = new CorsairSdk();
        Assert.Equal($"Recusei carregar a DLL da Corsair. {dll}: assinado por outro fabricante (O=Microsoft Corporation)",
                     sdk.LoadDll(new[] { dll }, rule));
        Assert.False(sdk.DllLoaded);
    }

    [Fact(DisplayName = "G1-a isolado · DLL OFICIAL da Corsair em caminho permitido é aceita e carregada (requer RESETEXPLODE_CORSAIR_DLL)")]
    public void CaminhoPermitido_DllOficialCorsair_Aceita()
    {
        string? real = Environment.GetEnvironmentVariable("RESETEXPLODE_CORSAIR_DLL");
        Assert.False(string.IsNullOrEmpty(real), "defina RESETEXPLODE_CORSAIR_DLL com o iCUESDK.x64_2019.dll do iCUESDK_4.0.84.zip oficial");
        string dll = Path.Combine(_tmp, CorsairSdk.DllFile);
        File.Copy(real!, dll);
        var (rule, calls) = AllowOnlyTmp();

        using var sdk = new CorsairSdk();
        Assert.Null(sdk.LoadDll(new[] { dll }, rule)); // null = carregou e achou as exportações do SDK
        Assert.True(sdk.DllLoaded);
        Assert.Equal(dll, sdk.LoadedFrom);
        Assert.Equal(1, calls());
        // A DLL fica no processo até o fim (CorsairSdk não descarrega): a pasta temporária pode sobrar em %TEMP%.
    }

    [Fact(DisplayName = "G1-b · %AppData%, %TEMP%, perfil e PATH estão fora da busca da DLL")]
    public void Candidatos_SoProgramFiles()
    {
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) + "\\";
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var c in CorsairSdk.DllCandidates())
        {
            Assert.True(Path.IsPathFullyQualified(c), c);
            Assert.StartsWith(pf, c, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(profile, c, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Null(TrustedBinary.CheckAdminOnly(CoreClr)); // Program Files de verdade passa
        Assert.NotNull(TrustedBinary.CheckAdminOnly(Path.Combine(AppSettings.Folder, "x.dll"))); // %AppData% não
    }

    [Fact(DisplayName = "G1-b · guarda de fonte: nenhuma busca em AppData, PATH ou NativeLibrary.TryLoad por nome")]
    public void Guarda_FonteCorsair()
    {
        string src = File.ReadAllText(RepoFile("CorsairSdk.cs"));
        Assert.DoesNotContain("AppSettings.Folder", src);
        Assert.DoesNotContain("NativeLibrary.TryLoad", src);
        Assert.DoesNotContain("NativeLibrary.Load(", src);
        Assert.Contains("TrustedBinary.LoadVerifiedLibrary", src);
        string inst = File.ReadAllText(RepoFile("CorsairSdkInstaller.cs"));
        Assert.DoesNotContain("Aviso: a DLL não tem assinatura", inst);
    }

    [Fact(DisplayName = "G1 · DLL oficial da Corsair (4.0.84): assinatura aceita, mas recusada fora de Program Files (requer RESETEXPLODE_CORSAIR_DLL)")]
    public void DllOficialCorsair_AssinaturaAceita_LocalRecusado()
    {
        // A DLL da Corsair não é versionada (licença do SDK). Sem a variável, este teste FALHA de propósito para não passar vazio.
        string? real = Environment.GetEnvironmentVariable("RESETEXPLODE_CORSAIR_DLL");
        Assert.False(string.IsNullOrEmpty(real), "defina RESETEXPLODE_CORSAIR_DLL com o iCUESDK.x64_2019.dll do iCUESDK_4.0.84.zip oficial");
        var sig = TrustedBinary.VerifySignature(real!, CorsairSdk.Publishers);
        Assert.True(sig.Ok, sig.Message);
        Assert.Equal("Corsair Memory, Inc.", sig.Organization);

        string planted = Path.Combine(_tmp, CorsairSdk.DllFile);
        File.Copy(real!, planted);
        using var sdk = new CorsairSdk();
        Assert.StartsWith("Recusei", sdk.LoadDll(new[] { planted }));
        Assert.False(sdk.DllLoaded);
    }

    // ------------------------------------------------------------------ G1-c: abrir a aba não carrega a DLL

    [Fact(DisplayName = "G1-c · abrir a aba Integração não conecta a Corsair; só o clique em ATIVAR")]
    public void AbrirAba_NaoCarregaDll()
    {
        Assert.False(IntegrationPolicy.For(IntegrationTrigger.TabOpened, corsairActivated: false).Corsair);
        Assert.False(IntegrationPolicy.For(IntegrationTrigger.TabOpened, corsairActivated: true).Corsair);
        Assert.False(IntegrationPolicy.For(IntegrationTrigger.ConnectAllClicked, corsairActivated: false).Corsair);
        Assert.False(IntegrationPolicy.For(IntegrationTrigger.CorsairSdkInstalled, corsairActivated: false).Corsair);
        Assert.True(IntegrationPolicy.For(IntegrationTrigger.CorsairActivateClicked, corsairActivated: false).Corsair);

        using var sdk = new CorsairSdk(); // criar a ponte não carrega nada
        Assert.Equal(0, sdk.LoadAttempts);
        Assert.False(sdk.DllLoaded);
    }

    [Fact(DisplayName = "G1-c · guarda de fonte: InitInteg usa o plano TabOpened e LoadDll só é chamado por ConnectCorsairAsync")]
    public void Guarda_FonteDaAba()
    {
        string src = File.ReadAllText(RepoFile("MainWindow.Integ.cs"));
        int a = src.IndexOf("private void InitInteg()", StringComparison.Ordinal);
        int b = src.IndexOf("ControlTemplate SwatchTemplate()", a, StringComparison.Ordinal);
        Assert.True(a > 0 && b > a, "InitInteg não encontrado");
        string init = src[a..b];
        Assert.Contains("IntegrationTrigger.TabOpened", init);
        Assert.DoesNotContain("LoadDll", init);
        Assert.DoesNotContain("ConnectCorsairAsync", init);

        Assert.Equal(1, Count(src, "LoadDll("));
        Assert.Equal(2, Count(src, "ConnectCorsairAsync(")); // a definição + a chamada única
        Assert.Contains("if (plan.Corsair) tasks.Add(ConnectCorsairAsync());", src);
        Assert.Equal(1, Count(src, "IntegrationTrigger.CorsairActivateClicked"));
    }

    private static int Count(string s, string sub)
    {
        int n = 0;
        for (int i = s.IndexOf(sub, StringComparison.Ordinal); i >= 0; i = s.IndexOf(sub, i + sub.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    // ------------------------------------------------------------------ G2: OpenRGB

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    [Fact(DisplayName = "G2-a · OpenRGB.exe com hash que não é de release oficial é recusado")]
    public void OpenRgb_HashErrado_Recusado()
    {
        string exe = Path.Combine(_tmp, "OpenRGB.exe");
        File.WriteAllBytes(exe, RandomNumberGenerator.GetBytes(4096));
        string? why = OpenRgbGuard.VerifyFolder(exe, out var rel);
        Assert.Null(rel);
        Assert.Contains("não é de nenhuma release oficial", why);

        var ex = Assert.Throws<InvalidOperationException>(() => OpenRgbGuard.Launch(exe)); // não chega no Process.Start
        Assert.StartsWith("Recusei abrir o OpenRGB", ex.Message);
    }

    [Fact(DisplayName = "G2-b · .exe certo com DLL a mais, DLL alterada ou DLL faltando é recusado")]
    public void OpenRgb_PastaAdulterada_Recusada()
    {
        byte[] exeBytes = RandomNumberGenerator.GetBytes(2048), qtBytes = RandomNumberGenerator.GetBytes(2048);
        var fake = new[] { new OpenRgbRelease("teste", "x.zip", "", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            { ["OpenRGB.exe"] = Sha(exeBytes), [@"platforms\qwindows.dll"] = Sha(qtBytes) }) };
        string exe = Path.Combine(_tmp, "OpenRGB.exe"), qt = Path.Combine(_tmp, "platforms", "qwindows.dll");
        File.WriteAllBytes(exe, exeBytes);
        Directory.CreateDirectory(Path.GetDirectoryName(qt)!);

        Assert.Contains("falta", OpenRgbGuard.VerifyFolder(exe, out _, fake));          // DLL faltando (iria para o PATH)
        File.WriteAllBytes(qt, qtBytes);
        Assert.Null(OpenRgbGuard.VerifyFolder(exe, out var ok, fake));                   // tudo idêntico
        Assert.Equal("teste", ok!.Version);

        File.WriteAllBytes(Path.Combine(_tmp, "version.dll"), RandomNumberGenerator.GetBytes(64));
        Assert.Contains("version.dll", OpenRgbGuard.VerifyFolder(exe, out _, fake));    // DLL plantada ao lado
        File.Delete(Path.Combine(_tmp, "version.dll"));

        File.WriteAllBytes(qt, RandomNumberGenerator.GetBytes(2048));
        Assert.Contains("foi alterado", OpenRgbGuard.VerifyFolder(exe, out _, fake));   // DLL trocada

        Assert.NotNull(TrustedBinary.CheckAdminOnlyTree(_tmp)); // e a pasta, sendo do usuário, nunca é aceita para executar
    }

    [Fact(DisplayName = "G2 · manifesto oficial: 1.0 e 1.1, 16 binários cada, SHA-256 bem formado")]
    public void Manifesto_Oficial()
    {
        Assert.Equal(new[] { "1.1", "1.0" }, OpenRgbGuard.Official.Select(r => r.Version));
        foreach (var r in OpenRgbGuard.Official)
        {
            Assert.Equal(16, r.Files.Count);
            Assert.All(r.Files.Values.Append(r.ZipSha256), h => Assert.Matches("^[0-9a-f]{64}$", h));
        }
        Assert.NotEqual(OpenRgbGuard.Official[0].Files["OpenRGB.exe"], OpenRgbGuard.Official[1].Files["OpenRGB.exe"]);
    }

    [Fact(DisplayName = "G2 · guarda de fonte: nada de procurar em Downloads/Área de Trabalho; abrir passa pelo OpenRgbGuard")]
    public void Guarda_FonteOpenRgb()
    {
        string src = File.ReadAllText(RepoFile("MainWindow.Integ.cs"));
        Assert.DoesNotContain("Downloads", src);
        Assert.DoesNotContain("DesktopDirectory", src);
        Assert.DoesNotContain("FindOpenRgb", src);
        Assert.Contains("OpenRgbGuard.Launch(", src);
        Assert.DoesNotContain("--server --startminimized\"", src);
    }
}
