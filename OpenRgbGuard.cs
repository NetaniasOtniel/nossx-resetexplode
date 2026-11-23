using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;

namespace ResetExplode;

/// <summary>Uma release oficial do OpenRGB para Windows 64 bits: SHA-256 de cada binário do zip, pelo caminho relativo.</summary>
public sealed record OpenRgbRelease(string Version, string ZipName, string ZipSha256, IReadOnlyDictionary<string, string> Files);

/// <summary>
/// G2 (spec 001): o OpenRGB só é aberto (elevado, porque o SMBus da placa-mãe e da RAM exige administrador) se:
/// 1) o .exe e TODOS os binários da pasta forem idênticos, byte a byte, aos da release oficial (SHA-256 gravado aqui), sem
///    nenhum .dll/.exe a mais e sem faltar nenhum (DLL faltando seria procurada no PATH);
/// 2) a pasta inteira só puder ser alterada por administrador. Um OpenRGB escolhido em Downloads é conferido e COPIADO para
///    %ProgramFiles%\ResetExplode\OpenRGB\&lt;versão&gt;, conferido de novo lá e executado só de lá;
/// 3) a configuração (e com ela a pasta de plugins, que o OpenRGB carrega como DLL) fica em %ProgramData%\ResetExplode\OpenRGB,
///    trancada para administradores, via --config. Sem isso, qualquer DLL em %AppData%\OpenRGB\plugins rodaria como admin.
/// O OpenRGB não assina o .exe (Get-AuthenticodeSignature = NotSigned nas releases 1.0 e 1.1), por isso o hash.
/// </summary>
public static class OpenRgbGuard
{
    // Hashes calculados em 22/11/2026 a partir dos zips oficiais baixados de
    // https://codeberg.org/OpenRGB/OpenRGB/releases/download/<tag>/<zip>. O Codeberg não publica checksums.
    private static readonly Dictionary<string, string> CommonFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PawnIOLib.dll"] = "c6e7c2f54643ef9cb51b9a3ec7b0efc9e204803c206f33014e39f281546ea9b4",
        ["Qt6Core.dll"] = "8632f4a9f57355680f1b0de5013eb4657b1038b0cedc7c0534db827f73162014",
        ["Qt6Core5Compat.dll"] = "0d949a077144cfe2fecd4a030a3a6fbf0d651501e1b39bbdaaa11f56c74c25a2",
        ["Qt6Gui.dll"] = "1c6ab7a785ade7a8964f67cce21e6099b1209301498726019189ea0023e6f24c",
        ["Qt6OpenGL.dll"] = "bfb1be764c0e9d75682319b14d8ba0493d89a52d1680e1cc914525ea1ad944fb",
        ["Qt6Widgets.dll"] = "baf97dfdb7fe72a14e1675f37b21cc8634c7ec26cc9082f4883aa7492292a1db",
        ["dxcompiler.dll"] = "6ec587d1952778a41a5a90647b39d0c86958e7dabf87fa1cf2b14408bfe1e8de",
        ["dxil.dll"] = "a0cfaf3e930894e9b5d48efcacaed6ace5c645bad3c4e99b4a23c9831a078120",
        ["hidapi-hotplug.dll"] = "263a662b014a7082871bb3e46f4d3c4790d7ed253768dcd585264fd598892dfb",
        [@"imageformats\qgif.dll"] = "9cb2a7645576e72db54f957e9431b6145daf55bb83f4879cc01f864d4af96621",
        [@"imageformats\qico.dll"] = "41038802daf3a01d4701f88e2c7730c41a9e7e63fa8c985e2ef4a9a9370b2d6e",
        [@"imageformats\qjpeg.dll"] = "c6d100f40b051a1750cda26c46b66694b8854eae01de45a0d4510b779b5ea88d",
        ["libusb-1.0.dll"] = "a8c91f0ff68fb7802a9f4416728f0eeb4d99af4ceaa4ef7dfe9374e76e375018",
        [@"platforms\qwindows.dll"] = "d858a41e1617052f113442ea755696bc6dbe8030e64266e3ff3dc27fddea6bef",
        [@"styles\qmodernwindowsstyle.dll"] = "518118bbe1eba484b431a5eff1a212a1ffe8b92305db18a3476a6a6217a78c9f",
    };

    private static Dictionary<string, string> With(string exeSha) =>
        new(CommonFiles, StringComparer.OrdinalIgnoreCase) { ["OpenRGB.exe"] = exeSha };

    public static readonly IReadOnlyList<OpenRgbRelease> Official = new[]
    {
        new OpenRgbRelease("1.1", "OpenRGB_1.1_Windows_64_8e16de2.zip",
            "63291726c6fc84eb479ef2ee752e97e1187f6ce9c49fd27eee40ded588a8172a",
            With("e26ae71162795a8abcfb90c05c1572a00ba06ca63b886aca57392a0138963b1e")),
        new OpenRgbRelease("1.0", "OpenRGB_1.0_Windows_64_81bbe18.zip",
            "182a52a3c97c4c4ae52c80286b4260c9666c51c3447dbc416ee8945f66192e90",
            With("86a88b99f60a086e13e6f6ecfb8260a94dbcd598ddeabffda7070d78c97d7e95")),
    };

    /// <summary>Extensões que o Windows executa ou carrega como código.</summary>
    private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".dll", ".exe", ".sys", ".ocx", ".cpl", ".drv", ".scr", ".com", ".node", ".pyd", ".ax", ".efi" };

    public static string InstallRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ResetExplode", "OpenRGB");

    public static string ConfigFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ResetExplode", "OpenRGB");

    public static string Sha256(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    /// <summary>
    /// Confere o .exe e a pasta contra as releases conhecidas. Devolve null e a release se tudo bate; senão, o motivo.
    /// Não olha permissões: isso é <see cref="TrustedBinary.CheckAdminOnlyTree"/>.
    /// </summary>
    public static string? VerifyFolder(string exePath, out OpenRgbRelease? release, IReadOnlyList<OpenRgbRelease>? known = null)
    {
        release = null;
        known ??= Official;
        if (!File.Exists(exePath)) return $"{exePath} não existe";
        if (!string.Equals(Path.GetFileName(exePath), "OpenRGB.exe", StringComparison.OrdinalIgnoreCase))
            return "o arquivo escolhido não se chama OpenRGB.exe";

        string exeSha;
        try { exeSha = Sha256(exePath); } catch (Exception ex) { return $"não consegui ler {exePath}: {ex.Message}"; }
        release = known.FirstOrDefault(r => r.Files.TryGetValue("OpenRGB.exe", out var h) && h == exeSha);
        if (release == null)
            return $"o OpenRGB.exe não é de nenhuma release oficial conhecida (SHA-256 {exeSha[..16]}…). Versões aceitas: " +
                   string.Join(", ", known.Select(r => r.Version)) + ", baixadas de codeberg.org/OpenRGB ou openrgb.org.";

        string dir = Path.GetDirectoryName(Path.GetFullPath(exePath))!;
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            if (!CodeExtensions.Contains(Path.GetExtension(f))) continue;
            string rel = Path.GetRelativePath(dir, f);
            if (!release.Files.TryGetValue(rel, out var expected))
            { var r0 = release; release = null; return $"a pasta do OpenRGB {r0.Version} tem um binário que não faz parte da release oficial: {rel}"; }
            string got;
            try { got = Sha256(f); } catch (Exception ex) { release = null; return $"não consegui ler {rel}: {ex.Message}"; }
            if (got != expected) { var r0 = release; release = null; return $"{rel} foi alterado (não confere com a release oficial {r0.Version})"; }
        }
        foreach (var rel in release.Files.Keys)
            if (!File.Exists(Path.Combine(dir, rel))) { var r0 = release; release = null; return $"falta {rel} da release oficial {r0.Version}"; }
        return null;
    }

    /// <summary>Copia os binários conferidos para a pasta só de admin e confere de novo lá. Devolve o novo OpenRGB.exe.</summary>
    public static string ImportToAdminFolder(string exePath)
    {
        string? why = VerifyFolder(exePath, out var rel);
        if (why != null || rel == null) throw new InvalidOperationException(why);
        string src = Path.GetDirectoryName(Path.GetFullPath(exePath))!;
        string dst = Path.Combine(InstallRoot, rel.Version);
        Directory.CreateDirectory(InstallRoot); // dentro de Program Files: herda a ACL que só deixa admin gravar
        if (Directory.Exists(dst)) Directory.Delete(dst, true);
        foreach (var relPath in rel.Files.Keys)
        {
            string to = Path.Combine(dst, relPath);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(Path.Combine(src, relPath), to, true);
        }
        string newExe = Path.Combine(dst, "OpenRGB.exe");
        // A cópia pode ter sido trocada no caminho (a origem é gravável): o que vale é o que chegou na pasta trancada.
        why = TrustedBinary.CheckAdminOnlyTree(dst) ?? VerifyFolder(newExe, out _);
        if (why != null) { try { Directory.Delete(dst, true); } catch { } throw new InvalidOperationException("a cópia não passou na conferência: " + why); }
        return newExe;
    }

    /// <summary>Confere tudo de novo e abre o servidor do OpenRGB. Lança exceção com o motivo se recusar.</summary>
    public static Process? Launch(string exePath)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(exePath))!;
        string? why = TrustedBinary.CheckAdminOnlyTree(dir) ?? VerifyFolder(exePath, out _);
        if (why != null) throw new InvalidOperationException("Recusei abrir o OpenRGB: " + why);
        string cfg = AdminFolder.Ensure(ConfigFolder);
        var psi = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            WorkingDirectory = dir,
        };
        foreach (var a in new[] { "--server", "--startminimized", "--config", cfg }) psi.ArgumentList.Add(a);
        return Process.Start(psi);
    }
}
