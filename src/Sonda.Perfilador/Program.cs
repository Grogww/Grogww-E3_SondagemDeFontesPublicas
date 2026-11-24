// Perfilador: lê as execuções já coletadas em dados/<fonte>/<id>/ e calcula as métricas da
// caracterização. Não faz nenhum acesso à rede e não altera dados/.
// Uso: perfilador [fonte ...] [--execucao <id>] [--saida <pasta>] [--regras <caminho>]
//   sem fonte: todas as fontes; sem --execucao: todas as execuções de cada fonte.
//   Saída padrão: perfis/ (só agregados, sem valores dos registros).
using System.Text.Json;
using Sonda.Core;
using Sonda.Perfilador;

var raiz = LocalizarRaiz();
var fontes = new List<string>();
string? idFiltro = null;
var saida = Path.Combine(raiz, "perfis");
var caminhoRegras = Path.Combine(raiz, "config", "perfil.json");
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--execucao" when i + 1 < args.Length: idFiltro = args[++i]; break;
        case "--saida" when i + 1 < args.Length: saida = Path.GetFullPath(args[++i]); break;
        case "--regras" when i + 1 < args.Length: caminhoRegras = Path.GetFullPath(args[++i]); break;
        case "-h" or "--help":
            Console.WriteLine("Uso: perfilador [fonte ...] [--execucao <id>] [--saida <pasta>] [--regras <caminho>]");
            return 0;
        default: fontes.Add(args[i]); break;
    }
}

var geral = ConfigGeral.Carregar(Path.Combine(raiz, "config", "fontes.json"));
var regras = RegrasPerfil.Carregar(caminhoRegras);
var pastaDados = Path.Combine(raiz, geral.PastaDados);
if (!Directory.Exists(pastaDados)) { Console.Error.WriteLine($"Pasta de dados não encontrada: {pastaDados}"); return 2; }

var execucoes = new List<PerfilExecucao>();
foreach (var pastaFonte in Directory.EnumerateDirectories(pastaDados).Order())
{
    var fonte = Path.GetFileName(pastaFonte);
    if (fontes.Count > 0 && !fontes.Contains(fonte, StringComparer.OrdinalIgnoreCase)) continue;
    foreach (var pastaExec in Directory.EnumerateDirectories(pastaFonte).Order())
    {
        var id = Path.GetFileName(pastaExec);
        if (idFiltro is not null && id != idFiltro) continue;
        if (!File.Exists(Path.Combine(pastaExec, "execucao.json"))) continue;
        Console.WriteLine($"Perfilando {fonte}/{id}...");
        execucoes.Add(PerfilExecucao.Montar(fonte, id, pastaExec, regras));
    }
}

if (execucoes.Count == 0) { Console.Error.WriteLine("Nenhuma execução encontrada com esse filtro."); return 2; }

Directory.CreateDirectory(saida);
Saida.Gravar(saida, execucoes, regras);
Console.WriteLine($"{execucoes.Count} execuções perfiladas. Saída: {saida}");
return 0;

static string LocalizarRaiz()
{
    for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        if (File.Exists(Path.Combine(d.FullName, "Sonda.sln"))) return d.FullName;
    return Directory.GetCurrentDirectory();
}

/// <summary>Tudo o que o perfilador apura de uma execução.</summary>
public sealed class PerfilExecucao
{
    public string Fonte { get; init; } = "";
    public string Id { get; init; } = "";
    public List<Segmento> Segmentos { get; init; } = new();
    public double? LimitePorMinuto { get; init; }
    public double FracaoLimite { get; init; }
    public List<Tentativa> Tentativas { get; init; } = new();
    public Dictionary<string, MetricasEndpoint> Endpoints { get; init; } = new();
    public List<Evento429> Eventos429 { get; init; } = new();
    public PerfilCampos Campos { get; init; } = null!;
    public PapelExecucao Papel { get; init; } = new();
    public int? Rodada { get; init; }

    public double RitmoConfigurado => (LimitePorMinuto ?? 60) * FracaoLimite;

    /// <summary>Tentativas por minuto entre a primeira e a última tentativa de cada segmento.</summary>
    public double? RitmoObservado
    {
        get
        {
            double minutos = 0; var n = 0;
            foreach (var s in Segmentos)
            {
                var fim = s.Fim ?? DateTimeOffset.MaxValue;
                var doSeg = Tentativas.Where(t => t.Ts >= s.Inicio && t.Ts <= fim).ToList();
                if (doSeg.Count < 2) continue;
                minutos += (doSeg[^1].Ts - doSeg[0].Ts).TotalMinutes;
                n += doSeg.Count - 1; // intervalos entre tentativas
            }
            return minutos > 0 ? n / minutos : null;
        }
    }

    public SortedSet<string> HeadersLimite => new(Tentativas.Where(t => t.HeadersLimite is not null).SelectMany(t => t.HeadersLimite!.Keys));

    public static PerfilExecucao Montar(string fonte, string id, string pasta, RegrasPerfil regras)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(pasta, "execucao.json")));
        var m = doc.RootElement;
        var segmentos = m.GetProperty("segmentos").EnumerateArray().Select(s => new Segmento(
            s.GetProperty("inicio").GetDateTimeOffset(),
            s.TryGetProperty("fim", out var f) && f.ValueKind == JsonValueKind.String ? f.GetDateTimeOffset() : null,
            s.GetProperty("commit").GetString() ?? "",
            s.TryGetProperty("commitSujo", out var cs) && cs.GetBoolean(),
            s.GetProperty("situacao").GetString() ?? "",
            s.TryGetProperty("detalhe", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null)).ToList();
        var cfg = m.GetProperty("config");
        double? limite = cfg.TryGetProperty("limitePorMinuto", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetDouble() : null;
        var fracao = cfg.TryGetProperty("fracaoLimite", out var fr) && fr.ValueKind == JsonValueKind.Number ? fr.GetDouble() : 0.6;

        var regrasFonte = regras.DaFonte(fonte);
        var campos = new PerfilCampos(regras, regrasFonte);
        campos.LerBruto(pasta);
        campos.LerArquivos(pasta);

        var tentativas = PerfilRequisicoes.Ler(Path.Combine(pasta, "requisicoes.jsonl"));
        var endpoints = PerfilRequisicoes.Agregar(tentativas, regrasFonte,
            (ep, chave) => campos.Corpos.GetValueOrDefault((ep, chave)));

        return new PerfilExecucao
        {
            Fonte = fonte, Id = id, Segmentos = segmentos, LimitePorMinuto = limite, FracaoLimite = fracao,
            Papel = regras.Papel(fonte, id), Rodada = regras.RodadaDe(segmentos[0].Inicio),
            Tentativas = tentativas, Endpoints = endpoints, Eventos429 = PerfilRequisicoes.Eventos429(tentativas),
            Campos = campos,
        };
    }
}

public sealed record Segmento(DateTimeOffset Inicio, DateTimeOffset? Fim, string Commit, bool CommitSujo, string Situacao, string? Detalhe);
