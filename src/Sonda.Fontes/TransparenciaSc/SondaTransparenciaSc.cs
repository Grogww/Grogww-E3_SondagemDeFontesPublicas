using System.Text.Json;
using System.Text.RegularExpressions;
using Sonda.Core;

namespace Sonda.Fontes.TransparenciaSc;

/// <summary>
/// Sonda do Portal da Transparência de Santa Catarina (ficha 8, "Condicionado").
/// Acesso misto: consultas parametrizadas em ambiente web e arquivos CSV. Não há Swagger
/// nem API declarada.
///
/// API interna (não documentada): as páginas de consulta do portal são rotas de SPA, que
/// abrem no navegador e respondem 404 quando acessadas direto (execução 20261122-222340).
/// O conteúdo vem de uma API JSON interna, em outro host
/// (api-portal-transparencia.apps.sm.okd4.ciasc.sc.gov.br), descoberta pela inspeção do
/// navegador em 2026-11-23. Ela não consta da ficha 8 nem de documento do portal, não tem
/// contrato publicado e não tem garantia de estabilidade: caminhos, parâmetros e formato
/// podem mudar sem aviso. As respostas observadas trazem x-ratelimit-limit: 300 e
/// x-ratelimit-remaining, sem a janela. O navegador envia Origin/Referer do portal e o CORS
/// só libera essa origem; a sonda não imita o navegador (só o User-Agent padrão, sem
/// Origin/Referer). Recusa da API (403 ou outro) é resultado da sondagem e não é contornada.
///
/// A sonda faz três coisas:
/// 1. Consulta a API interna: data e situação da última carga da despesa (latência de
///    alimentação), despesa por área no exercício (lista "detalhe") e série anual da área
///    ("valoresAno"), para os indicadores do PDF da página inicial (p. 1), e a lista dos
///    arquivos de dados abertos da despesa (subárea 35: grupolink[].links[], um grupo por ano).
/// 2. Verifica o acesso às páginas com endereço documentado e guarda o HTML bruto: página
///    inicial e portal de dados abertos do Estado (texto_dados-abertos-despesa.txt).
/// 3. Baixa o dicionário de dados e uma amostra fixa dos arquivos mensais de despesa (ZIP
///    com um CSV). Os nomes vêm da lista da subárea (arquivo1 + caminho), não de um modelo:
///    a convenção é inconsistente (anos sem zero à esquerda no mês, 2017 misto, "caminho"
///    às vezes com "/" no início). Mês da amostra ausente da lista é registrado e não baixado.
///
/// As consultas Receita Detalhada e Despesa Detalhada (PDF, p. 2) não têm endereço nem
/// parâmetros documentados e não são acionadas.
/// Layout do CSV (dicionário de dados; texto_amostra-csv_despesa_2026_10.txt): latin-1,
/// separador ';', 60 colunas, textos com espaços à direita. Divergência dicionário × arquivo:
/// o dicionário lista a coluna RN, ausente do CSV, e não lista "Indicador Despesa
/// Emergencial" nem "Descrição Indicador Despesa Emergencial", presentes no CSV; vale o arquivo.
/// Credor: "Código Credor" (código interno) e "Credor" (nome); sem CNPJ/CPF e sem localização.
/// Recorte SC: toda a fonte é do Executivo estadual de SC; não há campo de UF nem filtro
/// na origem.
/// Paginação: a despesa por área informa hasMorePages/last_page; a amostra lê só a primeira
/// página e registra quando há mais. Arquivos: um por mês.
/// </summary>
public sealed class SondaTransparenciaSc : ISonda
{
    public string Nome => "transparencia-sc";

    private static readonly Regex Ano = new(@"^\d{4}$");
    private static readonly Regex NomeMensal = new(@"^despesa_(\d{4})_(\d{1,2})\.zip$", RegexOptions.IgnoreCase);

