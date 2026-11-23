using System.Text.Json;
using Sonda.Core;

namespace Sonda.Fontes.Pncp;

/// <summary>
/// Sonda do PNCP (fichas 1 e 2). Duas APIs:
/// Consultas (BaseUrl, /api/consulta): listas paginadas por período;
/// Integração (baseUrlIntegracao, /api/pncp): só GET públicos por chave (órgão, ano, sequencial).
/// A Integração não tem busca ampla (ficha 1, "Condicionado"): as chaves usadas nela
/// saem das respostas da API de Consultas.
/// Recorte SC: o parâmetro uf filtra na origem só nas consultas de contratações
/// (UF da unidade administrativa do órgão). Contratos trazem unidadeOrgao.ufSigla
/// apenas no retorno; atas não trazem UF. A UF do fornecedor só aparece em
/// localidadeFornecedor, no resultado do item (Integração).
/// </summary>
public sealed class SondaPncp : ISonda
{
    public string Nome => "pncp";

    public async Task ExecutarAsync(Execucao execucao, CancellationToken ct)
    {
        var cfg = execucao.Config;
        var integracao = cfg.Parametro<string>("baseUrlIntegracao").TrimEnd('/');
        var uf = cfg.Parametro<string>("uf");
        var detalhes = cfg.Parametro<ConfigDetalhes>("detalhes");

        foreach (var s in cfg.Parametro<List<ConfigSwagger>>("swagger"))
        {
            var arq = await execucao.Http.BaixarAsync("swagger", s.Chave, s.Url, s.Arquivo, ct);
            if (!arq.Sucesso) execucao.Registrar($"swagger {s.Chave}: falhou (status {arq.Status})");
        }

        // Tabelas de domínio da Integração: uma requisição cada, sem paginação.
        foreach (var d in cfg.Parametro<List<ConfigDominio>>("dominios"))
            await execucao.Http.ObterAsync(d.Endpoint, "todos", $"{integracao}/{d.Caminho}", ct);

        var compras = new List<ChaveRegistro>();
        var contratos = new List<ChaveRegistro>();

        foreach (var c in cfg.Parametro<List<ConfigConsulta>>("consultas"))
        {
            // codigoModalidadeContratacao é obrigatório em publicacao e atualizacao: uma série por modalidade.
            var series = c.Modalidades is { Length: > 0 }
                ? c.Modalidades.Select(m => (int?)m)
                : new int?[] { null };

            foreach (var modalidade in series)
            {
                var achados = await PaginarAsync(execucao, c, modalidade, ct);
                if (c.Detalhar == "compras")
                {
                    compras.AddRange(achados
                        .Select(e => Chave(e, "anoCompra", "sequencialCompra"))
                        .OfType<ChaveRegistro>()
                        .Take(detalhes.ComprasPorModalidade));
                }
                else if (c.Detalhar == "contratos")
                {
                    // Sem filtro de UF na origem: SC é separada pelo retorno (unidadeOrgao.ufSigla).
                    contratos.AddRange(achados
                        .Where(e => Texto(e, "unidadeOrgao", "ufSigla") == uf)
                        .Select(e => Chave(e, "anoContrato", "sequencialContrato"))
                        .OfType<ChaveRegistro>());
                }
            }
        }

        compras = compras.Distinct().ToList();
        contratos = contratos.Distinct().Take(detalhes.Contratos).ToList();
        execucao.Registrar($"detalhamento: {compras.Count} contratações e {contratos.Count} contratos de {uf}");

        foreach (var k in compras)
            await DetalharCompraAsync(execucao, integracao, k, detalhes, ct);

        foreach (var k in contratos)
            await execucao.Http.ObterAsync("contrato-chave", k.Id,
                $"{integracao}/v1/orgaos/{k.CnpjUrl}/contratos/{k.Ano}/{k.Sequencial}", ct);

        foreach (var cnpj in compras.Select(k => k.Cnpj).Distinct().Take(detalhes.Orgaos))
        {
            var c = Uri.EscapeDataString(cnpj);
            await execucao.Http.ObterAsync("orgao", cnpj, $"{integracao}/v1/orgaos/{c}", ct);
            await execucao.Http.ObterAsync("orgao-unidades", cnpj, $"{integracao}/v1/orgaos/{c}/unidades", ct);
        }
    }

