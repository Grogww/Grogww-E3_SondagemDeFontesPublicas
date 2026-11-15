namespace Sonda.Core;

/// <summary>Contrato de cada sonda. A Cli localiza as implementações de Sonda.Fontes por reflexão.</summary>
public interface ISonda
{
    /// <summary>Argumento da Cli e chave da fonte em config/fontes.json (ex.: "pncp").</summary>
    string Nome { get; }

    /// <summary>
    /// Executa a amostra da fonte. Toda requisição passa por <see cref="Execucao.Http"/>.
    /// Reexecutar com o mesmo Id de execução pula o que já foi coletado.
    /// </summary>
    Task ExecutarAsync(Execucao execucao, CancellationToken ct);
}