    public async Task ExecutarAsync(Execucao execucao, CancellationToken ct)
    {
        var cfg = execucao.Config;
        var api = cfg.Parametro<ConfigApi>("api");

        // 1. API interna. Falha de acesso (403, CORS, 404) é resultado da sondagem.
        foreach (var c in api.Consultas)
        {
            if (!c.PorIndicador)
            {
                await ConsultarAsync(execucao, c.Endpoint, c.Chave, Url(api.BaseUrl, c.Url), ct);
                continue;
            }
            foreach (var ind in api.Indicadores)
            {
                var url = c.Url.Replace("{ano}", api.Ano.ToString()).Replace("{indicador}", ind.Id.ToString());
                var chave = $"ano={api.Ano};indicador={ind.Id};area={ind.Area}";
                await ConsultarAsync(execucao, c.Endpoint, chave, Url(api.BaseUrl, url), ct);
            }
        }

        var lista = api.Lista;
        var rLista = await ConsultarAsync(execucao, lista.Endpoint, lista.Chave, Url(api.BaseUrl, lista.Url), ct);
        var links = rLista.Sucesso ? LerLinks(execucao, lista.Endpoint, rLista.Corpo) : null;
        if (links is not null) RegistrarConvencao(execucao, links);

        // 2. Páginas com endereço documentado.
        foreach (var p in cfg.Parametro<List<ConfigPagina>>("paginas"))
        {
            var r = await execucao.Http.ObterAsync(p.Endpoint, p.Chave, p.Url, ct);
            execucao.Registrar($"{p.Endpoint} [{p.Chave}]: status {r.Status}, {r.Corpo.Length} caracteres");
        }

        // 3. Dicionário: endereço da lista, se ela trouxer o arquivo; senão, o da configuração.
        var arquivos = cfg.Parametro<ConfigArquivos>("arquivos");
        var dic = cfg.Parametro<ConfigDicionario>("dicionario");
        var linkDic = links?.FirstOrDefault(l => !Ano.IsMatch(l.Grupo) && l.Interno
            && l.Arquivo.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
            && l.Arquivo.Contains("Despesa", StringComparison.OrdinalIgnoreCase));
        var urlDic = linkDic is not null ? UrlArquivo(arquivos.BaseUrl, linkDic) : dic.Url;
        execucao.Registrar($"{dic.Endpoint}: endereço {(linkDic is not null ? "da lista da subárea" : "da configuração")}");
        if (urlDic.Length > 0)
            Registrar(execucao, dic.Endpoint, dic.Chave,
                await execucao.Http.BaixarAsync(dic.Endpoint, dic.Chave, urlDic, dic.Arquivo, ct));

        // Arquivos mensais: o ZIP fica como veio; a leitura do CSV é do perfilador.
        if (links is null)
        {
            execucao.Registrar($"{arquivos.Endpoint}: lista de arquivos indisponível; amostra mensal não baixada");
            return;
        }
        foreach (var mes in arquivos.Meses)
        {
            var partes = mes.Split('-');
            if (partes.Length != 2 || !int.TryParse(partes[0], out var ano) || !int.TryParse(partes[1], out var m))
                throw new InvalidOperationException($"Mês inválido em arquivos.meses: \"{mes}\" (esperado AAAA-MM).");
            var chave = $"ano={partes[0]};mes={partes[1]}";
            var achados = links.Where(l => l.Ano == ano && l.Mes == m).ToList();
            if (achados.Count == 0)
            {
                execucao.Registrar($"{arquivos.Endpoint} [{chave}]: mês não consta da lista da subárea; não baixado");
                continue;
            }
            if (achados.Count > 1)
                execucao.Registrar($"{arquivos.Endpoint} [{chave}]: {achados.Count} links para o mês na lista; usado o primeiro");
            var link = achados[0];
            var destino = $"arquivos/despesa/{ano}/{link.Arquivo}";
            Registrar(execucao, arquivos.Endpoint, chave,
                await execucao.Http.BaixarAsync(arquivos.Endpoint, chave, UrlArquivo(arquivos.BaseUrl, link), destino, ct));
        }
    }

