using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace ResetExplode;

/// <summary>Resultado da conferência Authenticode de um arquivo.</summary>
public sealed record SignatureCheck(bool Ok, int Code, string Message, string? Organization);

/// <summary>
/// Regras para um binário entrar no processo elevado ou ser aberto por ele (spec 001, G1/G2):
/// 1) o arquivo e as pastas acima dele só podem ser alterados por Administradores, SYSTEM ou TrustedInstaller;
/// 2) a assinatura Authenticode é conferida pelo Windows com revogação na cadeia inteira, e o assinante é comparado pelo
///    campo O= exato do certificado que o próprio WinVerifyTrust validou (nada de Subject.Contains);
/// 3) a DLL é carregada com LoadLibraryEx(LOAD_LIBRARY_SEARCH_SYSTEM32), com o arquivo aberto sem compartilhar escrita
///    do começo da conferência até o fim da carga.
/// Base: a ideia de conferir antes de carregar vem do commit c5d66be de EnricoSpanier/ResetExplode (MIT); a checagem de dono e
/// ACL segue o AdminDir.cs do mesmo fork. A conferência de local, de revogação e de O= exato é deste fork.
/// </summary>
public static class TrustedBinary
{
    // ------------------------------------------------------------------ 1. local só de administrador

    private static readonly SecurityIdentifier[] TrustedSids =
    {
        new(WellKnownSidType.BuiltinAdministratorsSid, null),
        new(WellKnownSidType.LocalSystemSid, null),
        new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"), // NT SERVICE\TrustedInstaller
        new("S-1-3-4"), // OWNER RIGHTS: vale para o dono, que também é exigido confiável
    };

    private const int GenericAll = 0x10000000, GenericWrite = 0x40000000;

    /// <summary>O que ninguém fora de admin/SYSTEM pode ter no próprio arquivo.</summary>
    private const FileSystemRights FileForbidden =
        FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete |
        FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    /// <summary>Na pasta do arquivo (e nas subpastas de uma árvore conferida): não pode criar, apagar nem renomear nada.</summary>
    private const FileSystemRights FolderForbidden =
        FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories | FileSystemRights.DeleteSubdirectoriesAndFiles |
        FileSystemRights.Delete | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    /// <summary>Nas pastas acima: não pode apagar/renomear o caminho nem trocar permissões. Criar pasta nova ao lado é inofensivo
    /// (é o caso de C:\, onde "Usuários autenticados" criam pastas).</summary>
    private const FileSystemRights AncestorForbidden =
        FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.Delete |
        FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    private static bool IsTrusted(SecurityIdentifier sid) => TrustedSids.Any(t => t.Equals(sid));

    private static string Name(SecurityIdentifier sid)
    {
        try { return sid.Translate(typeof(NTAccount)).Value; } catch { return sid.Value; }
    }

    /// <summary>Devolve null se só administradores conseguem alterar <paramref name="filePath"/>; senão, o motivo.</summary>
    public static string? CheckAdminOnly(string filePath)
    {
        string? why = CheckLocalPath(filePath, out string full);
        if (why != null) return why;
        var fi = new FileInfo(full);
        if (!fi.Exists) return $"{full} não existe";
        return CheckObject(fi, FileForbidden) ?? CheckAncestors(fi.Directory!, FolderForbidden);
    }

    /// <summary>Confere a pasta, todas as subpastas e todos os arquivos dentro dela, e as pastas acima.</summary>
    public static string? CheckAdminOnlyTree(string folder)
    {
        string? why = CheckLocalPath(folder, out string full);
        if (why != null) return why;
        var di = new DirectoryInfo(full);
        if (!di.Exists) return $"{full} não existe";
        why = CheckAncestors(di, FolderForbidden);
        if (why != null) return why;
        foreach (var sub in di.EnumerateDirectories("*", SearchOption.AllDirectories))
            if ((why = CheckObject(sub, FolderForbidden)) != null) return why;
        foreach (var f in di.EnumerateFiles("*", SearchOption.AllDirectories))
            if ((why = CheckObject(f, FileForbidden)) != null) return why;
        return null;
    }

