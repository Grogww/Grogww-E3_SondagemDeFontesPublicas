using System.Diagnostics;
using System.Net;

namespace Sonda.Core;

/// <summary>Resposta final de uma requisição. Status 0: falha sem resposta HTTP (ver Erro).</summary>
public sealed record RespostaSonda(int Status, string Corpo, bool DaExecucaoAnterior, string? Erro = null)
{
    public bool Sucesso => Status is >= 200 and < 300;
}

/// <summary>Resultado de um download. Caminho nulo quando não houve arquivo.</summary>
public sealed record ArquivoSonda(int Status, string? Caminho, long Bytes, bool DaExecucaoAnterior, string? Erro = null)
{
    public bool Sucesso => Caminho is not null;
}

/// <summary>Lançada ao receber 429: a execução para e pode ser retomada depois.</summary>
public sealed class LimiteAtingidoException(string endpoint, string? retryAfter)
    : Exception($"HTTP 429 em {endpoint}" + (retryAfter is null ? "" : $" (Retry-After: {retryAfter})"));

/// <summary>Uma linha de requisicoes.jsonl (uma por tentativa).</summary>
internal sealed record RegistroRequisicao(
    DateTimeOffset Ts, string Endpoint, string Chave, string Url, int Tentativa, bool Final,
    int Status, double? TtfbMs, double LatenciaMs, long? Bytes,
    Dictionary<string, string>? HeadersLimite, string? Erro);

/// <summary>
/// Único caminho de rede das sondas: throttle por fonte, retry só para 5xx/timeout,
/// parada em 429, log por tentativa e persistência bruta.
/// </summary>
public sealed class ClienteSonda : IDisposable
{
    private readonly Execucao _exec;
    private readonly ConfigFonte _cfg;
    private readonly HttpClient _http;
    private readonly string? _chave;
    private readonly SemaphoreSlim _vez = new(1, 1);
    private long _proximaLiberacao; // Stopwatch ticks

