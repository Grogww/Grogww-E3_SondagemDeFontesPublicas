using System.Text.Json;
using Sonda.Core;

namespace Sonda.Fontes.Sicaf;

/// <summary>
/// Sonda do SICAF (ficha 9): cadastro de fornecedores pela API de dados abertos do Compras.gov.br
/// e contratações vinculadas ao fornecedor (filtro niFornecedor).
/// Contrato: docs/sicaf/swagger-recorte.json (GET sem requisito de segurança).
/// Recorte SC:
/// fornecedores não têm filtro de UF nem de município; SC só é identificável no retorno,
/// por ufSigla (varredura nacional paginada);
/// UASG filtra na origem por siglaUf; contratos não têm filtro de UF e são amostrados
/// pelas unidades de SC obtidas na consulta de UASG.
/// Paginação: pagina e tamanhoPagina (10 a 500, Swagger); a consulta de UASG só tem pagina.
/// A série termina em resultado vazio, paginasRestantes = 0 ou maxPaginas.
/// Sanções, ocorrências impeditivas e níveis de habilitação não constam da API (ficha 9,
/// Limitações declaradas): só o booleano habilitadoLicitar.
/// Divergência documental: resposta da área técnica no Portal de Dados Abertos (PDF, p. 18)
/// cita dataVigenciaInicial/dataVigenciaFinal em 2_consultarARPItem; o Swagger traz
/// dataVigenciaInicialMin/dataVigenciaInicialMax; vale o Swagger.
/// </summary>
public sealed class SondaSicaf : ISonda
{
    public string Nome => "sicaf";

    public async Task ExecutarAsync(Execucao execucao, CancellationToken ct)
    {
        var cfg = execucao.Config;
        var uf = cfg.Parametro<string>("uf");
        var forn = cfg.Parametro<ConfigFornecedores>("fornecedores");
        var vinculadas = cfg.Parametro<List<ConfigConsulta>>("porFornecedor");
        var uasg = cfg.Parametro<ConfigUasg>("uasg");
        var contratos = cfg.Parametro<ConfigContratos>("contratos");

        var swagger = cfg.Parametro<ConfigSwagger>("swagger");
        if (swagger.Url.Length > 0)
        {
            var arq = await execucao.Http.BaixarAsync("swagger", "snapshot", swagger.Url, swagger.Arquivo, ct);
            if (!arq.Sucesso) execucao.Registrar($"swagger: falhou (status {arq.Status})");
        }

        // 1. Varredura do cadastro (ativo é obrigatório); SC separada no retorno por ufSigla.
        var registros = new List<JsonElement>();
        foreach (var s in forn.Series)
        {
            var (lidos, negado) = await PaginarAsync(execucao, forn.Endpoint, forn.Caminho, s.Filtros, forn.TamanhoPagina, s.MaxPaginas, ct);
            if (negado)
            {
                // Recurso documentado como aberto: bloqueio é resultado da sondagem.
                execucao.Registrar($"{forn.Endpoint}: acesso negado; sondagem interrompida");
                return;
            }
            registros.AddRange(lidos);
        }
        var deSc = registros.Where(e => Valor(e, "ufSigla") == uf).ToList();
        execucao.Registrar($"{forn.Endpoint}: {registros.Count} registros lidos, {deSc.Count} de {uf}");

        // 2. Filtros do recurso (codigoCnae, porteEmpresaId, naturezaJuridicaId). Os domínios não
        // são documentados: os valores vêm do retorno (mais frequentes em SC ou, sem SC, no total).
        var baseFiltro = deSc.Count > 0 ? deSc : registros;
        foreach (var (campo, quantos) in forn.FiltrosDerivados)
        {
            var valores = baseFiltro
                .Select(e => Valor(e, campo))
                .Where(v => v is { Length: > 0 })
                .GroupBy(v => v!)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.Ordinal)
                .Take(quantos)
                .Select(g => g.Key);
            foreach (var valor in valores)
                await PaginarAsync(execucao, $"{forn.Endpoint}-filtro", forn.Caminho,
                    new Dictionary<string, string> { ["ativo"] = "true", [campo] = valor },
                    forn.TamanhoPagina, forn.MaxPaginasFiltro, ct);
        }

