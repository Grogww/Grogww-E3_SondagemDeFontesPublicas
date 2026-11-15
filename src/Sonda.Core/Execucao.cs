using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Sonda.Core;

/// <summary>
/// Uma execução datada de uma fonte: pasta dados/&lt;fonte&gt;/&lt;id&gt;/ com
/// execucao.json (manifesto), requisicoes.jsonl (log por tentativa) e bruto/&lt;endpoint&gt;.jsonl.
/// </summary>
public sealed class Execucao : IDisposable
{
    public string Fonte { get; }
    public string Id { get; }
    public string Pasta { get; }
    public ConfigFonte Config { get; }
    public ClienteSonda Http { get; }

    private readonly Manifesto _manifesto;
    private readonly Segmento _segmento;
    private readonly StreamWriter _log;
    private readonly Dictionary<string, StreamWriter> _brutos = new();
    private readonly Dictionary<string, Dictionary<string, RespostaSonda>> _coletados = new();
    private readonly object _trava = new();

    private Execucao(string raiz, ConfigGeral geral, string fonte, string? id, string? chave, HttpMessageHandler? handler)
    {
        Fonte = fonte;
        var retomada = id is not null;
        Id = id ?? DateTime.Now.ToString("yyyyMMdd-HHmmss");
        Pasta = Path.Combine(raiz, geral.PastaDados, fonte, Id);

        var caminhoManifesto = Path.Combine(Pasta, "execucao.json");
        if (retomada && !File.Exists(caminhoManifesto))
            throw new InvalidOperationException($"Execução não encontrada para retomada: {Pasta}");
        if (!retomada && Directory.Exists(Pasta))
            throw new InvalidOperationException($"Pasta de execução já existe: {Pasta}");
        Directory.CreateDirectory(Path.Combine(Pasta, "bruto"));

        _manifesto = retomada
            ? JsonSerializer.Deserialize<Manifesto>(File.ReadAllText(caminhoManifesto), ConfigGeral.Json)!
            : new Manifesto { Fonte = fonte, Id = Id, UserAgent = geral.UserAgent, Config = geral.Fontes[fonte] };
        // A retomada usa a configuração gravada na abertura, para manter a mesma amostra.
        Config = _manifesto.Config;

        var (commit, sujo) = LerCommit(raiz);
        _segmento = new Segmento { Inicio = DateTimeOffset.Now, Commit = commit, CommitSujo = sujo, Situacao = "em-andamento" };
        _manifesto.Segmentos.Add(_segmento);
        GravarManifesto();

        _log = new StreamWriter(Path.Combine(Pasta, "requisicoes.jsonl"), append: true, new UTF8Encoding(false)) { AutoFlush = true };
        Http = new ClienteSonda(this, Config, geral.UserAgent, chave, handler);

        Registrar($"Execução {Fonte}/{Id} ({(retomada ? "retomada" : "nova")}), commit {commit}{(sujo ? " com alterações não commitadas" : "")}");
    }

    /// <summary>Abre uma execução nova (id nulo) ou retoma uma existente.</summary>
    public static Execucao Abrir(string raiz, ConfigGeral geral, string fonte, string? id = null, string? chave = null, HttpMessageHandler? handler = null)
        => new(raiz, geral, fonte, id, chave, handler);

    /// <summary>Caminho absoluto dentro da pasta da execução.</summary>
    public string Caminho(string relativo) => Path.Combine(Pasta, relativo);

    public void Registrar(string mensagem) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {mensagem}");

    public void Finalizar(string situacao, string? detalhe = null)
    {
        _segmento.Fim = DateTimeOffset.Now;
        _segmento.Situacao = situacao;
        _segmento.Detalhe = detalhe;
        GravarManifesto();
        Registrar($"Situação: {situacao}. Pasta: {Pasta}");
    }

    internal void GravarRequisicao(RegistroRequisicao r)
    {
        var linha = JsonSerializer.Serialize(r, ConfigGeral.JsonLinha);
        lock (_trava) _log.WriteLine(linha);
    }

    /// <summary>Resposta final já coletada nesta execução (2xx ou 4xx exceto 429).</summary>
    internal bool TryObterColetado(string endpoint, string chave, out RespostaSonda resposta)
    {
        lock (_trava) return Indice(endpoint).TryGetValue(chave, out resposta!);
    }

