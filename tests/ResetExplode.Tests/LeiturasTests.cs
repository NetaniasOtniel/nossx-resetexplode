using System.Globalization;
using LibreHardwareMonitor.Hardware;
using Xunit;

namespace ResetExplode.Tests;

/// <summary>Critérios de aceite do spec 002 (docs/specs/002-correcoes-leitura.md) que não dependem do hardware.</summary>
public sealed class LeiturasTests
{
    public LeiturasTests() => CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pt-BR"); // o app formata na cultura do usuário

    // ------------------------------------------------------------------ D1: perfil da placa

    [Fact(DisplayName = "D1 · B360M AORUS Gaming 3-CF + IT8686E acha o perfil; os valores crus medidos viram trilhos dentro do ATX")]
    public void Perfil_B360M_Aorus()
    {
        var p = BoardProfiles.Find("Gigabyte B360M AORUS Gaming 3-CF", "ITE IT8686E");
        Assert.NotNull(p);
        Assert.Null(BoardProfiles.Find("Gigabyte B360M H", "ITE IT8686E"));                 // a placa irmã fica com o LHM
        Assert.Null(BoardProfiles.Find("Gigabyte B360M AORUS Gaming 3-CF", "ITE IT8688E")); // outro chip: sem perfil

        VoltageMap V(string n) => p!.Voltages.Single(v => v.Name == n);
        // Valores crus da coleta de 23/11/2026 (Voltage #2, #3, #4) e a tolerância ATX de ±5 %.
        AssertRail(BoardProfiles.Volts(2.028f, V("+3.3V")), 3.3, 3.344);
        AssertRail(BoardProfiles.Volts(2.028f, V("+12V")), 12.0, 12.168);
        AssertRail(BoardProfiles.Volts(1.980f, V("+5V")), 5.0, 4.950);
        Assert.Equal(1.236f, BoardProfiles.Volts(1.236f, V("Vcore")), 3);                  // sem divisor
        Assert.Equal(3.384f, BoardProfiles.Volts(1.692f, V("+3V Standby")), 3);             // ×2, igual ao default do LHM

        static void AssertRail(float got, double nominal, double expected)
        {
            Assert.InRange(got, expected - 0.005, expected + 0.005);
            Assert.InRange(got, nominal * 0.95, nominal * 1.05);
        }
    }

    [Fact(DisplayName = "D1 · canal de perfil não confirmado aparece como 'estimado (escala da B360M H)' com a origem na dica")]
    public void Perfil_Estimado()
    {
        var p = BoardProfiles.Find("Gigabyte B360M AORUS Gaming 3-CF", "ITE IT8686E")!;
        // Caminho (b) de 23/11/2026: sem a referência do HWiNFO, toda tensão da placa é estimada; nomes de temperatura e
        // ventoinha não carregam escala e ficam sem a marca.
        Assert.All(p.Voltages, v => Assert.True(v.Estimated, v.Name));
        Assert.All(p.Names, n => Assert.False(n.Estimated, n.Name));
        string tip = BoardProfiles.EstimatedTooltip(p);
        Assert.Contains("B360M_H", tip);
        Assert.Contains("2 %", tip);
        var est = new SensorRow { Name = "+12V", Type = SensorType.Voltage, EstimateNote = tip };
        Assert.Equal("+12V · estimado (escala da B360M H)", est.Label);
        var med = new SensorRow { Name = "+12V", Type = SensorType.Voltage };
        Assert.Equal("+12V", med.Label);
        Assert.False(med.IsEstimated);
    }

    [Fact(DisplayName = "D1/D8/D9 · regras de listagem: tensão crua do Super I/O sem perfil, sensor sem valor e consumo 0 W da CPU ficam fora")]
    public void RegrasDeListagem()
    {
        Assert.False(SensorMonitor.ShouldList(HardwareType.SuperIO, SensorType.Voltage, defaultHidden: true, profiled: false, 2.028f));
        Assert.True(SensorMonitor.ShouldList(HardwareType.SuperIO, SensorType.Voltage, defaultHidden: true, profiled: true, 12.17f));
        Assert.True(SensorMonitor.ShouldList(HardwareType.GpuNvidia, SensorType.Load, defaultHidden: true, profiled: false, 5f)); // fora do Super I/O nada muda
        Assert.False(SensorMonitor.ShouldList(HardwareType.SuperIO, SensorType.Control, false, false, null));                 // "Fan #n" sempre "—"
        Assert.False(SensorMonitor.ShouldList(HardwareType.Cpu, SensorType.Power, false, false, 0f));                         // CPU Memory/Platform
        Assert.True(SensorMonitor.ShouldList(HardwareType.Cpu, SensorType.Power, false, false, 54.3f));                       // Package
        Assert.True(SensorMonitor.ShouldList(HardwareType.SuperIO, SensorType.Fan, false, false, 0f));                        // ventoinha parada é leitura real
    }