        // 3. Consulta por CNPJ e contratações vinculadas, para fornecedores de SC.
        // Formato do valor (com ou sem máscara) não documentado; envia só os dígitos, como no
        // exemplo de niFornecedor do Portal de Dados Abertos. CPF não é consultado.
        var fornecedoresSc = deSc
            .Select(e => (Cnpj: Digitos(Valor(e, "cnpj")), Ativo: Valor(e, "ativo") ?? "true"))
            .Where(f => f.Cnpj.Length == 14)
            .DistinctBy(f => f.Cnpj)
            .Take(forn.ConsultarCnpjs)
            .ToList();
        foreach (var f in fornecedoresSc)
        {
            await PaginarAsync(execucao, $"{forn.Endpoint}-cnpj", forn.Caminho,
                new Dictionary<string, string> { ["ativo"] = f.Ativo, ["cnpj"] = f.Cnpj }, MinimoPagina, 1, ct);
            foreach (var v in vinculadas)
                await PaginarAsync(execucao, v.Endpoint, v.Caminho,
                    v.Filtros.Append(new("niFornecedor", f.Cnpj)), v.TamanhoPagina, v.MaxPaginas, ct);
        }

        // 4. Unidades (UASG) de SC, com filtro na origem: fornecem o codigoOrgao exigido em contratos.
        var filtrosUasg = uasg.Filtros.Append(new(uasg.ParametroUf, uf));
        var (lidasUasg, _) = await PaginarAsync(execucao, uasg.Endpoint, uasg.Caminho, filtrosUasg, null, uasg.MaxPaginas, ct);
        // codigoOrgao é inteiro na UASG e texto em contratos; vai como veio (zeros à esquerda não documentados).
        var unidades = lidasUasg
            .Select(e => (Orgao: Valor(e, "codigoOrgao") ?? "", Uasg: Valor(e, "codigoUasg") ?? ""))
            .Where(u => u.Orgao is not ("" or "0") && u.Uasg.Length > 0)
            .DistinctBy(u => u.Orgao)
            .Take(contratos.Unidades)
            .ToList();
        execucao.Registrar($"{uasg.Endpoint}: {lidasUasg.Count} unidades de {uf}, {unidades.Count} órgãos na amostra");

        // 5. Contratos das unidades de SC. A correspondência codigoUnidadeGestora = codigoUasg é
        // dedução (não documentada); desligável por filtrarUnidadeGestora.
        var pares = new List<(string Orgao, string Uasg, string Cnpj)>();
        foreach (var u in unidades)
            foreach (var c in contratos.Consultas)
            {
                var (lidos, _) = await PaginarAsync(execucao, c.Endpoint, c.Caminho, FiltrosContrato(c, contratos, u.Orgao, u.Uasg), c.TamanhoPagina, c.MaxPaginas, ct);
                pares.AddRange(lidos
                    .Select(e => (u.Orgao, u.Uasg, Cnpj: Digitos(Valor(e, "niFornecedor"))))
                    .Where(p => p.Cnpj.Length == 14));
            }