    /// <summary>Percorre as páginas de uma consulta até o fim ou até maxPaginas; devolve os registros lidos.</summary>
    private static async Task<List<JsonElement>> PaginarAsync(Execucao execucao, ConfigConsulta c, int? modalidade, CancellationToken ct)
    {
        var filtros = new List<KeyValuePair<string, string>>(c.Filtros);
        if (modalidade is not null)
            filtros.Add(new("codigoModalidadeContratacao", modalidade.Value.ToString()));

        var registros = new List<JsonElement>();
        for (var pagina = 1; pagina <= c.MaxPaginas; pagina++)
        {
            var parametros = filtros
                .Append(new("pagina", pagina.ToString()))
                .Append(new("tamanhoPagina", c.TamanhoPagina.ToString()))
                .ToList();
            var query = string.Join("&", parametros.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));
            var chave = string.Join(";", parametros.Select(p => $"{p.Key}={p.Value}"));

            var r = await execucao.Http.ObterAsync(c.Endpoint, chave, $"{c.Caminho}?{query}", ct);
            // 204 (sem registros) e erros encerram a série; o status fica no log do núcleo.
            if (!r.Sucesso || Ler(r.Corpo) is not { ValueKind: JsonValueKind.Object } corpo) break;

            var dados = Itens(corpo);
            registros.AddRange(dados);

            var totalPaginas = corpo.TryGetProperty("totalPaginas", out var t) && t.TryGetInt32(out var n) ? n : 0;
            if (dados.Count == 0 || pagina >= totalPaginas) break;
        }
        execucao.Registrar($"{c.Endpoint}{(modalidade is null ? "" : $" modalidade={modalidade}")}: {registros.Count} registros lidos");
        return registros;
    }

    private static async Task DetalharCompraAsync(Execucao execucao, string integracao, ChaveRegistro k, ConfigDetalhes d, CancellationToken ct)
    {
        var compra = $"v1/orgaos/{k.CnpjUrl}/compras/{k.Ano}/{k.Sequencial}";

        // Mesma contratação pela API de Consultas (endpoint que só consta no Swagger).
        await execucao.Http.ObterAsync("contratacao-chave", k.Id, compra, ct);

        // Itens: só a primeira página. O Swagger e o manual de Integração (§11.13) não informam tamanho máximo.
        var itens = await execucao.Http.ObterAsync("compra-itens", $"{k.Id};pagina=1;tamanhoPagina={d.ItensTamanhoPagina}",
            $"{integracao}/{compra}/itens?pagina=1&tamanhoPagina={d.ItensTamanhoPagina}", ct);

        if (itens.Sucesso && Ler(itens.Corpo) is { } corpoItens)
        {
            // Resultados (fornecedor, porte, natureza jurídica, localidade) só dos itens com temResultado.
            var numeros = Itens(corpoItens)
                .Where(i => i.TryGetProperty("temResultado", out var t) && t.ValueKind == JsonValueKind.True)
                .Select(i => i.TryGetProperty("numeroItem", out var n) && n.TryGetInt32(out var v) ? v : (int?)null)
                .OfType<int>()
                .Take(d.ItensComResultadoPorCompra);

            foreach (var numero in numeros)
                await execucao.Http.ObterAsync("compra-item-resultados", $"{k.Id};item={numero}",
                    $"{integracao}/{compra}/itens/{numero}/resultados", ct);
        }

        // tamanhoPagina de contratos por contratação: máximo 50 no Swagger de Integração.
        await execucao.Http.ObterAsync("compra-contratos", $"{k.Id};pagina=1;tamanhoPagina=50",
            $"{integracao}/v1/orgaos/{k.CnpjUrl}/contratos/contratacao/{k.Ano}/{k.Sequencial}?pagina=1&tamanhoPagina=50", ct);

        // Atas por compra: tamanho de página não documentado; usa o padrão do serviço.
        await execucao.Http.ObterAsync("compra-atas", $"{k.Id};pagina=1", $"{integracao}/{compra}/atas?pagina=1", ct);
    }

    private static JsonElement? Ler(string? corpo)
    {
        if (string.IsNullOrWhiteSpace(corpo)) return null;
        try { return JsonSerializer.Deserialize<JsonElement>(corpo); }
        catch (JsonException) { return null; }
    }

    /// <summary>Registros de uma resposta: vetor direto ou atributo data da página.</summary>
    private static List<JsonElement> Itens(JsonElement corpo)
    {
        if (corpo.ValueKind == JsonValueKind.Object && corpo.TryGetProperty("data", out var data)) corpo = data;
        return corpo.ValueKind == JsonValueKind.Array ? corpo.EnumerateArray().ToList() : new();
    }

    private static string? Texto(JsonElement e, string objeto, string campo) =>
        e.ValueKind == JsonValueKind.Object
        && e.TryGetProperty(objeto, out var o) && o.ValueKind == JsonValueKind.Object
        && o.TryGetProperty(campo, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static ChaveRegistro? Chave(JsonElement e, string campoAno, string campoSequencial) =>
        Texto(e, "orgaoEntidade", "cnpj") is { Length: > 0 } cnpj
        && e.TryGetProperty(campoAno, out var a) && a.TryGetInt32(out var ano)
        && e.TryGetProperty(campoSequencial, out var s) && s.TryGetInt32(out var sequencial)
            ? new ChaveRegistro(cnpj, ano, sequencial)
            : null;

    private sealed record ChaveRegistro(string Cnpj, int Ano, int Sequencial)
    {
        public string Id => $"{Cnpj}/{Ano}/{Sequencial}";
        public string CnpjUrl => Uri.EscapeDataString(Cnpj);
    }

    private sealed class ConfigSwagger
    {
        public string Chave { get; set; } = "";
        public string Url { get; set; } = "";
        public string Arquivo { get; set; } = "";
    }

    private sealed class ConfigDominio
    {
        public string Endpoint { get; set; } = "";
        public string Caminho { get; set; } = "";
    }

    private sealed class ConfigConsulta
    {
        public string Endpoint { get; set; } = "";
        public string Caminho { get; set; } = "";
        public Dictionary<string, string> Filtros { get; set; } = new();
        public int[]? Modalidades { get; set; }
        public int TamanhoPagina { get; set; } = 50;
        public int MaxPaginas { get; set; } = 1;
        public string? Detalhar { get; set; }
    }

    private sealed class ConfigDetalhes
    {
        public int ComprasPorModalidade { get; set; }
        public int ItensTamanhoPagina { get; set; } = 50;
        public int ItensComResultadoPorCompra { get; set; }
        public int Contratos { get; set; }
        public int Orgaos { get; set; }
    }
}
