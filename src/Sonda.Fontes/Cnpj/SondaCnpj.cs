using Sonda.Core;

namespace Sonda.Fontes.Cnpj;

/// <summary>
/// Sonda dos Dados Abertos do CNPJ (ficha 6). Fonte sem API nem Swagger: download em lote
/// de arquivos ZIP no repositório de arquivos da Receita Federal (compartilhamentos públicos).
/// Etapas: (1) páginas de listagem citadas na documentação, guardadas em HTML bruto;
/// (2) amostra fixa de arquivos, definida na configuração.
/// O endereço de download de cada arquivo não consta dos documentos salvos (ficha 6,
/// Observações); só as páginas de listagem estão documentadas. Por isso a etapa 2 depende
/// de "modeloDownload" na configuração e é pulada enquanto ele estiver vazio.
/// Recorte SC: sem filtro na origem (arquivos nacionais, divididos por numeração e não por UF).
/// SC é identificada no retorno pelo campo UF de Estabelecimentos (cnpj-metadados.pdf, p. 2);
/// Empresas, Simples e Sócios não têm UF e dependem da junção pelo CNPJ básico.
/// Layout: separador ";" (cnpj-metadados.pdf, p. 5, item 1); codificação não informada.
/// </summary>
public sealed class SondaCnpj : ISonda
{
    public string Nome => "cnpj";

    public async Task ExecutarAsync(Execucao execucao, CancellationToken ct)
    {
        var cfg = execucao.Config;

        // Listagens: uma requisição por página, sem paginação. Falha de acesso é resultado da sondagem.
        foreach (var l in cfg.Parametro<List<ConfigListagem>>("listagens"))
        {
            var r = await execucao.Http.ObterAsync(l.Endpoint, l.Chave, l.Url, ct);
            execucao.Registrar($"{l.Endpoint} [{l.Chave}]: status {r.Status}, {r.Corpo.Length} caracteres");
        }

        var modelo = cfg.Parametro<string>("modeloDownload");
        var arquivos = cfg.Parametro<List<ConfigArquivo>>("arquivos");
        if (string.IsNullOrWhiteSpace(modelo))
        {
            execucao.Registrar($"modeloDownload vazio: {arquivos.Count} arquivo(s) da amostra não baixado(s) (endereço de download não documentado).");
            return;
        }

        foreach (var a in arquivos)
        {
            var url = modelo
                .Replace("{compartilhamento}", a.Compartilhamento)
                .Replace("{dir}", Uri.EscapeDataString(a.Dir))
                .Replace("{nome}", Uri.EscapeDataString(a.Nome));
            var pasta = a.Dir.Trim('/');
            var chave = $"dir={(pasta.Length == 0 ? "/" : pasta)};arquivo={a.Nome}";
            var destino = pasta.Length == 0 ? $"arquivos/{a.Nome}" : $"arquivos/{pasta}/{a.Nome}";

            var r = await execucao.Http.BaixarAsync(a.Endpoint, chave, url, destino, ct);
            execucao.Registrar(r.Sucesso
                ? $"{a.Endpoint} [{chave}]: {r.Bytes} bytes"
                : $"{a.Endpoint} [{chave}]: falha, status {r.Status} {r.Erro}");
        }
    }

    private sealed class ConfigListagem
    {
        public string Endpoint { get; set; } = "";
        public string Chave { get; set; } = "pagina";
        /// <summary>Absoluta ou relativa a baseUrl.</summary>
        public string Url { get; set; } = "";
    }

    private sealed class ConfigArquivo
    {
        public string Endpoint { get; set; } = "";
        /// <summary>Caminho do compartilhamento público, relativo a baseUrl.</summary>
        public string Compartilhamento { get; set; } = "";
        /// <summary>Pasta dentro do compartilhamento ("/" = raiz).</summary>
        public string Dir { get; set; } = "/";
        public string Nome { get; set; } = "";
    }
}