        // 6. Cruzamento contrato → cadastro: o filtro niFornecedor é testado com pares (órgão,
        // fornecedor) vistos no retorno, e o mesmo CNPJ é procurado no cadastro do SICAF.
        var cruzar = pares.DistinctBy(p => p.Cnpj).Take(contratos.CruzarFornecedores).ToList();
        execucao.Registrar($"contratos: {pares.Count} com CNPJ; {cruzar.Count} fornecedores no cruzamento");
        foreach (var p in cruzar)
        {
            if (contratos.Consultas.FirstOrDefault() is { } c)
                await PaginarAsync(execucao, $"{c.Endpoint}-fornecedor", c.Caminho,
                    FiltrosContrato(c, contratos, p.Orgao, p.Uasg).Append(new("niFornecedor", p.Cnpj)), c.TamanhoPagina, 1, ct);

            // Situação cadastral desconhecida: ativo=true e, sem resultado, ativo=false.
            foreach (var ativo in new[] { "true", "false" })
            {
                var (achados, _) = await PaginarAsync(execucao, $"{forn.Endpoint}-cnpj", forn.Caminho,
                    new Dictionary<string, string> { ["ativo"] = ativo, ["cnpj"] = p.Cnpj }, MinimoPagina, 1, ct);
                if (achados.Count > 0) break;
            }
        }
    }

    /// <summary>Menor tamanhoPagina aceito (Swagger, minimum = 10).</summary>
    private const int MinimoPagina = 10;

    private static IEnumerable<KeyValuePair<string, string>> FiltrosContrato(ConfigConsulta c, ConfigContratos cfg, string orgao, string uasg)
    {
        var filtros = c.Filtros.Prepend(new("codigoOrgao", orgao));
        return cfg.FiltrarUnidadeGestora ? filtros.Append(new("codigoUnidadeGestora", uasg)) : filtros;
    }

    /// <summary>
    /// Percorre as páginas até resultado vazio, paginasRestantes = 0 ou maxPaginas.
    /// tamanhoPagina nulo: operação sem esse parâmetro (UASG).
    /// </summary>
    private static async Task<(List<JsonElement> Registros, bool Negado)> PaginarAsync(
        Execucao execucao, string endpoint, string caminho, IEnumerable<KeyValuePair<string, string>> filtros,
        int? tamanhoPagina, int maxPaginas, CancellationToken ct)
    {
        var registros = new List<JsonElement>();
        var fixos = filtros.ToList();
        for (var pagina = 1; pagina <= maxPaginas; pagina++)
        {
            var parametros = fixos.Append(new("pagina", pagina.ToString())).ToList();
            if (tamanhoPagina is { } t) parametros.Add(new("tamanhoPagina", t.ToString()));
            var query = string.Join("&", parametros.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));
            var chave = string.Join(";", parametros.Select(p => $"{p.Key}={p.Value}"));

            var r = await execucao.Http.ObterAsync(endpoint, chave, $"{caminho}?{query}", ct);
            if (r.Status is 401 or 403) return (registros, true);
            // Erro ou corpo fora do envelope {resultado, paginasRestantes} encerra a série.
            if (!r.Sucesso || Ler(r.Corpo) is not { ValueKind: JsonValueKind.Object } corpo) break;
            if (!corpo.TryGetProperty("resultado", out var resultado) || resultado.ValueKind != JsonValueKind.Array) break;

            var itens = resultado.EnumerateArray().ToList();
            if (itens.Count == 0) break;
            registros.AddRange(itens);
            if (corpo.TryGetProperty("paginasRestantes", out var resto) && resto.TryGetInt64(out var n) && n <= 0) break;
        }
        return (registros, false);
    }

    private static JsonElement? Ler(string? corpo)
    {
        if (string.IsNullOrWhiteSpace(corpo)) return null;
        try { return JsonSerializer.Deserialize<JsonElement>(corpo); }
        catch (JsonException) { return null; }
    }

    /// <summary>Valor escalar de um atributo como texto (texto, número ou booleano).</summary>
    private static string? Valor(JsonElement e, string nome)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(nome, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static string Digitos(string? texto) =>
        texto is null ? "" : new string(texto.Where(char.IsAsciiDigit).ToArray());

    private sealed class ConfigSwagger
    {
        public string Url { get; set; } = "";
        public string Arquivo { get; set; } = "swagger.json";
    }

    private sealed class ConfigSerie
    {
        public Dictionary<string, string> Filtros { get; set; } = new();
        public int MaxPaginas { get; set; } = 1;
    }

    private sealed class ConfigFornecedores
    {
        public string Endpoint { get; set; } = "";
        public string Caminho { get; set; } = "";
        public int TamanhoPagina { get; set; } = MinimoPagina;
        public List<ConfigSerie> Series { get; set; } = new();
        /// <summary>Parâmetro de filtro → quantos valores do retorno testar.</summary>
        public Dictionary<string, int> FiltrosDerivados { get; set; } = new();
        public int MaxPaginasFiltro { get; set; } = 1;
        public int ConsultarCnpjs { get; set; }
    }

    private sealed class ConfigConsulta
    {
        public string Endpoint { get; set; } = "";
        public string Caminho { get; set; } = "";
        public Dictionary<string, string> Filtros { get; set; } = new();
        public int TamanhoPagina { get; set; } = MinimoPagina;
        public int MaxPaginas { get; set; } = 1;
    }

    private sealed class ConfigUasg
    {
        public string Endpoint { get; set; } = "";
        public string Caminho { get; set; } = "";
        public Dictionary<string, string> Filtros { get; set; } = new();
        public string ParametroUf { get; set; } = "siglaUf";
        public int MaxPaginas { get; set; } = 1;
    }

    private sealed class ConfigContratos
    {
        public int Unidades { get; set; }
        public bool FiltrarUnidadeGestora { get; set; }
        public List<ConfigConsulta> Consultas { get; set; } = new();
        public int CruzarFornecedores { get; set; }
    }
}
