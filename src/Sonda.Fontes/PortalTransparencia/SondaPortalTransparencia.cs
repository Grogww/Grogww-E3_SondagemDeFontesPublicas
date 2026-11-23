using System.Text.Json;
using Sonda.Core;

namespace Sonda.Fontes.PortalTransparencia;

/// <summary>
/// Sonda do Portal da Transparência (fichas 3, 4 e 5): CEIS, CNEP e CEPIM pela API de Dados.
/// Os três cadastros compartilham host, chave (cabeçalho chave-api-dados) e limite de requisições.
/// Contrato: docs/portCeis/swagger-recorte.json (GET /api-de-dados/{ceis|cnep|cepim} e /{id}).
/// Recorte SC:
/// CEIS e CNEP não têm filtro de UF; SC só é identificável no retorno, por
/// orgaoSancionador.siglaUf (UF do órgão sancionador, não do sancionado);
/// CEPIM filtra na origem por ufSancionado e não devolve UF no retorno.
/// Paginação: só o parâmetro pagina (obrigatório). O tamanho da página não é
/// informado no Swagger nem na página da API; a série termina na primeira página
/// vazia ou em maxPaginas.
/// Divergência documental (ficha 5): o resumo do GET /cepim cita CPF e a página da
/// API cita período, mas os parâmetros do Swagger não trazem nenhum dos dois; vale o Swagger.
/// </summary>
public sealed class SondaPortalTransparencia : ISonda
{
    public string Nome => "portal-transparencia";

    public async Task ExecutarAsync(Execucao execucao, CancellationToken ct)
    {
        var cfg = execucao.Config;
        var uf = cfg.Parametro<string>("uf");
        var cadastros = cfg.Parametro<List<ConfigCadastro>>("cadastros");
        var cruzamento = cfg.Parametro<ConfigCruzamento>("cruzamento");

        // Snapshot do Swagger: requisição com a mesma chave das demais (host da API).
        var swagger = cfg.Parametro<ConfigSwagger>("swagger");
        if (swagger.Url.Length > 0)
        {
            var arq = await execucao.Http.BaixarAsync("swagger", "snapshot", swagger.Url, swagger.Arquivo, ct);
            if (!arq.Sucesso) execucao.Registrar($"swagger: falhou (status {arq.Status})");
        }

        var cnpjs = new List<string>();

        foreach (var c in cadastros)
        {
            var registros = new List<JsonElement>();
            foreach (var s in c.Series)
            {
                var (lidos, negado) = await PaginarAsync(execucao, c, s, ct);
                if (negado)
                {
                    // Chave ausente, inválida ou sem permissão: falha de acesso é resultado da sondagem.
                    execucao.Registrar($"{c.Endpoint}: acesso negado; sondagem interrompida");
                    return;
                }
                registros.AddRange(lidos);
            }

            // SC no retorno (CEIS/CNEP) ou já filtrada na origem (CEPIM, campoUf ausente).
            var deSc = c.CampoUf is { Length: > 0 }
                ? registros.Where(e => Texto(e, c.CampoUf) == uf).ToList()
                : registros;
            execucao.Registrar($"{c.Endpoint}: {registros.Count} registros lidos, {deSc.Count} de {uf}");

            // Consulta individual pelo id interno do registro.
            var ids = deSc
                .Select(e => e.TryGetProperty("id", out var i) && i.TryGetInt32(out var v) ? v : (int?)null)
                .OfType<int>()
                .Distinct()
                .Take(c.DetalharIds);
            foreach (var id in ids)
                await execucao.Http.ObterAsync($"{c.Endpoint}-id", $"id={id}", $"{c.Caminho}/{id}", ct);

            // CNPJ do sancionado (só os de 14 dígitos; CPF vem mascarado e não é consultado).
            cnpjs.AddRange(deSc
                .Select(e => Digitos(Texto(e, c.CampoCnpj)))
                .Where(d => d.Length == 14)
                .Distinct()
                .Take(cruzamento.CnpjsPorCadastro));
        }

        // Consulta por CNPJ do sancionado nos três cadastros: é o uso previsto nas fichas
        // ("consulta por CNPJ"). O formato do valor (com ou sem máscara) não é documentado;
        // envia só os dígitos.
        cnpjs = cnpjs.Distinct().ToList();
        execucao.Registrar($"cruzamento: {cnpjs.Count} CNPJs em {cadastros.Count} cadastros");
        foreach (var cnpj in cnpjs)
            foreach (var c in cadastros)
                await execucao.Http.ObterAsync($"{c.Endpoint}-cnpj", $"{c.ParametroCnpj}={cnpj};pagina=1",
                    $"{c.Caminho}?{c.ParametroCnpj}={cnpj}&pagina=1", ct);
    }

    /// <summary>Percorre as páginas de uma série até a primeira vazia ou até maxPaginas.</summary>
    private static async Task<(List<JsonElement> Registros, bool Negado)> PaginarAsync(
        Execucao execucao, ConfigCadastro c, ConfigSerie s, CancellationToken ct)
    {
        var registros = new List<JsonElement>();
        for (var pagina = 1; pagina <= s.MaxPaginas; pagina++)
        {
            var parametros = s.Filtros
                .Append(new("pagina", pagina.ToString()))
                .ToList();
            var query = string.Join("&", parametros.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));
            var chave = string.Join(";", parametros.Select(p => $"{p.Key}={p.Value}"));

            var r = await execucao.Http.ObterAsync(c.Endpoint, chave, $"{c.Caminho}?{query}", ct);
            if (r.Status is 401 or 403) return (registros, true);
            // Erro ou corpo que não é vetor encerra a série; o status fica no log do núcleo.
            if (!r.Sucesso || Ler(r.Corpo) is not { ValueKind: JsonValueKind.Array } corpo) break;

            var itens = corpo.EnumerateArray().ToList();
            if (itens.Count == 0) break;
            registros.AddRange(itens);
        }
        return (registros, false);
    }

    private static JsonElement? Ler(string? corpo)
    {
        if (string.IsNullOrWhiteSpace(corpo)) return null;
        try { return JsonSerializer.Deserialize<JsonElement>(corpo); }
        catch (JsonException) { return null; }
    }

    /// <summary>Texto em um caminho de atributos separados por ponto (ex.: "orgaoSancionador.siglaUf").</summary>
    private static string? Texto(JsonElement e, string caminho)
    {
        foreach (var parte in caminho.Split('.'))
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(parte, out e)) return null;
        }
        return e.ValueKind == JsonValueKind.String ? e.GetString() : null;
    }

    private static string Digitos(string? texto) =>
        texto is null ? "" : new string(texto.Where(char.IsAsciiDigit).ToArray());

    private sealed class ConfigSwagger
    {
        public string Url { get; set; } = "";
        public string Arquivo { get; set; } = "swagger.json";
    }

    private sealed class ConfigCadastro
    {
        public string Endpoint { get; set; } = "";
        public string Caminho { get; set; } = "";
        /// <summary>Atributo do retorno com a UF; ausente quando o filtro é na origem.</summary>
        public string? CampoUf { get; set; }
        public string CampoCnpj { get; set; } = "";
        public string ParametroCnpj { get; set; } = "";
        public List<ConfigSerie> Series { get; set; } = new();
        public int DetalharIds { get; set; }
    }

    private sealed class ConfigSerie
    {
        public Dictionary<string, string> Filtros { get; set; } = new();
        public int MaxPaginas { get; set; } = 1;
    }

    private sealed class ConfigCruzamento
    {
        public int CnpjsPorCadastro { get; set; }
    }
}
