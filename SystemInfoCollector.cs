using System.Management;

namespace ResetExplode;

/// <summary>Coleta informações estáticas do sistema via WMI.</summary>
public static class SystemInfoCollector
{
    public static List<InfoNode> Collect()
    {
        var roots = new List<InfoNode>();
        Safe(roots, "Sistema operacional", CollectOs);
        Safe(roots, "Placa-mãe e BIOS", CollectBoard);
        Safe(roots, "Processador", CollectCpu);
        Safe(roots, "Memória RAM", CollectRam);
        Safe(roots, "Placa de vídeo", CollectGpu);
        Safe(roots, "Armazenamento", CollectDisks);
        return roots;
    }

    private static void Safe(List<InfoNode> roots, string title, Action<InfoNode> fill)
    {
        var node = new InfoNode { Header = title };
        try { fill(node); }
        catch (Exception ex) { node.Add("Erro ao ler", ex.Message); }
        roots.Add(node);
    }

    private static IEnumerable<ManagementObject> Query(string wql)
    {
        using var searcher = new ManagementObjectSearcher(wql);
        foreach (ManagementObject mo in searcher.Get())
            yield return mo;
    }

    private static string S(ManagementObject mo, string prop) => Clean(mo[prop]?.ToString());

    /// <summary>Spec 002, D11: textos de preenchimento que fabricantes deixam no SMBIOS viram "—".</summary>
    internal static string Clean(string? s)
    {
        s = s?.Trim();
        if (string.IsNullOrEmpty(s)) return "—";
        return SmbiosPlaceholders.Contains(s) ? "—" : s;
    }

