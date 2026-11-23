using System.Collections.ObjectModel;
using LibreHardwareMonitor.Hardware;

namespace ResetExplode;

/// <summary>Encapsula o LibreHardwareMonitorLib e mantém a lista de sensores atualizada.</summary>
public sealed class SensorMonitor : IDisposable
{
    private readonly Computer _computer;
    private readonly Dictionary<string, SensorRow> _index = new();
    private volatile bool _open;

    public ObservableCollection<SensorRow> Rows { get; } = new();
    public bool IsOpen => _open;

    public SensorMonitor()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = true,
            IsStorageEnabled = true,
            IsNetworkEnabled = true,
            IsControllerEnabled = true
        };
    }

    /// <summary>Detecta o hardware (leva de 0,5 a alguns segundos) fora da thread da janela. Até terminar, Refresh não faz nada.</summary>
    public Task OpenAsync() => Task.Run(() =>
    {
        _computer.Open();
        try { _profiled = BoardProfiles.Apply(_computer); } catch { _profiled = new(); } // perfil é melhoria: nunca derruba os sensores
        _open = true;
    });

    /// <summary>Sensores ajustados por um perfil de placa (spec 002, D1).</summary>
    private HashSet<string> _profiled = new();

    public void Refresh()
    {
        if (!_open) return;
        foreach (var hw in _computer.Hardware)
            Walk(hw);
    }

    private void Walk(IHardware hw)
    {
        hw.Update();
        foreach (var s in hw.Sensors)
        {
            string id = s.Identifier.ToString();
            float? value = Normalize(hw, s);
            if (!_index.TryGetValue(id, out var row))
            {
                if (!ShouldList(hw, s, id, value)) continue;
                row = new SensorRow
                {
                    Id = id,
                    Hardware = hw.Name,
                    Category = SensorRow.CategoryName(s.SensorType),
                    Name = DisplayName(hw, s),
                    Index = s.Index,
                    Type = s.SensorType,
                    HardwareType = hw.HardwareType
                };
                _index[id] = row;
                Rows.Add(row);
            }
            row.Update(value);
        }
        foreach (var sub in hw.SubHardware)
            Walk(sub);
    }

    /// <summary>
    /// Spec 002: a linha só entra quando o sensor tem o que mostrar.
    /// D1 — canal que o LHM deixou oculto por padrão (tensão crua de placa não reconhecida) só aparece se um perfil o ajustou;
    /// D9 — sensor sem valor nenhum não vira linha "—";
    /// D8 — consumo da CPU só entra depois de ler &gt; 0 (o LHM ativa "CPU Memory"/"CPU Platform" mesmo com o contador parado).
    /// </summary>
    private bool ShouldList(IHardware hw, ISensor s, string id, float? value)
        => ShouldList(hw.HardwareType, s.SensorType, s.IsDefaultHidden, _profiled.Contains(id), value);

    internal static bool ShouldList(HardwareType hwType, SensorType type, bool defaultHidden, bool profiled, float? value)
    {
        // Só no Super I/O: lá "oculto por padrão" quer dizer "tensão sem escala" (default do chip). Em outros hardwares o app
        // sempre mostrou os ocultos por padrão do LHM, e isso fica como estava.
        if (hwType == HardwareType.SuperIO && defaultHidden && !profiled) return false;
        if (value is null || float.IsNaN(value.Value)) return false;
        if (hwType == HardwareType.Cpu && type == SensorType.Power && value <= 0) return false;
        return true;
    }

    /// <summary>
    /// Spec 002, D5: disco é decimal no app inteiro. O "Data Read/Written" do LHM vem em GiB (unidades NVMe × 512.000 / 2³⁰),
    /// enquanto "Total Space"/"Free Space" já vêm em GB decimal; converte os dois primeiros para GB decimal.
    /// </summary>
    internal static float? Normalize(IHardware hw, ISensor s) => Normalize(hw.HardwareType, s.SensorType, s.Name, s.Value);

    internal static float? Normalize(HardwareType hwType, SensorType type, string name, float? value)
    {
        if (value is float v && hwType == HardwareType.Storage && type == SensorType.Data
            && (name.StartsWith("Data Read", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Data Written", StringComparison.OrdinalIgnoreCase)))
            return v * GiBToGB;
        return value;
    }

    public const float GiBToGB = 1.073741824f;

    /// <summary>Spec 002, D12: na Intel, "CPU Core" e "CPU Core #n" (tensão) são VIDs do IA32_PERF_STATUS, não tensão medida.</summary>
    internal static string DisplayName(IHardware hw, ISensor s) => DisplayName(hw.HardwareType, s.SensorType, s.Name, hw.Name);

    internal static string DisplayName(HardwareType hwType, SensorType type, string name, string hwName)
    {
        if (hwType == HardwareType.Cpu && type == SensorType.Voltage && hwName.Contains("Intel", StringComparison.OrdinalIgnoreCase))
        {
            if (name == "CPU Core") return "VID (pedido pela CPU)";
            if (name.StartsWith("CPU Core #", StringComparison.Ordinal)) return "VID núcleo #" + name["CPU Core #".Length..];
        }
        return name;
    }

    /// <summary>Temperatura principal da CPU: Package (Intel) / Tctl-Tdie (AMD), senão a maior entre os núcleos.</summary>
    public float? CpuTemperature
    {
        get
        {
            var temps = Rows.Where(r => r.HardwareType == HardwareType.Cpu
                                     && r.Type == SensorType.Temperature
                                     && r.RawValue is > 0).ToList();
            var main = temps.FirstOrDefault(r => r.Name.Contains("Package") || r.Name.Contains("Tctl") || r.Name.Contains("Tdie"));
            return main?.RawValue ?? temps.Max(r => r.RawValue);
        }
    }

    /// <summary>Carga total da CPU em %.</summary>
    public float? CpuLoad => Rows.FirstOrDefault(r => r.HardwareType == HardwareType.Cpu
                                                   && r.Type == SensorType.Load
                                                   && r.Name == "CPU Total")?.RawValue;

    /// <summary>Consumo do pacote da CPU em W.</summary>
    public float? CpuPower => Rows.FirstOrDefault(r => r.HardwareType == HardwareType.Cpu
                                                    && r.Type == SensorType.Power
                                                    && r.Name.Contains("Package"))?.RawValue;

    /// <summary>
    /// Driver PawnIO (necessário para temperatura/consumo da CPU via MSR/SMN).
    /// A biblioteca procura exatamente esta chave de desinstalação.
    /// </summary>
    public static bool IsPawnIOInstalled
    {
        get
        {
            using var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine,
                                                                     Microsoft.Win32.RegistryView.Registry64);
            using var key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
            return key != null;
        }
    }

    public static bool IsElevated =>
        new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);

    public enum CpuSensorProblem { None, NotElevated, PawnIOMissing, ReadFailed }

    /// <summary>Por que a temperatura/consumo da CPU não aparecem (o motivo mais provável primeiro).</summary>
    public CpuSensorProblem DiagnoseCpuSensors()
    {
        if (CpuTemperature != null) return CpuSensorProblem.None;
        if (!IsElevated) return CpuSensorProblem.NotElevated;
        if (!IsPawnIOInstalled) return CpuSensorProblem.PawnIOMissing;
        return CpuSensorProblem.ReadFailed;
    }

    public static string Explain(CpuSensorProblem p) => p switch
    {
        CpuSensorProblem.NotElevated =>
            "O RESET EXPLODE não está rodando como administrador, e o Windows só libera os sensores internos da CPU " +
            "nesse modo. Feche e abra de novo aceitando o pedido de administrador.",
        CpuSensorProblem.PawnIOMissing =>
            "Falta o driver PawnIO, que lê a temperatura e o consumo da CPU. Ele é gratuito, assinado e usado também " +
            "pelo LibreHardwareMonitor e pelo FanControl. Depois de instalar, feche e abra o RESET EXPLODE.",
        CpuSensorProblem.ReadFailed =>
            "O PawnIO está instalado, mas a leitura falhou. Se acabou de instalar, reinicie o PC. Se continuar, " +
            "confira se o antivírus não bloqueou o driver ou se a CPU é muito antiga/recente para a biblioteca de sensores.",
        _ => ""
    };

    public void Dispose()
    {
        if (_open) _computer.Close();
    }
}
