using Sonda.Core;

namespace Sonda.Fontes.TransparenciaSc;

/// <summary>
/// Sonda do Portal da Transparência de Santa Catarina (ficha 8, "Condicionado").
/// Acesso misto: consultas parametrizadas em ambiente web e arquivos CSV. Não há Swagger
/// nem API declarada. A sonda faz duas coisas:
/// 1. Verifica o acesso às páginas com endereço documentado e guarda o HTML bruto: página
///    inicial, indicadores de despesa anual por área (PDF da página inicial, p. 1), página
///    "Dados Abertos de Despesa" e portal de dados abertos do Estado
///    (texto_dados-abertos-despesa.txt). O portal monta o conteúdo por script e responde
///    404 a páginas existentes (mesmo texto): o status é registrado como resultado.
/// 2. Baixa o dicionário de dados e uma amostra fixa dos arquivos mensais de despesa (ZIP
///    com um CSV), no padrão de endereço listado na página de dados abertos.
/// As consultas Receita Detalhada e Despesa Detalhada (PDF, p. 2) não têm endereço nem
/// parâmetros documentados e não são acionadas.
/// Layout do CSV (dicionário de dados; texto_amostra-csv_despesa_2026_10.txt): latin-1,
/// separador ';', 60 colunas, textos com espaços à direita. Divergência dicionário × arquivo:
/// o dicionário lista a coluna RN, ausente do CSV, e não lista "Indicador Despesa
/// Emergencial" nem "Descrição Indicador Despesa Emergencial", presentes no CSV; vale o arquivo.
/// Credor: "Código Credor" (código interno) e "Credor" (nome); sem CNPJ/CPF e sem localização.
/// Recorte SC: toda a fonte é do Executivo estadual de SC; não há campo de UF nem filtro
/// na origem.
/// Paginação: não se aplica (um arquivo por mês; uma requisição por página).
/// </summary>
public sealed class SondaTransparenciaSc : ISonda
{
    public string Nome => "transparencia-sc";

    public async Task ExecutarAsync(Execucao execucao, CancellationToken ct)
    {
        var cfg = execucao.Config;

        // Falha de acesso (bloqueio, captcha, página montada por script) é resultado da sondagem.
        foreach (var p in cfg.Parametro<List<ConfigPagina>>("paginas"))
        {
            var r = await execucao.Http.ObterAsync(p.Endpoint, p.Chave, p.Url, ct);
            execucao.Registrar($"{p.Endpoint} [{p.Chave}]: status {r.Status}, {r.Corpo.Length} caracteres");
        }

        var dic = cfg.Parametro<ConfigArquivo>("dicionario");
        if (dic.Url.Length > 0)
            Registrar(execucao, dic.Endpoint, dic.Chave,
                await execucao.Http.BaixarAsync(dic.Endpoint, dic.Chave, dic.Url, dic.Arquivo, ct));

        // Arquivos mensais de despesa: o ZIP fica como veio; a leitura do CSV é do perfilador.
        var despesa = cfg.Parametro<ConfigDespesa>("despesa");
        foreach (var mes in despesa.Meses)
        {
            var partes = mes.Split('-');
            if (partes.Length != 2) throw new InvalidOperationException($"Mês inválido em despesa.meses: \"{mes}\" (esperado AAAA-MM).");
            var url = despesa.ModeloUrl.Replace("{ano}", partes[0]).Replace("{mes}", partes[1]);
            var chave = $"ano={partes[0]};mes={partes[1]}";
            var destino = $"arquivos/despesa/{partes[0]}/despesa_{partes[0]}_{partes[1]}.zip";
            Registrar(execucao, despesa.Endpoint, chave,
                await execucao.Http.BaixarAsync(despesa.Endpoint, chave, url, destino, ct));
        }
    }

    private static void Registrar(Execucao execucao, string endpoint, string chave, ArquivoSonda r) =>
        execucao.Registrar(r.Sucesso
            ? $"{endpoint} [{chave}]: {r.Bytes} bytes"
            : $"{endpoint} [{chave}]: falha, status {r.Status} {r.Erro}");

    private sealed class ConfigPagina
    {
        public string Endpoint { get; set; } = "";
        public string Chave { get; set; } = "pagina";
        /// <summary>Absoluta ou relativa a baseUrl; vazia = raiz.</summary>
        public string Url { get; set; } = "";
    }

    private sealed class ConfigArquivo
    {
        public string Endpoint { get; set; } = "";
        public string Chave { get; set; } = "arquivo";
        /// <summary>Vazia = não baixar.</summary>
        public string Url { get; set; } = "";
        public string Arquivo { get; set; } = "";
    }

    private sealed class ConfigDespesa
    {
        public string Endpoint { get; set; } = "";
        /// <summary>Marcadores: {ano} (AAAA) e {mes} (MM).</summary>
        public string ModeloUrl { get; set; } = "";
        /// <summary>Amostra fixa, em AAAA-MM.</summary>
        public List<string> Meses { get; set; } = new();
    }
}
