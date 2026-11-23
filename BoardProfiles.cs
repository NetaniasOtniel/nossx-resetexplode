using LibreHardwareMonitor.Hardware;

namespace ResetExplode;

/// <summary>Uma entrada de tensão do Super I/O: nome e divisor no formato do LHM (V = v + (v − Vf)·Ri/Rf).</summary>
public sealed record VoltageMap(int Index, string Name, float Ri = 0, float Rf = 1, float Vf = 0, bool Estimated = false);

/// <summary>Nome de um canal de temperatura, ventoinha ou controle do Super I/O.</summary>
public sealed record NameMap(SensorType Type, int Index, string Name, bool Estimated = false);

/// <summary>Perfil de placa que o LHM não reconhece. <paramref name="Board"/> é comparado por "contém" com o nome da placa.</summary>
public sealed record BoardProfile(string Board, string Chip, string Source, IReadOnlyList<VoltageMap> Voltages, IReadOnlyList<NameMap> Names);

/// <summary>
/// Spec 002, D1. Placas que o LibreHardwareMonitor não tem na tabela caem na configuração genérica do chip: tensões cruas
/// ("Voltage #n", ocultas por padrão), "Temperature #n" e "Fan #n". Aqui o app aplica nome e divisor pela API pública do LHM
/// (ISensor.Name e ISensor.Parameters), o mesmo mecanismo da janela "Parâmetros" do LibreHardwareMonitor.
/// </summary>
public static class BoardProfiles
{
    public static readonly IReadOnlyList<BoardProfile> All = new[]
    {
        // Gigabyte B360M AORUS Gaming 3 (-CF), ITE IT8686E. Divisores e nomes da config "B360M_H" do próprio LHM
        // (mesmo chip, mesma família Gigabyte B360): SuperIOHardware.cs:2072-2090 @ 3d331e3. Os três trilhos escalados
        // batem com o ATX (±5 %) na medição de 23/11/2026 (docs/diagnostico-pc.md §4.3), mas a referência de 2 % (HWiNFO)
        // não chegou: todas as tensões ficam "estimado" até essa comparação (spec 002, D1, caminho b).
        new BoardProfile("B360M AORUS Gaming 3", "IT8686E", "LHM B360M_H (SuperIOHardware.cs:2072-2090 @ 3d331e3)",
            new[]
            {
                new VoltageMap(0, "Vcore", Estimated: true),
                new VoltageMap(1, "+3.3V", 29.4f, 45.3f, Estimated: true),
                new VoltageMap(2, "+12V", 10f, 2f, Estimated: true),
                new VoltageMap(3, "+5V", 15f, 10f, Estimated: true),
                new VoltageMap(5, "VCCSA", Estimated: true),
                new VoltageMap(6, "DIMM A/B", Estimated: true),
                new VoltageMap(7, "+3V Standby", 1, 1, Estimated: true),
                new VoltageMap(8, "CMOS Battery", 1, 1, Estimated: true),
            },
            new[]
            {
                new NameMap(SensorType.Temperature, 0, "System"),
                new NameMap(SensorType.Temperature, 1, "PCH"),
                new NameMap(SensorType.Temperature, 2, "CPU"),
                new NameMap(SensorType.Temperature, 4, "VRM MOS"),
                new NameMap(SensorType.Fan, 0, "CPU Fan"),
                new NameMap(SensorType.Fan, 1, "System Fan"),
                new NameMap(SensorType.Control, 0, "CPU Fan"),
                new NameMap(SensorType.Control, 1, "System Fan"),
            }),
    };

    /// <summary>Perfil para a placa e o chip, ou null.</summary>
    public static BoardProfile? Find(string? board, string? chip)
    {
        if (string.IsNullOrWhiteSpace(board) || string.IsNullOrWhiteSpace(chip)) return null;
        return All.FirstOrDefault(p => board.Contains(p.Board, StringComparison.OrdinalIgnoreCase)
                                    && chip.Contains(p.Chip, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A mesma fórmula do LHM (SuperIOHardware.cs:6085), para teste e para conferir medições.</summary>
    public static float Volts(float raw, VoltageMap m) => raw + (raw - m.Vf) * m.Ri / m.Rf;

    /// <summary>Texto curto e dica mostrados nos canais do perfil ainda não confirmados contra o HWiNFO (spec 002, D1).</summary>
    public const string EstimatedLabel = "estimado (escala da B360M H)";

    public static string EstimatedTooltip(BoardProfile p) =>
        $"Esta placa não está na tabela do LibreHardwareMonitor. Nome e divisor deste canal vêm de outra placa com o mesmo chip ({p.Source}). " +
        "O valor é uma estimativa: não foi confirmado contra o HWiNFO dentro de 2 %.";

    /// <summary>
    /// Aplica o perfil a cada Super I/O de uma placa com perfil. Devolve os identificadores dos sensores ajustados, com a dica
    /// de "estimado" quando o canal não foi confirmado (null quando foi). O SensorMonitor mostra esses mesmo quando o LHM
    /// os marcou como ocultos por padrão.
    /// </summary>
    public static Dictionary<string, string?> Apply(IComputer computer)
    {
        var mapped = new Dictionary<string, string?>();
        foreach (var board in computer.Hardware.Where(h => h.HardwareType == HardwareType.Motherboard))
            foreach (var sio in board.SubHardware.Where(h => h.HardwareType == HardwareType.SuperIO))
            {
                var p = Find(board.Name, sio.Name);
                if (p == null) continue;
                sio.Update(); // o LHM só ativa (põe em Sensors) o canal que já leu um valor
                foreach (var s in sio.Sensors)
                {
                    if (s.SensorType == SensorType.Voltage && p.Voltages.FirstOrDefault(v => v.Index == s.Index) is { } v)
                    {
                        s.Name = v.Name;
                        if (s.Parameters.Count >= 3)
                        {
                            s.Parameters[0].Value = v.Ri;
                            s.Parameters[1].Value = v.Rf;
                            s.Parameters[2].Value = v.Vf;
                        }
                        mapped[s.Identifier.ToString()] = v.Estimated ? EstimatedTooltip(p) : null;
                    }
                    else if (p.Names.FirstOrDefault(n => n.Type == s.SensorType && n.Index == s.Index) is { } n)
                    {
                        s.Name = n.Name;
                        mapped[s.Identifier.ToString()] = n.Estimated ? EstimatedTooltip(p) : null;
                    }
                }
            }
        return mapped;
    }
}
