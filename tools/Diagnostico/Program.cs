using System.Globalization;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using ResetExplode;

// Uso: Diagnostico.exe <pasta-de-saída> [amostras=12] [intervalo-s=5]
// Gera:
//   resetexplode-relatorio.json  — o MESMO JSON do botão EXPORTAR RELATÓRIO (SystemInfoCollector, GpuInfoCollector, SensorMonitor, DiskHealth, BatteryInfo)
//   resetexplode-sensores.csv    — cada amostra de cada sensor do LHM como o app mostra (texto) e o número cru
//   hwinfo-sensores.csv          — cada amostra de cada leitura do HWiNFO (memória compartilhada), na mesma janela
//   coleta.txt                   — metadados e problemas encontrados
var inv = CultureInfo.InvariantCulture;
string outDir = args.Length > 0 ? args[0] : Path.Combine(Environment.CurrentDirectory, "diag-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
int samples = args.Length > 1 ? int.Parse(args[1], inv) : 12;
int interval = args.Length > 2 ? int.Parse(args[2], inv) : 5;
Directory.CreateDirectory(outDir);
var log = new StringBuilder();
void Log(string s) { string l = $"{DateTime.Now:HH:mm:ss} {s}"; Console.WriteLine(l); log.AppendLine(l); }

bool admin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
Log($"Diagnostico · admin={admin} · amostras={samples} · intervalo={interval}s · saída={outDir}");
if (!admin) Log("AVISO: sem administrador, o LHM não lê CPU/placa-mãe (PawnIO).");

List<InfoNode> system = new();
try { system = SystemInfoCollector.Collect(); Log($"SystemInfoCollector: {system.Count} categorias"); }
catch (Exception ex) { Log("SystemInfoCollector falhou: " + ex); }

List<GpuInfo> gpus = new();
try { gpus = GpuInfoCollector.Collect(); Log($"GpuInfoCollector: {gpus.Count} GPU(s)"); }
catch (Exception ex) { Log("GpuInfoCollector falhou: " + ex); }

using var monitor = new SensorMonitor();
await monitor.OpenAsync();
monitor.Refresh();
Log($"SensorMonitor: {monitor.Rows.Count} sensores na 1ª leitura");
try { Log("DiagnoseCpuSensors: " + monitor.DiagnoseCpuSensors()); } catch (Exception ex) { Log("DiagnoseCpuSensors falhou: " + ex.Message); }

var lhmCsv = new StringBuilder("amostra;hora;hardware;tipo_hw;categoria;sensor;id;texto;valor;min;max\n");
var hwiCsv = new StringBuilder("amostra;hora;sensor;tipo;leitura;unidade;valor;min;max;media\n");
string Q(string? s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
string N(double? v) => v.HasValue && !double.IsNaN(v.Value) ? v.Value.ToString("0.###", inv) : "";

for (int i = 1; i <= samples; i++)
{
    if (i > 1) await Task.Delay(TimeSpan.FromSeconds(interval));
    monitor.Refresh();
    string t = DateTime.Now.ToString("HH:mm:ss", inv);
    foreach (var r in monitor.Rows)
        lhmCsv.Append($"{i};{t};{Q(r.Hardware)};{r.HardwareType};{Q(r.Category)};{Q(r.Name)};{Q(r.Id)};{Q(r.Value)};{N(r.RawValue)};{Q(r.Min)};{Q(r.Max)}\n");
    var hw = HwInfoShm.Read(out string? why);
    if (hw == null) { if (i == 1) Log("HWiNFO: " + why); }
    else
    {
        if (i == 1) Log($"HWiNFO: {hw.Count} leituras na memória compartilhada");
        foreach (var x in hw)
            hwiCsv.Append($"{i};{t};{Q(x.Sensor)};{x.Type};{Q(x.Label)};{Q(x.Unit)};{N(x.Value)};{N(x.Min)};{N(x.Max)};{N(x.Avg)}\n");
    }
    Console.WriteLine($"  amostra {i}/{samples}");
}

var rows = monitor.Rows.ToList();
var data = new ReportData
{
    Date = DateTime.Now,
    Machine = Environment.MachineName,
    AppVersion = typeof(SensorMonitor).Assembly.GetName().Version?.ToString(3) ?? "",
    System = system.Select(ReportBuilder.ToNode).Where(n => n != null).Cast<ReportNode>().ToList(),
    Gpus = gpus.Select(g => new ReportGpu(g.Name, g.Subtitle, g.Sections.Select(ReportBuilder.ToNode).Where(n => n != null).Cast<ReportNode>().ToList())).ToList(),
    Sensors = rows.Select(s => new ReportSensor(s.Hardware, s.Category, s.Name, s.Value, s.Min, s.Max)).ToList(),
};
try { data.Disks.AddRange(DiskHealth.Read(rows).Select(d => new ReportDisk(d.Title, d.Meta, d.VerdictText, d.Reasons, d.Metrics))); }
catch (Exception ex) { Log("DiskHealth falhou: " + ex); }
try { data.Power.AddRange(BatteryInfo.Read(rows).Select(d => new ReportDisk(d.Title, d.Meta, d.VerdictText, d.Reasons, d.Metrics))); }
catch (Exception ex) { Log("BatteryInfo falhou: " + ex); }

File.WriteAllText(Path.Combine(outDir, "resetexplode-relatorio.json"), ReportBuilder.Json(data), new UTF8Encoding(true));
File.WriteAllText(Path.Combine(outDir, "resetexplode-sensores.csv"), lhmCsv.ToString(), new UTF8Encoding(true));
File.WriteAllText(Path.Combine(outDir, "hwinfo-sensores.csv"), hwiCsv.ToString(), new UTF8Encoding(true));
Log("Pronto.");
File.WriteAllText(Path.Combine(outDir, "coleta.txt"), log.ToString(), new UTF8Encoding(true));
return 0;

/// <summary>
/// Leitor da memória compartilhada do HWiNFO ("Shared Memory Support", SensorsSM=1). Layout público do HWiNFO SDK
/// (struct _HWiNFO_SENSORS_SHARED_MEM2, pack 1); tamanhos dos elementos vêm do cabeçalho, então revisões novas continuam legíveis.
/// </summary>
static class HwInfoShm
{
    public sealed record Reading(string Sensor, string Type, string Label, string Unit, double Value, double Min, double Max, double Avg);

    private static readonly string[] Types = { "none", "temp", "volt", "fan", "current", "power", "clock", "usage", "other" };

    public static List<Reading>? Read(out string? why)
    {
        why = null;
        try
        {
            using var mmf = MemoryMappedFile.OpenExisting(@"Global\HWiNFO_SENS_SM2", MemoryMappedFileRights.Read);
            using var v = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            if (v.ReadUInt32(0) != 0x53695748) { why = "assinatura da memória compartilhada inválida (HWiNFO fechado?)"; return null; } // "HWiS"
            uint offS = v.ReadUInt32(20), sizeS = v.ReadUInt32(24), numS = v.ReadUInt32(28);
            uint offR = v.ReadUInt32(32), sizeR = v.ReadUInt32(36), numR = v.ReadUInt32(40);
            var sensors = new string[numS];
            for (uint s = 0; s < numS; s++) sensors[s] = Str(v, offS + s * sizeS + 8, 128);
            var list = new List<Reading>((int)numR);
            for (uint r = 0; r < numR; r++)
            {
                long b = offR + r * sizeR;
                uint type = v.ReadUInt32(b), idx = v.ReadUInt32(b + 4);
                list.Add(new Reading(idx < numS ? sensors[idx] : "?", type < Types.Length ? Types[type] : type.ToString(),
                    Str(v, b + 12, 128), Str(v, b + 268, 16),
                    v.ReadDouble(b + 284), v.ReadDouble(b + 292), v.ReadDouble(b + 300), v.ReadDouble(b + 308)));
            }
            return list;
        }
        catch (FileNotFoundException) { why = "memória compartilhada ausente: HWiNFO fechado ou Shared Memory Support desligado"; return null; }
        catch (Exception ex) { why = ex.Message; return null; }
    }

    private static string Str(MemoryMappedViewAccessor v, long at, int len)
    {
        var buf = new byte[len];
        v.ReadArray(at, buf, 0, len);
        int n = Array.IndexOf(buf, (byte)0);
        return Encoding.Latin1.GetString(buf, 0, n < 0 ? len : n).Trim();
    }
}