    private static string? CheckLocalPath(string path, out string full)
    {
        full = path;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\"))
            return $"\"{path}\" não é um caminho absoluto num disco local";
        full = Path.GetFullPath(path);
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(full)!);
            if (drive.DriveType != DriveType.Fixed) return $"{full} não está num disco fixo";
            if (!string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase)) return $"{full} não está num disco NTFS (sem permissões)";
        }
        catch (Exception ex) { return $"não consegui conferir o disco de {full}: {ex.Message}"; }
        return null;
    }

    /// <summary>A pasta imediata recebe <paramref name="first"/>; as de cima, <see cref="AncestorForbidden"/>.</summary>
    private static string? CheckAncestors(DirectoryInfo dir, FileSystemRights first)
    {
        var rights = first;
        for (var d = dir; d != null; d = d.Parent)
        {
            string? why = CheckObject(d, rights);
            if (why != null) return why;
            rights = AncestorForbidden;
        }
        return null;
    }

    private static string? CheckObject(FileSystemInfo fsi, FileSystemRights forbidden)
    {
        try
        {
            bool isRoot = fsi is DirectoryInfo { Parent: null };
            if (!isRoot && (fsi.Attributes & FileAttributes.ReparsePoint) != 0) return $"{fsi.FullName} é um link (reparse point)";
            FileSystemSecurity sec = fsi is DirectoryInfo d ? d.GetAccessControl() : ((FileInfo)fsi).GetAccessControl();
            if (sec.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !IsTrusted(owner))
                return $"o dono de {fsi.FullName} não é Administradores/SYSTEM/TrustedInstaller";
            foreach (FileSystemAccessRule r in sec.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (r.AccessControlType != AccessControlType.Allow) continue;
                if ((r.PropagationFlags & PropagationFlags.InheritOnly) != 0) continue; // só vale para filhos, não para este objeto
                var sid = (SecurityIdentifier)r.IdentityReference;
                if (IsTrusted(sid)) continue;
                int mask = (int)r.FileSystemRights;
                if ((mask & (int)forbidden) != 0 || (mask & (GenericAll | GenericWrite)) != 0)
                    return $"{Name(sid)} pode alterar {fsi.FullName} sem ser administrador";
            }
            return null;
        }
        catch (Exception ex) { return $"não consegui ler as permissões de {fsi.FullName}: {ex.Message}"; }
    }

    // ------------------------------------------------------------------ 2. Authenticode

    public const int TRUST_E_NOSIGNATURE = unchecked((int)0x800B0100);

    /// <summary>Confere o arquivo pelo caminho (abre e fecha o próprio handle).</summary>
    public static SignatureCheck VerifySignature(string path, IReadOnlyCollection<string> organizations)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return VerifySignature(fs.SafeFileHandle, path, organizations);
    }

    /// <summary>
    /// WinVerifyTrust (WINTRUST_ACTION_GENERIC_VERIFY_V2) sobre o handle já aberto, revogação em toda a cadeia menos a raiz.
    /// O assinante vem do estado da própria verificação, então não há segunda leitura do arquivo.
    /// </summary>
    public static SignatureCheck VerifySignature(SafeFileHandle handle, string path, IReadOnlyCollection<string> organizations)
    {
        var file = new WINTRUST_FILE_INFO { pcwszFilePath = path, hFile = handle.DangerousGetHandle() };
        file.cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>();
        IntPtr pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        Marshal.StructureToPtr(file, pFile, false);
        var data = new WINTRUST_DATA
        {
            dwUIChoice = WTD_UI_NONE,
            fdwRevocationChecks = WTD_REVOKE_WHOLECHAIN,
            dwUnionChoice = WTD_CHOICE_FILE,
            pFile = pFile,
            dwStateAction = WTD_STATEACTION_VERIFY,
            dwProvFlags = WTD_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT,
        };
        data.cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>();
        bool refAdded = false;
        handle.DangerousAddRef(ref refAdded);
        try
        {
            int code = WinVerifyTrust(new IntPtr(-1), GenericVerifyV2, ref data);
            if (code != 0) return new SignatureCheck(false, code, Explain(code), null);

            string? org = null;
            IntPtr prov = WTHelperProvDataFromStateData(data.hWVTStateData);
            IntPtr sgnr = prov == IntPtr.Zero ? IntPtr.Zero : WTHelperGetProvSignerFromChain(prov, 0, false, 0);
            if (sgnr != IntPtr.Zero)
            {
                var s = Marshal.PtrToStructure<CRYPT_PROVIDER_SGNR>(sgnr);
                if (s.csCertChain > 0 && s.pasCertChain != IntPtr.Zero)
                {
                    var c = Marshal.PtrToStructure<CRYPT_PROVIDER_CERT>(s.pasCertChain);
                    if (c.pCert != IntPtr.Zero) { using var cert = new X509Certificate2(c.pCert); org = Organization(cert); }
                }
            }
            if (org == null) return new SignatureCheck(false, code, "não consegui ler o assinante do certificado validado", null);
            bool ok = organizations.Any(o => string.Equals(o, org, StringComparison.OrdinalIgnoreCase));
            return new SignatureCheck(ok, code, ok ? $"assinatura válida de {org}" : $"assinado por outro fabricante (O={org})", org);
        }
        finally
        {
            data.dwStateAction = WTD_STATEACTION_CLOSE;
            WinVerifyTrust(new IntPtr(-1), GenericVerifyV2, ref data);
            if (refAdded) handle.DangerousRelease();
            Marshal.DestroyStructure<WINTRUST_FILE_INFO>(pFile);
            Marshal.FreeHGlobal(pFile);
        }
    }

    /// <summary>Campo O= (organização) do certificado, exato.</summary>
    public static string? Organization(X509Certificate2 cert)
    {
        foreach (var rdn in cert.SubjectName.EnumerateRelativeDistinguishedNames())
            if (rdn.GetSingleElementType().Value == "2.5.4.10") return rdn.GetSingleElementValue();
        return null;
    }

    private static string Explain(int code) => code switch
    {
        TRUST_E_NOSIGNATURE => "sem assinatura digital",
        unchecked((int)0x800B0003) => "não é um arquivo assinável (formato desconhecido)",
        unchecked((int)0x80096010) => "o arquivo foi alterado depois de assinado",
        unchecked((int)0x800B010C) => "o certificado do assinante foi revogado",
        unchecked((int)0x80092013) or unchecked((int)0x800B010E) => "não foi possível conferir a revogação do certificado (sem internet?)",
        unchecked((int)0x800B0109) => "a cadeia termina numa raiz não confiável",
        unchecked((int)0x800B0101) => "o certificado venceu e a assinatura não tem carimbo de tempo",
        unchecked((int)0x800B0111) => "o assinante está bloqueado no Windows",
        _ => $"assinatura digital inválida (0x{code:X8})",
    };

    // ------------------------------------------------------------------ 3. carga

    public const uint LOAD_LIBRARY_SEARCH_SYSTEM32 = 0x00000800;

    /// <summary>
    /// Carrega a DLL só se passar no local e na assinatura. Devolve o HMODULE (ou zero, com o motivo em <paramref name="problem"/>).
    /// Dependências da DLL só são procuradas em System32.
    /// </summary>
    public static IntPtr LoadVerifiedLibrary(string path, IReadOnlyCollection<string> organizations, out string? problem)
    {
        problem = CheckAdminOnly(path);
        if (problem != null) return IntPtr.Zero;
        // FileShare.Read: ninguém grava, apaga ou renomeia o arquivo entre a conferência e a carga.
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var sig = VerifySignature(fs.SafeFileHandle, path, organizations);
        if (!sig.Ok) { problem = sig.Message; return IntPtr.Zero; }
        IntPtr h = LoadLibraryExW(path, IntPtr.Zero, LOAD_LIBRARY_SEARCH_SYSTEM32);
        if (h == IntPtr.Zero) problem = $"o Windows recusou carregar a DLL (erro {Marshal.GetLastWin32Error()})";
        return h;
    }

    // ------------------------------------------------------------------ P/Invoke

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
    private const uint WTD_UI_NONE = 2, WTD_REVOKE_WHOLECHAIN = 1, WTD_CHOICE_FILE = 1;
    private const uint WTD_STATEACTION_VERIFY = 1, WTD_STATEACTION_CLOSE = 2;
    private const uint WTD_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT = 0x80;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CRYPT_PROVIDER_SGNR
    {
        public uint cbStruct;
        public System.Runtime.InteropServices.ComTypes.FILETIME sftVerifyAsOf;
        public uint csCertChain;
        public IntPtr pasCertChain;
        public uint dwSignerType;
        public IntPtr psSigner;
        public uint dwError;
        public uint csCounterSigners;
        public IntPtr pasCounterSigners;
        public IntPtr pChainContext;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CRYPT_PROVIDER_CERT
    {
        public uint cbStruct;
        public IntPtr pCert;
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID, ref WINTRUST_DATA pWVTData);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperProvDataFromStateData(IntPtr hStateData);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr pProvData, uint idxSigner, [MarshalAs(UnmanagedType.Bool)] bool fCounterSigner, uint idxCounterSigner);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr LoadLibraryExW(string lpLibFileName, IntPtr hFile, uint dwFlags);
}

/// <summary>
/// Pasta em local fixo que só Administradores/SYSTEM gravam (sem herança). Adaptado de AdminDir.Locked, de
/// EnricoSpanier/ResetExplode (MIT): aqui a pasta NÃO é apagada, só criada ou trancada.
/// </summary>
public static class AdminFolder
{
    private static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier System_ = new(WellKnownSidType.LocalSystemSid, null);

    public static string Ensure(string path)
    {
        var di = new DirectoryInfo(path);
        if (di.Exists)
        {
            var owner = di.GetAccessControl().GetOwner(typeof(SecurityIdentifier));
            if (!Admins.Equals(owner) && !System_.Equals(owner))
                throw new InvalidOperationException($"A pasta {path} pertence a outro usuário; recusei usá-la.");
        }
        var sec = new DirectorySecurity();
        sec.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { Admins, System_ })
            sec.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        sec.SetOwner(Admins);
        if (di.Exists) di.SetAccessControl(sec); else di.Create(sec);
        return path;
    }
}