    internal void GravarBruto(string endpoint, string chave, string url, int status, string corpo)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Encoder = ConfigGeral.Json.Encoder }))
        {
            w.WriteStartObject();
            w.WriteString("chave", chave);
            w.WriteString("url", url);
            w.WriteNumber("status", status);
            w.WriteString("coletadoEm", DateTimeOffset.Now);
            JsonDocument? doc = null;
            try { doc = JsonDocument.Parse(corpo); } catch (JsonException) { }
            w.WriteBoolean("corpoJson", doc is not null);
            w.WritePropertyName("corpo");
            if (doc is not null) { doc.RootElement.WriteTo(w); doc.Dispose(); }
            else w.WriteStringValue(corpo);
            w.WriteEndObject();
        }
        var linha = Encoding.UTF8.GetString(ms.ToArray());

        lock (_trava)
        {
            if (!_brutos.TryGetValue(endpoint, out var sw))
            {
                sw = new StreamWriter(CaminhoBruto(endpoint), append: true, new UTF8Encoding(false)) { AutoFlush = true };
                _brutos[endpoint] = sw;
            }
            sw.WriteLine(linha);
            if (status is >= 200 and < 500 and not 429)
                Indice(endpoint)[chave] = new RespostaSonda(status, corpo, DaExecucaoAnterior: true);
        }
    }

    // Carrega, na primeira consulta ao endpoint, as respostas já gravadas (retomada idempotente).
    // O corpo devolvido de JSON é a re-serialização compacta do original (conteúdo equivalente).
    private Dictionary<string, RespostaSonda> Indice(string endpoint)
    {
        if (_coletados.TryGetValue(endpoint, out var indice)) return indice;
        indice = new();
        var caminho = CaminhoBruto(endpoint);
        if (File.Exists(caminho))
        {
            foreach (var linha in File.ReadLines(caminho))
            {
                if (string.IsNullOrWhiteSpace(linha)) continue;
                using var doc = JsonDocument.Parse(linha);
                var r = doc.RootElement;
                var status = r.GetProperty("status").GetInt32();
                if (status is < 200 or >= 500 or 429) continue;
                var corpo = r.GetProperty("corpoJson").GetBoolean() ? r.GetProperty("corpo").GetRawText() : r.GetProperty("corpo").GetString()!;
                indice[r.GetProperty("chave").GetString()!] = new RespostaSonda(status, corpo, DaExecucaoAnterior: true);
            }
        }
        _coletados[endpoint] = indice;
        return indice;
    }

    private string CaminhoBruto(string endpoint)
    {
        var nome = string.Concat(endpoint.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
        return Path.Combine(Pasta, "bruto", nome + ".jsonl");
    }

    private void GravarManifesto() =>
        File.WriteAllText(Path.Combine(Pasta, "execucao.json"), JsonSerializer.Serialize(_manifesto, ConfigGeral.Json));

    private static (string commit, bool sujo) LerCommit(string raiz)
    {
        string? Git(string args)
        {
            try
            {
                var psi = new ProcessStartInfo("git", $"-C \"{raiz}\" {args}") { RedirectStandardOutput = true, RedirectStandardError = true };
                using var p = Process.Start(psi)!;
                var saida = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit();
                return p.ExitCode == 0 ? saida : null;
            }
            catch { return null; }
        }
        var commit = Git("rev-parse HEAD");
        return commit is null ? ("sem-commit", true) : (commit, Git("status --porcelain") is not "");
    }

    public void Dispose()
    {
        Http.Dispose();
        _log.Dispose();
        foreach (var sw in _brutos.Values) sw.Dispose();
    }

    private sealed class Manifesto
    {
        public string Fonte { get; set; } = "";
        public string Id { get; set; } = "";
        public string UserAgent { get; set; } = "";
        public ConfigFonte Config { get; set; } = new();
        public List<Segmento> Segmentos { get; set; } = new();
    }

    /// <summary>Cada disparo (inicial ou retomada) registra seu próprio período e commit.</summary>
    private sealed class Segmento
    {
        public DateTimeOffset Inicio { get; set; }
        public DateTimeOffset? Fim { get; set; }
        public string Commit { get; set; } = "";
        public bool CommitSujo { get; set; }
        public string Situacao { get; set; } = "";
        public string? Detalhe { get; set; }
    }
}
