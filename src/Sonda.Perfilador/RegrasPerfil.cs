using System.Text.Json;
using Sonda.Core;

namespace Sonda.Perfilador;

/// <summary>Conteúdo de config/perfil.json.</summary>
public sealed class RegrasPerfil
{
    public string Uf { get; set; } = "SC";
    public List<string> ParametrosUf { get; set; } = new();
    public List<string> Marcadores { get; set; } = new();
    public Dictionary<string, RegrasFonte> Fontes { get; set; } = new();
    public List<Rodada> Rodadas { get; set; } = new();
    /// <summary>"fonte/id" → papel da execução. Sem entrada: principal.</summary>
    public Dictionary<string, PapelExecucao> Execucoes { get; set; } = new();

    public static RegrasPerfil Carregar(string caminho) =>
        JsonSerializer.Deserialize<RegrasPerfil>(File.ReadAllText(caminho), ConfigGeral.Json)
        ?? throw new InvalidOperationException($"Regras vazias: {caminho}");

    public RegrasFonte DaFonte(string fonte) => Fontes.TryGetValue(fonte, out var r) ? r : new RegrasFonte();

    public PapelExecucao Papel(string fonte, string id) =>
        Execucoes.TryGetValue($"{fonte}/{id}", out var p) ? p : new PapelExecucao();

    /// <summary>Rodada cuja faixa de datas contém o início da execução.</summary>
    public int? RodadaDe(DateTimeOffset inicio)
    {
        var dia = DateOnly.FromDateTime(inicio.DateTime);
        return Rodadas.FirstOrDefault(r => dia >= r.Inicio && dia <= r.Fim)?.Numero;
    }
}

/// <summary>Rodada de coleta (execução 1, execução 2...), pela data de início das execuções.</summary>
public sealed class Rodada
{
    public int Numero { get; set; }
    public DateOnly Inicio { get; set; }
    public DateOnly Fim { get; set; }
}

public sealed class PapelExecucao
{
    /// <summary>principal · evidencia-429 (só na tabela de 429) · evidencia (só na lista de evidências); as duas ficam fora dos números da rodada</summary>
    public string Papel { get; set; } = "principal";
    public string? Nota { get; set; }
    public bool Principal => Papel == "principal";
}

public sealed class RegrasFonte
{
    public List<RegraVazio> Vazio { get; set; } = new();
    public List<RegraSemConteudo> SemConteudo { get; set; } = new();
    public RegraRecorte Padrao { get; set; } = new();
    public Dictionary<string, RegraRecorte> Endpoints { get; set; } = new();
    public List<RegraArquivo> Arquivos { get; set; } = new();

    public RegraRecorte Recorte(string endpoint) => Endpoints.TryGetValue(endpoint, out var r) ? r : Padrao;
}

/// <summary>Resposta não-2xx que a fonte usa para "não há dados".</summary>
public sealed class RegraVazio
{
    public string Endpoint { get; set; } = "";
    public int Status { get; set; }
    /// <summary>Trecho que deve aparecer no corpo bruto. Nulo: basta o status.</summary>
    public string? MensagemContem { get; set; }
}

/// <summary>
/// Resposta 2xx que não traz o conteúdo esperado (ex.: página "casca" montada por script).
/// Conta como "sem conteúdo útil": nem OK, nem erro.
/// </summary>
public sealed class RegraSemConteudo
{
    public List<string> Endpoints { get; set; } = new();
    /// <summary>Expressão regular que o corpo precisa conter para ter conteúdo útil.</summary>
    public string Exige { get; set; } = "";
    public string? Nota { get; set; }

    private System.Text.RegularExpressions.Regex? _re;
    public System.Text.RegularExpressions.Regex Regex => _re ??= new(Exige, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
}

public sealed class RegraRecorte
{
    /// <summary>campo · juncao · fonte · herdado · sem-uf · nao-se-aplica</summary>
    public string Recorte { get; set; } = "sem-uf";
    public string? CampoUf { get; set; }
    /// <summary>Recorte "juncao": coluna deste endpoint cujo valor é procurado em <see cref="Juncao"/>.</summary>
    public string? CampoChave { get; set; }
    /// <summary>
    /// Recorte "juncao": endpoint com recorte "campo" e coluna de chave. O registro é de SC quando a
    /// chave aparece em algum registro de SC desse endpoint, de outra UF quando só aparece fora de SC,
    /// e sem UF quando não aparece nele (não há como saber).
    /// </summary>
    public RegraJuncao? Juncao { get; set; }
    public string? Nota { get; set; }
    /// <summary>
    /// Onde estão os registros no corpo, quando não é a raiz nem data/resultado/...
    /// Atributos separados por ponto; "[]" desce em cada elemento (ex.: "grupolink[].links").
    /// </summary>
    public string? Registros { get; set; }
}

public sealed class RegraJuncao
{
    public string Endpoint { get; set; } = "";
    public string Campo { get; set; } = "";
}

/// <summary>Arquivos baixados com conteúdo tabular (ZIP com CSV, ou CSV).</summary>
public sealed class RegraArquivo
{
    public string Endpoint { get; set; } = "";
    /// <summary>Pasta relativa à pasta da execução.</summary>
    public string Pasta { get; set; } = "";
    /// <summary>Expressão regular sobre o nome do arquivo baixado. Nulo: todos os da pasta.</summary>
    public string? Arquivo { get; set; }
    public string Codificacao { get; set; } = "utf-8";
    public string Separador { get; set; } = ";";
    /// <summary>
    /// Nomes das colunas, para CSV sem cabeçalho (a primeira linha já é registro). Nulo: a primeira
    /// linha é o cabeçalho. Com colunas, todas as entradas do ZIP são lidas (não só as ".csv").
    /// </summary>
    public List<string>? Colunas { get; set; }
}
