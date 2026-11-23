using System.Text.Json;

namespace Sonda.Perfilador;

/// <summary>Uma linha de requisicoes.jsonl (uma por tentativa).</summary>
public sealed record Tentativa(
    DateTimeOffset Ts, string Endpoint, string Chave, string Url, int Numero, bool Final,
    int Status, double? TtfbMs, double LatenciaMs, long? Bytes,
    Dictionary<string, string>? HeadersLimite, string? Erro);

/// <summary>Resultado de uma requisição (tentativa final), na ótica da sondagem.</summary>
public enum Desfecho { Ok, Vazio, Erro4xx, Erro5xx, FalhaRede, Limite429 }

/// <summary>Métricas de acesso de um endpoint numa execução.</summary>
public sealed class MetricasEndpoint
{
    public string Endpoint { get; init; } = "";
    public int Tentativas { get; set; }
    public int Requisicoes { get; set; }
    public int Retentativas => Tentativas - Requisicoes;
    public SortedDictionary<int, int> PorStatus { get; } = new();
    public Dictionary<Desfecho, int> PorDesfecho { get; } = Enum.GetValues<Desfecho>().ToDictionary(d => d, _ => 0);
    public List<double> Latencias { get; } = new();
    public List<double> Ttfbs { get; } = new();
    public long Bytes { get; set; }
    public HashSet<string> Erros { get; } = new();

    public int Falhas => PorDesfecho[Desfecho.Erro4xx] + PorDesfecho[Desfecho.Erro5xx]
                       + PorDesfecho[Desfecho.FalhaRede] + PorDesfecho[Desfecho.Limite429];
    public double? TaxaErro => Requisicoes == 0 ? null : (double)Falhas / Requisicoes;
    public double? TaxaVazio => Requisicoes == 0 ? null : (double)PorDesfecho[Desfecho.Vazio] / Requisicoes;
}

/// <summary>Uma resposta 429: quando veio e em que ritmo a sonda estava.</summary>
public sealed record Evento429(
    string Endpoint, string Chave, int Ordem, double SegundosDesdeInicio,
    int TentativasUltimos60s, string? RetryAfter, string? HeadersLimite);

public static class PerfilRequisicoes
{
    public static List<Tentativa> Ler(string caminho)
    {
        var lista = new List<Tentativa>();
        if (!File.Exists(caminho)) return lista;
        foreach (var linha in File.ReadLines(caminho))
        {
            if (string.IsNullOrWhiteSpace(linha)) continue;
            using var doc = JsonDocument.Parse(linha);
            var r = doc.RootElement;
            Dictionary<string, string>? headers = null;
            if (r.TryGetProperty("headersLimite", out var h) && h.ValueKind == JsonValueKind.Object)
                headers = h.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ToString());
            lista.Add(new Tentativa(
                r.GetProperty("ts").GetDateTimeOffset(),
                r.GetProperty("endpoint").GetString()!,
                r.GetProperty("chave").GetString()!,
                r.GetProperty("url").GetString()!,
                r.GetProperty("tentativa").GetInt32(),
                r.GetProperty("final").GetBoolean(),
                r.GetProperty("status").GetInt32(),
                r.TryGetProperty("ttfbMs", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetDouble() : null,
                r.GetProperty("latenciaMs").GetDouble(),
                r.TryGetProperty("bytes", out var b) && b.ValueKind == JsonValueKind.Number ? b.GetInt64() : null,
                headers,
                r.TryGetProperty("erro", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null));
        }
        return lista.OrderBy(x => x.Ts).ToList();
    }

    /// <summary>
    /// Classifica a tentativa final. 204 e as regras de "vazio" da fonte não são erro;
    /// <paramref name="corpo"/> é o corpo bruto gravado para (endpoint, chave), se houver.
    /// </summary>
    public static Desfecho Classificar(Tentativa t, RegrasFonte regras, Func<string, string, CorpoBruto?> corpo)
    {
        if (t.Status == 0) return Desfecho.FalhaRede;
        if (t.Status == 429) return Desfecho.Limite429;
        if (t.Status == 204) return Desfecho.Vazio;
        foreach (var v in regras.Vazio)
        {
            if (v.Endpoint != t.Endpoint || v.Status != t.Status) continue;
            if (v.MensagemContem is null) return Desfecho.Vazio;
            var c = corpo(t.Endpoint, t.Chave);
            if (c is not null && c.Contem(v.MensagemContem)) return Desfecho.Vazio;
        }
        return t.Status switch
        {
            >= 200 and < 300 => Desfecho.Ok,
            >= 400 and < 500 => Desfecho.Erro4xx,
            >= 500 => Desfecho.Erro5xx,
            _ => Desfecho.Erro4xx, // 1xx/3xx finais não deveriam ocorrer; ficam como falha de cliente
        };
    }

    public static Dictionary<string, MetricasEndpoint> Agregar(
        List<Tentativa> tentativas, RegrasFonte regras, Func<string, string, CorpoBruto?> corpo)
    {
        var por = new Dictionary<string, MetricasEndpoint>();
        foreach (var t in tentativas)
        {
            if (!por.TryGetValue(t.Endpoint, out var m)) por[t.Endpoint] = m = new MetricasEndpoint { Endpoint = t.Endpoint };
            m.Tentativas++;
            // Tempo só de tentativas com resposta HTTP (inclui as que foram repetidas).
            if (t.Status > 0)
            {
                m.Latencias.Add(t.LatenciaMs);
                if (t.TtfbMs is { } ttfb) m.Ttfbs.Add(ttfb);
            }
            if (t.Erro is not null) m.Erros.Add(t.Erro);
            if (!t.Final) continue;
            m.Requisicoes++;
            m.PorStatus[t.Status] = m.PorStatus.GetValueOrDefault(t.Status) + 1;
            m.PorDesfecho[Classificar(t, regras, corpo)]++;
            m.Bytes += t.Bytes ?? 0;
        }
        return por;
    }

    public static List<Evento429> Eventos429(List<Tentativa> tentativas)
    {
        var eventos = new List<Evento429>();
        if (tentativas.Count == 0) return eventos;
        var inicio = tentativas[0].Ts;
        for (var i = 0; i < tentativas.Count; i++)
        {
            var t = tentativas[i];
            if (t.Status != 429) continue;
            var janela = tentativas.Take(i + 1).Count(x => x.Ts > t.Ts.AddSeconds(-60));
            var headers = t.HeadersLimite;
            eventos.Add(new Evento429(
                t.Endpoint, t.Chave, i + 1, (t.Ts - inicio).TotalSeconds, janela,
                headers?.FirstOrDefault(h => h.Key.Equals("Retry-After", StringComparison.OrdinalIgnoreCase)).Value,
                headers is null ? null : string.Join(", ", headers.Select(h => $"{h.Key}={h.Value}"))));
        }
        return eventos;
    }

    /// <summary>Percentil com interpolação linear entre postos (igual ao PERCENTIL.INC do Excel).</summary>
    public static double? Percentil(List<double> valores, double p)
    {
        if (valores.Count == 0) return null;
        var v = valores.Order().ToArray();
        var pos = (v.Length - 1) * p;
        var baixo = (int)Math.Floor(pos);
        var alto = (int)Math.Ceiling(pos);
        return v[baixo] + (v[alto] - v[baixo]) * (pos - baixo);
    }
}