    // ------------------------------------------------------------------ D2/D3: SPD

    [Fact(DisplayName = "D2 · ciclos com o arredondamento JEDEC: 13,75 ns = CL22 @ 3200 e CL19 @ 2666; tRAS 32 ns = 52 e 43")]
    public void Spd_CiclosJedec()
    {
        Assert.Equal(22, SystemInfoCollector.Cycles(13.75, 3200));
        Assert.Equal(19, SystemInfoCollector.Cycles(13.75, 2666));
        Assert.Equal(52, SystemInfoCollector.Cycles(32, 3200));
        Assert.Equal(43, SystemInfoCollector.Cycles(32, 2666));
        Assert.Equal(3200, SystemInfoCollector.RatedMts(0.625));
        Assert.Equal(2666, SystemInfoCollector.RatedMts(0.750));
        Assert.Equal(2400, SystemInfoCollector.RatedMts(0.833));
    }

    [Fact(DisplayName = "D2/D3 · ApplySpd troca o 'Windows não expõe' pelo SPD e põe o nominal DDR4-3200 antes do valor da BIOS")]
    public void Spd_AplicadoNaRam()
    {
        var ram = new InfoNode { Header = "Memória RAM" };
        var sum = ram.Add("Resumo");
        sum.Add("Tipo", "DDR4");
        sum.Add("Velocidade em uso", "2666 MT/s");
        sum.Add(SystemInfoCollector.SpeedBiosLabel, "2666 MT/s");
        sum.Add(SystemInfoCollector.TimingsLabel, "…");

        var rows = new List<SensorRow>();
        foreach (var dimm in new[] { "Micron Technology - 8ATF1G64AZ-3G2J1 (#0)", "SK Hynix - HMA81GU6CJR8N-XN (#2)" })
            foreach (var (n, v) in new[] { ("tCKAVGmin (Minimum Cycle Time)", 0.625f), ("tAA (Minimum CAS Latency Time)", 13.75f),
                                           ("tRCD (RAS to CAS Delay Time)", 13.75f), ("tRP (Row Precharge Delay Time)", 13.75f),
                                           ("tRAS (Active to Precharge Delay Time)", 32f), ("tRC (Active to Active/Refresh Delay Time)", 45.75f) })
            {
                var r = new SensorRow { Hardware = dimm, Name = n, Type = SensorType.Timing, HardwareType = HardwareType.Memory };
                r.Update(v);
                rows.Add(r);
            }

        Assert.True(SystemInfoCollector.ApplySpd(ram, rows));
        string t = sum.Children.Single(c => c.Header == SystemInfoCollector.TimingsLabel).Value;
        Assert.Contains("22-22-22-52 @ 3200 MT/s", t);
        Assert.Contains("19-19-19-43 @ 2666 MT/s", t);
        Assert.DoesNotContain("não expõe", t);
        var labels = sum.Children.Select(c => c.Header).ToList();
        Assert.Equal("DDR4-3200 (3200 MT/s, lido do SPD)", sum.Children.Single(c => c.Header == SystemInfoCollector.SpeedSpdLabel).Value);
        Assert.True(labels.IndexOf(SystemInfoCollector.SpeedSpdLabel) < labels.IndexOf(SystemInfoCollector.SpeedBiosLabel));
    }

    [Fact(DisplayName = "D2 · sem SPD (sem admin/PawnIO) a tela diz o motivo em vez de 'o Windows não expõe'")]
    public void Spd_Ausente()
    {
        var ram = new InfoNode { Header = "Memória RAM" };
        ram.Add("Resumo").Add(SystemInfoCollector.TimingsLabel, "…");
        Assert.True(SystemInfoCollector.ApplySpd(ram, Array.Empty<SensorRow>()));
        Assert.Contains("administrador", ram.Children[0].Children[0].Value);
    }

