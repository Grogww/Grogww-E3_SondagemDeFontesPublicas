using Sonda.Core;

namespace Sonda.Fontes.Cndt;

/// <summary>
/// Sonda da CNDT (ficha 7, "Condicionado"). A fonte é consulta web sem API:
/// não há Swagger, endpoint de dados nem consulta em lote documentados.
/// A sonda só verifica o acesso às páginas públicas citadas na ficha
/// (emissão/validação e apresentação) e guarda o HTML bruto.
/// A emissão da certidão não é automatizada: o endereço do formulário não consta
/// da documentação, e o acesso por sistemas de terceiros depende de autorização
/// da CGJT (Ato CGJT 1/2022, art. 21).
/// Recorte SC: não há filtro na origem (consulta individual por CPF/CNPJ, art. 9º).
/// Só a certidão positiva indica o TRT de cada processo (Anexo II); a
/// correspondência entre TRT e UF não é informada na documentação.
/// </summary>
public sealed class SondaCndt : ISonda
{
    public string Nome => "cndt";

    public async Task ExecutarAsync(Execucao execucao, CancellationToken ct)
    {
        // Uma requisição por página, sem paginação. Falha de acesso é resultado da sondagem.
        foreach (var p in execucao.Config.Parametro<List<ConfigPagina>>("paginas"))
        {
            var r = await execucao.Http.ObterAsync(p.Endpoint, p.Chave, p.Url, ct);
            execucao.Registrar($"{p.Endpoint}: status {r.Status}, {r.Corpo.Length} caracteres");
        }
    }

    private sealed class ConfigPagina
    {
        public string Endpoint { get; set; } = "";
        public string Chave { get; set; } = "pagina";
        /// <summary>Absoluta ou relativa a baseUrl; vazia = raiz.</summary>
        public string Url { get; set; } = "";
    }
}