    private static readonly HashSet<string> SmbiosPlaceholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Default string", "To be filled by O.E.M.", "To Be Filled By O.E.M.", "System Product Name", "System manufacturer",
        "System Version", "Not Specified", "Not Applicable", "Base Board Version", "x.x", "None",
    };

    /// <summary>Spec 002, D5: tamanho de disco em base 1000, como no rótulo do produto, no smartctl e no TBW.</summary>
    internal static string DiskBytes(object? v)
    {
        if (v == null) return "—";
        double b = Convert.ToDouble(v);
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        int i = 0;
        while (b >= 1000 && i < u.Length - 1) { b /= 1000; i++; }
        return $"{b:0.##} {u[i]}";
    }

    private static string Bytes(object? v)
    {
        if (v == null) return "—";
        double b = Convert.ToDouble(v);
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        int i = 0;
        while (b >= 1024 && i < u.Length - 1) { b /= 1024; i++; }
        return $"{b:0.##} {u[i]}";
    }

    private static void CollectOs(InfoNode n)
    {
        foreach (var os in Query("SELECT * FROM Win32_OperatingSystem"))
        {
            n.Add("Nome", S(os, "Caption"));
            n.Add("Versão", S(os, "Version"));
            n.Add("Build", S(os, "BuildNumber"));
            n.Add("Arquitetura", S(os, "OSArchitecture"));
            n.Add("Nome do computador", Environment.MachineName);
        }
    }

    private static void CollectBoard(InfoNode n)
    {
        var board = n.Add("Placa-mãe");
        foreach (var b in Query("SELECT * FROM Win32_BaseBoard"))
        {
            board.Add("Fabricante", S(b, "Manufacturer"));
            board.Add("Modelo", S(b, "Product"));
            board.Add("Versão", S(b, "Version"));
            board.Add("Nº de série", S(b, "SerialNumber"));
        }
        var bios = n.Add("BIOS");
        foreach (var b in Query("SELECT * FROM Win32_BIOS"))
        {
            bios.Add("Fabricante", S(b, "Manufacturer"));
            bios.Add("Versão", S(b, "SMBIOSBIOSVersion"));
            bios.Add("Data", ManagementDateTimeConverter.ToDateTime(b["ReleaseDate"].ToString()).ToString("dd/MM/yyyy"));
        }
    }

    private static void CollectCpu(InfoNode n)
    {
        foreach (var c in Query("SELECT * FROM Win32_Processor"))
        {
            var cpu = n.Add(S(c, "Name"));
            cpu.Add("Fabricante", S(c, "Manufacturer"));
            cpu.Add("Soquete", S(c, "SocketDesignation"));
            cpu.Add("Núcleos físicos", S(c, "NumberOfCores"));
            cpu.Add("Threads (lógicos)", S(c, "NumberOfLogicalProcessors"));
            cpu.Add("Clock base/máx. declarado", S(c, "MaxClockSpeed") + " MHz");
            cpu.Add("Cache L2", S(c, "L2CacheSize") + " KB");
            cpu.Add("Cache L3", S(c, "L3CacheSize") + " KB");
            cpu.Add("Virtualização habilitada", Virtualization(c["VirtualizationFirmwareEnabled"] as bool?, HypervisorPresent()));
        }
    }

    private static bool? HypervisorPresent()
    {
        try { foreach (var cs in Query("SELECT HypervisorPresent FROM Win32_ComputerSystem")) return cs["HypervisorPresent"] as bool?; }
        catch { }
        return null;
    }

    /// <summary>
    /// Spec 002, D6: com Hyper-V/VBS ativo o Windows devolve VirtualizationFirmwareEnabled = False mesmo com VT-x/AMD-V ligado;
    /// um hypervisor rodando é a prova de que está ligado.
    /// </summary>
    internal static string Virtualization(bool? firmwareEnabled, bool? hypervisorPresent) => (firmwareEnabled, hypervisorPresent) switch
    {
        (_, true) => "Sim (hypervisor do Windows ativo: Hyper-V/VBS)",
        (true, _) => "Sim",
        (false, false) => "Não (desligada na BIOS ou sem suporte)",
        _ => "—",
    };

    private static string RamType(object? smbios, object? typeLegacy)
    {
        return Convert.ToInt32(smbios ?? 0) switch
        {
            20 => "DDR", 21 => "DDR2", 24 => "DDR3", 26 => "DDR4", 34 => "DDR5", 35 => "LPDDR5",
            _ => "Desconhecido"
        };
    }

    private static string RamForm(object? ff) => Convert.ToInt32(ff ?? 0) switch
    {
        8 => "DIMM", 12 => "SO-DIMM", 13 => "Micro-DIMM", 15 => "FB-DIMM", 9 => "RIMM", 11 => "SRIMM", 6 => "Proprietário", 0 => "—",
        _ => "Outro"
    };

    // Velocidades que costumam aparecer no part number do módulo (ex.: CMK32GX5M2B6000C30, F5-6000J3038F16GX2).
    private static readonly System.Text.RegularExpressions.Regex SpeedInPart = new(
        @"(?<!\d)(2133|2400|2666|2933|3000|3200|3466|3600|3733|3800|4000|4133|4266|4400|4800|5200|5600|5800|6000|6200|6400|6600|6800|7000|7200|7400|7600|7800|8000|8200|8400|8800)(?!\d)",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex ChannelInLocator = new(
        @"(?i)(?:channel|chan|ch)[\s_-]*([A-H0-9])|dimm[\s_-]*([A-H])\d|(?:^|[^A-Za-z])([A-H])[12](?:$|[^0-9])",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string? ChannelOf(string locator)
    {
        var m = ChannelInLocator.Match(locator);
        if (!m.Success) return null;
        for (int g = 1; g <= 3; g++) if (m.Groups[g].Success) return m.Groups[g].Value.ToUpperInvariant();
        return null;
    }

    private static string Volt(object? mv)
    {
        if (mv == null) return "—";
        double v = Convert.ToDouble(mv);
        return v > 0 ? $"{v / 1000:0.00#} V" : "—";
    }

    private static void CollectRam(InfoNode n)
    {
        var mods = new List<(string Bank, string Loc, ulong Cap, string Type, string Form, int Rated, int Conf, string Maker, string Part,
                             string Serial, int DataW, int TotalW, string Rank, string VoltCfg, string VoltRange)>();
        foreach (var m in Query("SELECT * FROM Win32_PhysicalMemory"))
        {
            int attr = Convert.ToInt32(m["Attributes"] ?? 0);
            string rank = attr switch { 1 => "1 (single)", 2 => "2 (dual)", 3 => "3", 4 => "4 (quad)", _ => "—" };
            string range = m["MinVoltage"] != null && m["MaxVoltage"] != null
                ? $"{Volt(m["MinVoltage"])} – {Volt(m["MaxVoltage"])}" : "—";
            mods.Add((S(m, "BankLabel"), S(m, "DeviceLocator"), Convert.ToUInt64(m["Capacity"] ?? 0UL),
                      RamType(m["SMBIOSMemoryType"], m["MemoryType"]), RamForm(m["FormFactor"]),
                      Convert.ToInt32(m["Speed"] ?? 0), Convert.ToInt32(m["ConfiguredClockSpeed"] ?? 0),
                      S(m, "Manufacturer"), S(m, "PartNumber"), S(m, "SerialNumber"),
                      Convert.ToInt32(m["DataWidth"] ?? 0), Convert.ToInt32(m["TotalWidth"] ?? 0),
                      rank, Volt(m["ConfiguredVoltage"]), range));
        }

        ulong total = 0;
        foreach (var m in mods) total += m.Cap;

        int slots = 0; ulong maxBytes = 0;
        try
        {
            foreach (var a in Query("SELECT * FROM Win32_PhysicalMemoryArray"))
            {
                slots += Convert.ToInt32(a["MemoryDevices"] ?? 0);
                ulong kb = a["MaxCapacityEx"] != null ? Convert.ToUInt64(a["MaxCapacityEx"]) : Convert.ToUInt64(a["MaxCapacity"] ?? 0UL);
                maxBytes += kb * 1024;
            }
        }
        catch { }

        n.Add("Total instalado", Bytes(total));

        // ---- Resumo ----
        var sum = n.Add("Resumo");
        sum.Add("Módulos instalados", slots > 0 ? $"{mods.Count} de {slots} slots" : mods.Count.ToString());
        if (maxBytes > 0) sum.Add("Capacidade máxima suportada pela placa", Bytes(maxBytes));
        if (mods.Count > 0)
        {
            var types = mods.Select(m => m.Type).Distinct().ToList();
            sum.Add("Tipo", string.Join(" / ", types));

            int conf = mods.Where(m => m.Conf > 0).Select(m => m.Conf).DefaultIfEmpty(0).Min();
            int rated = mods.Select(m => m.Rated).DefaultIfEmpty(0).Max();
            sum.Add("Velocidade em uso", conf > 0 ? $"{conf} MT/s" : "—");
            // Spec 002, D3: Win32_PhysicalMemory.Speed é o que a BIOS escreve no SMBIOS, não o nominal do módulo (esse vem do SPD, em ApplySpd).
            sum.Add(SpeedBiosLabel, rated > 0 ? $"{rated} MT/s" : "—");

            // Perfil EXPO/XMP: o Windows não diz se está ligado; estimamos pela velocidade escrita no part number.
            int label = 0;
            foreach (var m in mods)
            {
                var mt = SpeedInPart.Match(m.Part);
                if (mt.Success) label = Math.Max(label, int.Parse(mt.Value));
            }
            string profile;
            if (conf <= 0) profile = "Não foi possível ler a velocidade em uso.";
            else if (label > 0 && conf < label * 0.95)
                profile = $"Provavelmente DESLIGADO: a RAM roda a {conf} MT/s, mas o part number indica {label} MT/s. Ative EXPO/XMP na BIOS.";
            else if (label > 0)
                profile = $"Provavelmente ATIVO: roda a {conf} MT/s (part number indica {label} MT/s).";
            else if (rated > 0 && conf < rated)
                profile = $"Roda abaixo da velocidade nominal ({conf} de {rated} MT/s). Confira EXPO/XMP na BIOS.";
            else
                profile = "Sem como estimar (o part number não traz a velocidade). Confira na BIOS.";
            sum.Add("Perfil EXPO/XMP (estimado)", profile);

            // Canais: deduzidos pelos nomes dos slots (ex.: DIMM_A2 + DIMM_B2 = dois canais).
            var chans = mods.Select(m => ChannelOf(m.Loc) ?? ChannelOf(m.Bank)).ToList();
            string chText;
            if (mods.Count == 1) chText = "Canal único (1 módulo). Com 2 módulos iguais a RAM roda em dual channel e fica bem mais rápida.";
            else if (chans.All(c => c != null))
            {
                int distinct = chans.Distinct().Count();
                chText = distinct >= 2 ? $"{distinct} canais (dual channel ou mais)" : "Canal único: os módulos estão no mesmo canal. Mude para os slots recomendados no manual da placa.";
            }
            else chText = $"Não deu para identificar pelos nomes dos slots ({string.Join(", ", mods.Select(m => m.Loc))}). Veja o manual da placa.";
            sum.Add("Canais (estimado)", chText);

            bool ecc = mods.Any(m => m.TotalW > m.DataW && m.DataW > 0);
            sum.Add("ECC", ecc ? "Sim (correção de erros)" : "Não");

            var volts = mods.Select(m => m.VoltCfg).Where(v => v != "—").Distinct().ToList();
            if (volts.Count > 0) sum.Add("Tensão em uso", string.Join(" / ", volts));
        }
        sum.Add(TimingsLabel, "Lendo o SPD dos módulos… (precisa dos sensores abertos, como administrador)");

        try
        {
            foreach (var os in Query("SELECT TotalVisibleMemorySize, FreePhysicalMemory, TotalVirtualMemorySize, FreeVirtualMemory FROM Win32_OperatingSystem"))
            {
                double totKb = Convert.ToDouble(os["TotalVisibleMemorySize"]), freeKb = Convert.ToDouble(os["FreePhysicalMemory"]);
                double virtTot = Convert.ToDouble(os["TotalVirtualMemorySize"]), virtFree = Convert.ToDouble(os["FreeVirtualMemory"]);
                sum.Add("Em uso (no momento da leitura)", $"{Bytes((totKb - freeKb) * 1024)} de {Bytes(totKb * 1024)} ({(totKb - freeKb) / totKb:P0})");
                sum.Add("Memória virtual em uso (RAM + arquivo de paginação)", $"{Bytes((virtTot - virtFree) * 1024)} de {Bytes(virtTot * 1024)}");
            }
        }
        catch { }

        // ---- Módulos ----
        foreach (var m in mods)
        {
            var slot = n.Add($"Módulo — {m.Bank} / {m.Loc}");
            slot.Add("Capacidade", Bytes(m.Cap));
            slot.Add("Tipo", m.Type);
            slot.Add("Formato", m.Form);
            slot.Add("Ranks", m.Rank);
            slot.Add("Velocidade em uso", m.Conf > 0 ? $"{m.Conf} MT/s" : "—");
            slot.Add(SpeedBiosLabel, m.Rated > 0 ? $"{m.Rated} MT/s" : "—");
            slot.Add("Tensão em uso", m.VoltCfg);
            slot.Add("Faixa de tensão do módulo", m.VoltRange);
            slot.Add("Largura de dados / total", m.DataW > 0 ? $"{m.DataW} / {m.TotalW} bits" : "—");
            slot.Add("Fabricante", m.Maker);
            slot.Add("Part number", m.Part);
            slot.Add("Nº de série", m.Serial);
        }
    }

    private static void CollectGpu(InfoNode n)
    {
        foreach (var g in Query("SELECT * FROM Win32_VideoController"))
        {
            var gpu = n.Add(S(g, "Name"));
            gpu.Add("Versão do driver", S(g, "DriverVersion"));
            gpu.Add("Memória (WMI, limitada a 4 GB)", Bytes(g["AdapterRAM"]));
            gpu.Add("Resolução atual", $"{S(g, "CurrentHorizontalResolution")}x{S(g, "CurrentVerticalResolution")} @ {S(g, "CurrentRefreshRate")} Hz");
        }
    }

    /// <summary>Relê os discos físicos (ex.: depois de conectar um pendrive ou HD externo).</summary>
    public static void RefreshDisks(InfoNode n)
    {
        n.Children.Clear();
        try { CollectDisks(n); }
        catch (Exception ex) { n.Add("Erro ao ler", ex.Message); }
    }

    /// <summary>Spec 002, D4: barramento real pelo MSFT_PhysicalDisk (o mesmo do DiskHealth); Win32_DiskDrive diz "SCSI" para NVMe.</summary>
    private static Dictionary<int, string> BusByDiskIndex()
    {
        var map = new Dictionary<int, string>();
        try
        {
            var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
            scope.Connect();
            using var s = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT DeviceId, BusType FROM MSFT_PhysicalDisk"));
            foreach (ManagementObject d in s.Get())
                if (int.TryParse(d["DeviceId"]?.ToString(), out int idx) && BusName(Convert.ToInt32(d["BusType"] ?? 0)) is { } bus)
                    map[idx] = bus;
        }
        catch { }
        return map;
    }

    internal static string? BusName(int busType) => busType switch
    {
        17 => "NVMe", 11 => "SATA", 7 => "USB", 10 => "SAS", 8 => "RAID", 3 => "ATA", 1 => "SCSI", 12 => "SD", 13 => "MMC", 15 => "Arquivo (VHD)",
        _ => null,
    };

    // ---------------------------------------------------------------- SPD da RAM (spec 002, D2 e D3)

    public const string TimingsLabel = "Timings (CL, tRCD, tRP...)";
    public const string SpeedBiosLabel = "Velocidade máxima informada pela BIOS (SMBIOS)";
    public const string SpeedSpdLabel = "Velocidade nominal do módulo (SPD)";

    /// <summary>Timings do SPD de um módulo, em ns, como o LHM lê (sensores "Timing" de cada DIMM).</summary>
    public sealed record SpdTimings(string Module, double TckMin, double Taa, double Trcd, double Trp, double Tras, double Trc);

    /// <summary>Lê os timings do SPD nas linhas de sensores do LHM (HardwareType.Memory, categoria Timing).</summary>
    public static List<SpdTimings> ReadSpd(IEnumerable<SensorRow> rows)
    {
        var list = new List<SpdTimings>();
        foreach (var g in rows.Where(r => r.HardwareType == LibreHardwareMonitor.Hardware.HardwareType.Memory
                                       && r.Type == LibreHardwareMonitor.Hardware.SensorType.Timing)
                              .GroupBy(r => r.Hardware))
        {
            double? T(string name) => g.FirstOrDefault(r => r.Name.StartsWith(name + " ", StringComparison.Ordinal) || r.Name == name)?.RawValue;
            if (T("tCKAVGmin") is double ck && ck > 0 && T("tAA") is double aa && T("tRCD") is double rcd && T("tRP") is double rp)
                list.Add(new SpdTimings(g.Key, ck, aa, rcd, rp, T("tRAS") ?? 0, T("tRC") ?? 0));
        }
        return list;
    }

    /// <summary>MT/s nominal a partir do tCKAVGmin (ns): DDR transfere duas vezes por ciclo.</summary>
    public static int RatedMts(double tckMinNs)
    {
        double mts = 2000.0 / tckMinNs;
        int bin = SpeedBins.OrderBy(b => Math.Abs(b - mts)).First();
        return Math.Abs(bin - mts) / mts < 0.01 ? bin : (int)Math.Round(mts); // 0,750 ns = 2666,7 → "2666", como o JEDEC escreve
    }

    private static readonly int[] SpeedBins =
        { 1600, 1866, 2133, 2400, 2666, 2933, 3200, 3600, 4000, 4400, 4800, 5200, 5600, 6000, 6400, 6800, 7200, 7600, 8000, 8400, 8800 };

    /// <summary>Ciclos de clock para um tempo em ns na velocidade dada, com o arredondamento do JEDEC (fator de correção 0,974).</summary>
    public static int Cycles(double ns, int mts)
    {
        if (ns <= 0 || mts <= 0) return 0;
        long tckPs = (long)Math.Round(2_000_000.0 / mts); // ps por ciclo
        long tPs = (long)Math.Round(ns * 1000);
        return (int)((tPs * 1000 / tckPs + 974) / 1000);
    }

    public static string Primary(SpdTimings t, int mts) =>
        $"{Cycles(t.Taa, mts)}-{Cycles(t.Trcd, mts)}-{Cycles(t.Trp, mts)}-{Cycles(t.Tras, mts)} @ {mts} MT/s";

    /// <summary>
    /// Troca o texto fixo de timings pelo SPD lido pelo LHM e põe o nominal do módulo. Devolve true se mudou algo
    /// (a tela então reexibe o nó). Chamado depois que os sensores abrem: o SystemInfoCollector em si só lê WMI.
    /// </summary>
    public static bool ApplySpd(InfoNode ram, IEnumerable<SensorRow> rows)
    {
        var sum = ram.Children.FirstOrDefault(c => c.Header == "Resumo");
        var timings = sum?.Children.FirstOrDefault(c => c.Header == TimingsLabel);
        if (sum == null || timings == null) return false;
        var spd = ReadSpd(rows);
        if (spd.Count == 0)
        {
            timings.Value = "Não foi possível ler o SPD dos módulos (o app precisa estar como administrador, com o PawnIO). Veja na BIOS.";
            return true;
        }
        int inUse = 0;
        var speedNode = sum.Children.FirstOrDefault(c => c.Header == "Velocidade em uso");
        if (speedNode != null) int.TryParse(new string(speedNode.Value.TakeWhile(char.IsDigit).ToArray()), out inUse);

        var groups = spd.GroupBy(t => (t.TckMin, t.Taa, t.Trcd, t.Trp, t.Tras)).ToList();
        var parts = new List<string>();
        foreach (var g in groups)
        {
            var t = g.First();
            int rated = RatedMts(t.TckMin);
            string who = groups.Count > 1 ? string.Join(", ", g.Select(x => x.Module)) + ": " : "";
            string s = $"{who}SPD {Primary(t, rated)}";
            if (inUse > 0 && inUse != rated) s += $"; na velocidade em uso equivalem a {Primary(t, inUse)}";
            parts.Add(s + $" (tAA {t.Taa:0.##} ns, tRCD {t.Trcd:0.##} ns, tRP {t.Trp:0.##} ns, tRAS {t.Tras:0.##} ns)");
        }
        timings.Value = string.Join(" · ", parts) +
                        ". São os valores de fábrica gravados no módulo; os timings exatos que a BIOS configurou não são lidos.";

        int ratedAll = spd.Select(t => RatedMts(t.TckMin)).Min();
        var ratedNode = sum.Children.FirstOrDefault(c => c.Header == SpeedSpdLabel);
        string ratedText = $"{RamKind(ram)}-{ratedAll} ({ratedAll} MT/s, lido do SPD)";
        if (ratedNode == null)
        {
            int at = sum.Children.FindIndex(c => c.Header == SpeedBiosLabel);
            var node = new InfoNode { Header = SpeedSpdLabel, Value = ratedText };
            if (at >= 0) sum.Children.Insert(at, node); else sum.Children.Add(node);
        }
        else ratedNode.Value = ratedText;
        return true;
    }

    private static string RamKind(InfoNode ram) =>
        ram.Children.FirstOrDefault(c => c.Header == "Resumo")?.Children.FirstOrDefault(c => c.Header == "Tipo")?.Value is { Length: > 0 } t && t != "—" ? t : "DDR";

    private static void CollectDisks(InfoNode n)
    {
        var bus = BusByDiskIndex();
        foreach (var d in Query("SELECT * FROM Win32_DiskDrive"))
        {
            var disk = n.Add(S(d, "Model"));
            disk.Add("Capacidade", DiskBytes(d["Size"]));
            int idx = Convert.ToInt32(d["Index"] ?? -1);
            disk.Add("Interface", bus.TryGetValue(idx, out var b) ? b : S(d, "InterfaceType"));
            disk.Add("Nº de série", S(d, "SerialNumber"));
            disk.Add("Firmware", S(d, "FirmwareRevision"));
        }
    }
}