    // ------------------------------------------------------------------ D5: unidades de disco

    [Fact(DisplayName = "D5 · disco em base 1000: 98.331 GiB do LHM = 105,58 TB do smartctl; capacidade 1,02 TB (não 953,86 \"GB\")")]
    public void Unidades_Disco()
    {
        // smartctl, 23/11/2026: 206.216.435 unidades × 512.000 B
        double smartGb = 206_216_435d * 512_000 / 1e9;
        float lhmGiB = (float)(206_216_435d * 512_000 / (1L << 30));
        float? gb = SensorMonitor.Normalize(HardwareType.Storage, SensorType.Data, "Data Written", lhmGiB);
        Assert.InRange(gb!.Value, smartGb * 0.999, smartGb * 1.001);
        Assert.Equal("105,58 TB", DiskHealth.FmtGb(gb.Value));
        Assert.Equal(228.59f, SensorMonitor.Normalize(HardwareType.Storage, SensorType.Data, "Free Space", 228.59f)); // já é decimal
        Assert.Equal(30.85f, SensorMonitor.Normalize(HardwareType.Memory, SensorType.Data, "Memory Used", 30.85f));   // RAM fica binária
        Assert.Equal("1,02 TB", SystemInfoCollector.DiskBytes(1_024_209_543_168L)); // mesma base do DiskHealth.FmtSize
        Assert.Equal("256,06 GB", SystemInfoCollector.DiskBytes(256_060_514_304L));
    }

    // ------------------------------------------------------------------ D4, D6, D11, D12

    [Fact(DisplayName = "D4 · BusType do MSFT_PhysicalDisk: 17 = NVMe")]
    public void Barramento() { Assert.Equal("NVMe", SystemInfoCollector.BusName(17)); Assert.Equal("SATA", SystemInfoCollector.BusName(11)); Assert.Null(SystemInfoCollector.BusName(0)); }

    [Fact(DisplayName = "D6 · hypervisor ativo = virtualização ligada, mesmo com VirtualizationFirmwareEnabled = False")]
    public void Virtualizacao()
    {
        Assert.StartsWith("Sim", SystemInfoCollector.Virtualization(false, true));
        Assert.Equal("Sim", SystemInfoCollector.Virtualization(true, false));
        Assert.StartsWith("Não", SystemInfoCollector.Virtualization(false, false));
    }

    [Fact(DisplayName = "D11 · placeholders do SMBIOS viram '—'")]
    public void Placeholders_Smbios()
    {
        Assert.Equal("—", SystemInfoCollector.Clean("Default string"));
        Assert.Equal("—", SystemInfoCollector.Clean("  To be filled by O.E.M. "));
        Assert.Equal("F17a", SystemInfoCollector.Clean("F17a"));
    }

    [Fact(DisplayName = "D12 · tensões por núcleo da Intel aparecem como VID; AMD, GPU e clocks não mudam")]
    public void Nomes_Vid()
    {
        Assert.Equal("VID núcleo #3", SensorMonitor.DisplayName(HardwareType.Cpu, SensorType.Voltage, "CPU Core #3", "Intel Core i9-9900KF"));
        Assert.Equal("VID (pedido pela CPU)", SensorMonitor.DisplayName(HardwareType.Cpu, SensorType.Voltage, "CPU Core", "Intel Core i9-9900KF"));
        Assert.Equal("CPU Core #3", SensorMonitor.DisplayName(HardwareType.Cpu, SensorType.Clock, "CPU Core #3", "Intel Core i9-9900KF"));
        Assert.Equal("Core #1 VID", SensorMonitor.DisplayName(HardwareType.Cpu, SensorType.Voltage, "Core #1 VID", "AMD Ryzen 7 5800X"));
    }

    // ------------------------------------------------------------------ D7

    [Fact(DisplayName = "D7 · mínimo e máximo são os lidos pelo app: o 0 de antes do S.M.A.R.T. não vira mínimo")]
    public void SensorRow_MinMaxProprio()
    {
        var r = new SensorRow { Type = SensorType.Temperature, HardwareType = HardwareType.Storage };
        r.Update(null);
        Assert.Equal("—", r.Min);
        r.Update(49);
        r.Update(48);
        r.Update(52);
        Assert.Equal("48 °C", r.Min);
        Assert.Equal("52 °C", r.Max);
        Assert.Equal("52 °C", r.Value);
    }
}