    private static async Task<RespostaSonda> ConsultarAsync(Execucao execucao, string endpoint, string chave, string url, CancellationToken ct)
    {
        var r = await execucao.Http.ObterAsync(endpoint, chave, url, ct);
        var detalhe = r.Erro ?? (r.Sucesso ? ResumoJson(r.Corpo) : $"{r.Corpo.Length} caracteres");
        execucao.Registrar($"{endpoint} [{chave}]: status {r.Status}, {detalhe}");
        return r;
    }

    // Resumo do corpo: atributos da raiz e, na despesa por área, a paginação informada.
    private static string ResumoJson(string corpo)
    {
        try
        {
            using var doc = JsonDocument.Parse(corpo);
            var raiz = doc.RootElement;
            if (raiz.ValueKind != JsonValueKind.Object) return $"JSON ({raiz.ValueKind})";
            var nomes = string.Join(", ", raiz.EnumerateObject().Select(p => p.Name).Take(8));
            var pag = raiz.TryGetProperty("hasMorePages", out var mais) && mais.ValueKind == JsonValueKind.True
                ? $"; há mais páginas (last_page {(raiz.TryGetProperty("last_page", out var lp) ? lp.ToString() : "?")}), lida só a primeira"
                : "";
            var n = raiz.TryGetProperty("detalhe", out var det) && det.ValueKind == JsonValueKind.Array ? $"; detalhe com {det.GetArrayLength()} itens" : "";
            return $"JSON {{{nomes}}}{n}{pag}";
        }
        catch (JsonException) { return "corpo não é JSON"; }
    }

    private static List<Link>? LerLinks(Execucao execucao, string endpoint, string corpo)
    {
        try
        {
            using var doc = JsonDocument.Parse(corpo);
            if (!doc.RootElement.TryGetProperty("grupolink", out var grupos) || grupos.ValueKind != JsonValueKind.Array)
            {
                execucao.Registrar($"{endpoint}: resposta sem \"grupolink\"");
                return null;
            }
            var links = new List<Link>();
            foreach (var g in grupos.EnumerateArray())
            {
                var grupo = Texto(g, "descricao");
                if (!g.TryGetProperty("links", out var ls) || ls.ValueKind != JsonValueKind.Array) continue;
                foreach (var l in ls.EnumerateArray())
                {
                    var arquivo = Texto(l, "arquivo1");
                    var caminho = Texto(l, "caminho");
                    int? ano = null, mes = null;
                    var nm = NomeMensal.Match(arquivo);
                    // Só os grupos de ano entram na amostra mensal (há grupos de órgãos, como SES e IPREV).
                    if (nm.Success && Ano.IsMatch(grupo))
                    {
                        ano = int.Parse(nm.Groups[1].Value);
                        mes = int.Parse(nm.Groups[2].Value);
                    }
                    links.Add(new Link(grupo, caminho, arquivo, Texto(l, "tipocaminho") == "1", ano, mes));
                }
            }
            execucao.Registrar($"{endpoint}: {grupos.GetArrayLength()} grupos, {links.Count} links");
            return links;
        }
        catch (JsonException)
        {
            execucao.Registrar($"{endpoint}: corpo não é JSON");
            return null;
        }
    }

