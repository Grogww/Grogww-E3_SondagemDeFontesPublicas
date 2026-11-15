using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sonda.Core;

/// <summary>Conteúdo de config/fontes.json.</summary>
public sealed class ConfigGeral
{
    public string UserAgent { get; set; } = "";
    public string PastaDados { get; set; } = "dados";
    public Dictionary<string, ConfigFonte> Fontes { get; set; } = new();

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Mesmas opções, sem indentação (uma linha por registro nos JSONL).</summary>
    public static readonly JsonSerializerOptions JsonLinha = new(Json) { WriteIndented = false };

    public static ConfigGeral Carregar(string caminho) =>
        JsonSerializer.Deserialize<ConfigGeral>(File.ReadAllText(caminho), Json)
        ?? throw new InvalidOperationException($"Configuração vazia: {caminho}");
}

/// <summary>Configuração de uma fonte (uma entrada em "fontes"). Um limitador por entrada.</summary>
public sealed class ConfigFonte
{
    public string BaseUrl { get; set; } = "";

    /// <summary>Limite declarado na documentação (req/min). Nulo: sem limite declarado, toma 60 req/min como base.</summary>
    public double? LimitePorMinuto { get; set; }

    /// <summary>Onde o limite está declarado (ficha da E2, documento e seção).</summary>
    public string? OrigemLimite { get; set; }

    /// <summary>Fração do limite efetivamente usada (throttle).</summary>
    public double FracaoLimite { get; set; } = 0.6;

    public int TimeoutSegundos { get; set; } = 60;

    /// <summary>Tentativas por requisição; só 5xx e timeout são repetidos.</summary>
    public int MaxTentativas { get; set; } = 3;

    /// <summary>Nome do header que leva a chave de acesso (ex.: chave-api-dados).</summary>
    public string? HeaderChave { get; set; }

    /// <summary>Variável de ambiente com a chave. A chave nunca fica no arquivo de configuração.</summary>
    public string? VariavelChave { get; set; }

    /// <summary>Parâmetros próprios da fonte (endpoints, amostra, períodos...).</summary>
    public Dictionary<string, JsonElement> Parametros { get; set; } = new();

    public T Parametro<T>(string nome) =>
        Parametros.TryGetValue(nome, out var valor)
            ? valor.Deserialize<T>(ConfigGeral.Json)!
            : throw new KeyNotFoundException($"Parâmetro ausente na configuração da fonte: {nome}");

    public TimeSpan IntervaloEntreRequisicoes =>
        TimeSpan.FromMilliseconds(60_000 / ((LimitePorMinuto ?? 60) * FracaoLimite));
}