    internal ClienteSonda(Execucao exec, ConfigFonte cfg, string userAgent, string? chave, HttpMessageHandler? handler)
    {
        _exec = exec;
        _cfg = cfg;
        _chave = chave;
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = Timeout.InfiniteTimeSpan, // o timeout é aplicado por tentativa
        };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);
    }

    /// <summary>
    /// GET com corpo em texto. <paramref name="chave"/> identifica a requisição na execução
    /// (ex.: "pagina=3"); se já houver resposta final gravada, ela é devolvida sem nova requisição.
    /// <paramref name="url"/> pode ser absoluta ou relativa a BaseUrl.
    /// </summary>
    public async Task<RespostaSonda> ObterAsync(string endpoint, string chave, string url, CancellationToken ct)
    {
        if (_exec.TryObterColetado(endpoint, chave, out var anterior)) return anterior;
        var uri = Montar(url);

        for (var tentativa = 1; ; tentativa++)
        {
            await AguardarVezAsync(ct);
            var ultima = tentativa >= _cfg.MaxTentativas;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_cfg.TimeoutSegundos));
            var sw = Stopwatch.StartNew();
            try
            {
                using var req = Requisicao(uri);
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                var ttfb = sw.Elapsed.TotalMilliseconds;
                var corpo = await resp.Content.ReadAsStringAsync(cts.Token);
                var status = (int)resp.StatusCode;
                var repetir = status >= 500 && !ultima;
                Log(endpoint, chave, uri, tentativa, !repetir, status, ttfb, sw, System.Text.Encoding.UTF8.GetByteCount(corpo), resp, null);

                if (status == 429) throw new LimiteAtingidoException(endpoint, RetryAfter(resp));
                if (repetir) { await EsperarRepeticaoAsync(tentativa, ct); continue; }
                if (status < 500) _exec.GravarBruto(endpoint, chave, uri.ToString(), status, corpo);
                return new RespostaSonda(status, corpo, false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Log(endpoint, chave, uri, tentativa, ultima, 0, null, sw, null, null, "timeout");
                if (ultima) return new RespostaSonda(0, "", false, "timeout");
                await EsperarRepeticaoAsync(tentativa, ct);
            }
            catch (HttpRequestException e)
            {
                Log(endpoint, chave, uri, tentativa, true, 0, null, sw, null, null, e.Message);
                return new RespostaSonda(0, "", false, e.Message);
            }
        }
    }

    /// <summary>
    /// Download em fluxo para um arquivo dentro da pasta da execução (ex.: snapshot do Swagger, ZIP, CSV).
    /// Se o arquivo já existir, não baixa de novo. O timeout vale até a chegada dos headers.
    /// </summary>
    public async Task<ArquivoSonda> BaixarAsync(string endpoint, string chave, string url, string caminhoRelativo, CancellationToken ct)
    {
        var destino = _exec.Caminho(caminhoRelativo);
        if (File.Exists(destino)) return new ArquivoSonda(200, destino, new FileInfo(destino).Length, true);
        Directory.CreateDirectory(Path.GetDirectoryName(destino)!);
        var uri = Montar(url);

        for (var tentativa = 1; ; tentativa++)
        {
            await AguardarVezAsync(ct);
            var ultima = tentativa >= _cfg.MaxTentativas;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_cfg.TimeoutSegundos));
            var sw = Stopwatch.StartNew();
            double? ttfb = null;
            try
            {
                using var req = Requisicao(uri);
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                ttfb = sw.Elapsed.TotalMilliseconds;
                cts.CancelAfter(Timeout.InfiniteTimeSpan);
                var status = (int)resp.StatusCode;

                if (!resp.IsSuccessStatusCode)
                {
                    var repetir = status >= 500 && !ultima;
                    Log(endpoint, chave, uri, tentativa, !repetir, status, ttfb, sw, null, resp, null);
                    if (status == 429) throw new LimiteAtingidoException(endpoint, RetryAfter(resp));
                    if (repetir) { await EsperarRepeticaoAsync(tentativa, ct); continue; }
                    return new ArquivoSonda(status, null, 0, false);
                }

                var parcial = destino + ".parcial";
                long bytes;
                await using (var arquivo = File.Create(parcial))
                {
                    await resp.Content.CopyToAsync(arquivo, cts.Token);
                    bytes = arquivo.Length;
                }
                File.Move(parcial, destino, overwrite: true);
                Log(endpoint, chave, uri, tentativa, true, status, ttfb, sw, bytes, resp, null);
                return new ArquivoSonda(status, destino, bytes, false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Log(endpoint, chave, uri, tentativa, ultima, 0, ttfb, sw, null, null, "timeout");
                if (ultima) return new ArquivoSonda(0, null, 0, false, "timeout");
                await EsperarRepeticaoAsync(tentativa, ct);
            }
            catch (HttpRequestException e)
            {
                Log(endpoint, chave, uri, tentativa, true, 0, ttfb, sw, null, null, e.Message);
                return new ArquivoSonda(0, null, 0, false, e.Message);
            }
        }
    }

    // Espaçamento mínimo entre requisições: 60 s / (limite declarado × fração).
    private async Task AguardarVezAsync(CancellationToken ct)
    {
        await _vez.WaitAsync(ct);
        try
        {
            var espera = _proximaLiberacao - Stopwatch.GetTimestamp();
            if (espera > 0) await Task.Delay(TimeSpan.FromSeconds((double)espera / Stopwatch.Frequency), ct);
            _proximaLiberacao = Stopwatch.GetTimestamp() + (long)(_cfg.IntervaloEntreRequisicoes.TotalSeconds * Stopwatch.Frequency);
        }
        finally { _vez.Release(); }
    }

    private static Task EsperarRepeticaoAsync(int tentativa, CancellationToken ct) =>
        Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, tentativa)), ct);

    private HttpRequestMessage Requisicao(Uri uri)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, uri);
        if (_cfg.HeaderChave is not null && _chave is not null)
            req.Headers.TryAddWithoutValidation(_cfg.HeaderChave, _chave);
        return req;
    }

    private Uri Montar(string url) =>
        new(url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : _cfg.BaseUrl.TrimEnd('/') + "/" + url.TrimStart('/'));

    private static string? RetryAfter(HttpResponseMessage resp) =>
        resp.Headers.TryGetValues("Retry-After", out var v) ? string.Join(",", v) : null;

    // Guarda só headers ligados a limite de requisição; a chave de acesso nunca é registrada.
    private static Dictionary<string, string>? HeadersLimite(HttpResponseMessage? resp)
    {
        if (resp is null) return null;
        var d = resp.Headers.Concat(resp.Content.Headers)
            .Where(h => h.Key.Contains("ratelimit", StringComparison.OrdinalIgnoreCase)
                     || h.Key.Contains("rate-limit", StringComparison.OrdinalIgnoreCase)
                     || h.Key.StartsWith("x-rate", StringComparison.OrdinalIgnoreCase)
                     || h.Key.Equals("Retry-After", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(h => h.Key, h => string.Join(",", h.Value));
        return d.Count == 0 ? null : d;
    }

    private void Log(string endpoint, string chave, Uri uri, int tentativa, bool final, int status,
        double? ttfb, Stopwatch sw, long? bytes, HttpResponseMessage? resp, string? erro) =>
        _exec.GravarRequisicao(new RegistroRequisicao(
            DateTimeOffset.Now, endpoint, chave, uri.ToString(), tentativa, final, status,
            ttfb is null ? null : Math.Round(ttfb.Value, 1), Math.Round(sw.Elapsed.TotalMilliseconds, 1),
            bytes, HeadersLimite(resp), erro));

    public void Dispose() => _http.Dispose();
}