    // Irregularidades da convenção de nomes na lista (sem requisição; vai para o relatório).
    private static void RegistrarConvencao(Execucao execucao, List<Link> links)
    {
        var mensais = links.Where(l => l.Ano is not null).ToList();
        if (mensais.Count == 0) { execucao.Registrar("convenção de nomes: nenhum arquivo mensal reconhecido na lista"); return; }
        var semZero = mensais.Where(l => l.Mes < 10 && !l.Arquivo.Contains($"_{l.Mes:00}.", StringComparison.Ordinal))
            .GroupBy(l => l.Ano!.Value).OrderBy(g => g.Key).Select(g => $"{g.Key}({g.Count()})").ToList();
        var barra = mensais.Where(l => l.Caminho.StartsWith('/')).GroupBy(l => l.Ano!.Value)
            .OrderBy(g => g.Key).Select(g => $"{g.Key}({g.Count()})").ToList();
        var anoDivergente = mensais.Count(l => l.Grupo != l.Ano.ToString());
        var faixa = $"{mensais.Min(l => l.Ano * 100 + l.Mes)}–{mensais.Max(l => l.Ano * 100 + l.Mes)}";
        execucao.Registrar($"convenção de nomes: {mensais.Count} arquivos mensais ({faixa}); "
            + $"mês sem zero à esquerda: {(semZero.Count == 0 ? "nenhum" : string.Join(" ", semZero))}; "
            + $"caminho com \"/\" inicial: {(barra.Count == 0 ? "nenhum" : string.Join(" ", barra))}; "
            + $"ano do nome ≠ grupo: {anoDivergente}");
    }

    private static string Texto(JsonElement e, string nome) =>
        e.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";

    private static string Url(string baseUrl, string url) =>
        url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : baseUrl.TrimEnd('/') + "/" + url.TrimStart('/');

    // Download: <baseUrl>/<caminho sem "/" nas pontas>/<arquivo1>, com cada parte codificada.
    private static string UrlArquivo(string baseUrl, Link l)
    {
        var caminho = string.Join("/", l.Caminho.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
        return $"{baseUrl.TrimEnd('/')}/{caminho}/{Uri.EscapeDataString(l.Arquivo)}";
    }

    private static void Registrar(Execucao execucao, string endpoint, string chave, ArquivoSonda r) =>
        execucao.Registrar(r.Sucesso
            ? $"{endpoint} [{chave}]: {r.Bytes} bytes"
            : $"{endpoint} [{chave}]: falha, status {r.Status} {r.Erro}");

    /// <summary>Link da lista da subárea. Interno: tipocaminho 1 (arquivo no repositório do portal).</summary>
    private sealed record Link(string Grupo, string Caminho, string Arquivo, bool Interno, int? Ano, int? Mes);

    private sealed class ConfigApi
    {
        public string BaseUrl { get; set; } = "";
        public int Ano { get; set; }
        public List<ConfigIndicador> Indicadores { get; set; } = new();
        public List<ConfigConsulta> Consultas { get; set; } = new();
        /// <summary>Lista dos arquivos de dados abertos (subárea).</summary>
        public ConfigConsulta Lista { get; set; } = new();
    }

    private sealed class ConfigIndicador
    {
        public int Id { get; set; }
        public string Area { get; set; } = "";
    }

    private sealed class ConfigConsulta
    {
        public string Endpoint { get; set; } = "";
        public string Chave { get; set; } = "";
        /// <summary>Relativa a api.baseUrl. Com porIndicador, marcadores {ano} e {indicador}.</summary>
        public string Url { get; set; } = "";
        public bool PorIndicador { get; set; }
    }

    private sealed class ConfigPagina
    {
        public string Endpoint { get; set; } = "";
        public string Chave { get; set; } = "pagina";
        /// <summary>Absoluta ou relativa a baseUrl; vazia = raiz.</summary>
        public string Url { get; set; } = "";
    }

    private sealed class ConfigDicionario
    {
        public string Endpoint { get; set; } = "";
        public string Chave { get; set; } = "arquivo";
        /// <summary>Usada só se a lista da subárea não trouxer o dicionário. Vazia = não baixar.</summary>
        public string Url { get; set; } = "";
        public string Arquivo { get; set; } = "";
    }

    private sealed class ConfigArquivos
    {
        public string Endpoint { get; set; } = "";
        /// <summary>Raiz do repositório de arquivos; o resto vem de caminho + arquivo1.</summary>
        public string BaseUrl { get; set; } = "";
        /// <summary>Amostra fixa, em AAAA-MM.</summary>
        public List<string> Meses { get; set; } = new();
    }
}
