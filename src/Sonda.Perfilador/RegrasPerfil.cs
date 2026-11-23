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

    public static RegrasPerfil Carregar(string caminho) =>
        JsonSerializer.Deserialize<RegrasPerfil>(File.ReadAllText(caminho), ConfigGeral.Json)
        ?? throw new InvalidOperationException($"Regras vazias: {caminho}");

    public RegrasFonte DaFonte(string fonte) => Fontes.TryGetValue(fonte, out var r) ? r : new RegrasFonte();
}

public sealed class RegrasFonte
{
    public List<RegraVazio> Vazio { get; set; } = new();
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

public sealed class RegraRecorte
{
    /// <summary>campo · fonte · herdado · sem-uf · nao-se-aplica</summary>
    public string Recorte { get; set; } = "sem-uf";
    public string? CampoUf { get; set; }
    public string? Nota { get; set; }
}

/// <summary>Arquivos baixados com conteúdo tabular (ZIP com CSV, ou CSV).</summary>
public sealed class RegraArquivo
{
    public string Endpoint { get; set; } = "";
    /// <summary>Pasta relativa à pasta da execução.</summary>
    public string Pasta { get; set; } = "";
    public string Codificacao { get; set; } = "utf-8";
    public string Separador { get; set; } = ";";
}
