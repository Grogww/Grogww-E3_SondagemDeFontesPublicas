using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Sonda.Perfilador;

/// <summary>
/// Verificações de conformidade e consistência sobre os registros de cada execução principal.
/// Grava qualidade.csv: por regra, registros avaliados (os que têm os campos necessários) e violações.
/// Nenhum valor de registro é gravado.
/// </summary>
public static class Qualidade
{
    private static readonly NumberFormatInfo Br = new() { NumberDecimalSeparator = ",", NumberGroupSeparator = "" };

    private sealed class Contagem(string fonte, string execucao, string endpoint, string regra, string base_)
    {
        public string Fonte = fonte, Execucao = execucao, Endpoint = endpoint, Regra = regra, Base = base_;
        public long Avaliados, Violacoes;

        /// <summary>null = registro não avaliado (falta campo); true/false = conforme/violação.</summary>
        public void Somar(bool? conforme)
        {
            if (conforme is null) return;
            Avaliados++;
            if (conforme == false) Violacoes++;
        }
    }

    public static void Gravar(string caminho, string pastaDados, List<PerfilExecucao> execucoes)
    {
        var linhas = new List<Contagem>();
        foreach (var e in execucoes.Where(x => x.Papel.Principal))
        {
            var pasta = Path.Combine(pastaDados, e.Fonte, e.Id);
            var bruto = Path.Combine(pasta, "bruto");
            Contagem Nova(string ep, string regra, string base_)
            {
                var c = new Contagem(e.Fonte, e.Id, ep, regra, base_);
                linhas.Add(c);
                return c;
            }
            switch (e.Fonte)
            {
                case "pncp":
                    foreach (var ep in new[] { "contratacoes-publicacao", "contratacoes-proposta", "contratacoes-atualizacao", "contratacao-chave" })
                    {
                        var c = Nova(ep, "modalidadeId entre 1 e 13", "Manual API Consultas §5.2");
                        foreach (var r in Registros(bruto, ep))
                            c.Somar(Int(r, "modalidadeId") is { } m ? m is >= 1 and <= 13 : null);
                    }
                    foreach (var ep in new[] { "contratos", "contratos-atualizacao", "contrato-chave", "compra-contratos" })
                    {
                        var c = Nova(ep, "dataVigenciaFim >= dataVigenciaInicio", "lógica");
                        foreach (var r in Registros(bruto, ep))
                            c.Somar(DataNaoAntes(r, "dataVigenciaInicio", "dataVigenciaFim"));
                    }
                    {
                        var c = Nova("compra-itens", "situacaoCompraItem = 2 (Homologado) <-> temResultado = true", "Manual API Consultas §5.6");
                        foreach (var r in Registros(bruto, "compra-itens"))
                            c.Somar(Int(r, "situacaoCompraItem") is { } s && Bool(r, "temResultado") is { } t ? (s == 2) == t : null);
                    }
                    break;

                case "portal-transparencia":
                    foreach (var ep in new[] { "ceis", "ceis-id", "ceis-cnpj", "cnep", "cnep-id", "cnep-cnpj" })
                    {
                        var datas = Nova(ep, "dataFimSancao >= dataInicioSancao (\"Sem informação\" não conta)", "lógica");
                        var doc = Nova(ep, "Pessoa Física -> CPF (11 dígitos); demais tipos -> CNPJ (14) em sancionado.codigoFormatado", "dicionário de dados");
                        foreach (var r in Registros(bruto, ep))
                        {
                            datas.Somar(DataNaoAntes(r, "dataInicioSancao", "dataFimSancao"));
                            var tipo = Texto(r, "pessoa.tipo");
                            var digitos = Texto(r, "sancionado.codigoFormatado") is { } cod ? cod.Count(char.IsAsciiDigit) : 0;
                            doc.Somar(tipo is null || tipo.StartsWith("Sem Informa", StringComparison.OrdinalIgnoreCase) ? null
                                : digitos == (tipo == "Pessoa Física" ? 11 : 14));
                        }
                    }
                    break;

                case "sicaf":
                    foreach (var ep in new[] { "contratos", "contratos-fim-vigencia", "contratos-fornecedor", "contratos-itens" })
                    {
                        // Em contratos-itens a data de exclusão do contrato tem outro nome.
                        var campoExclusao = ep == "contratos-itens" ? "dataHoraExclusaoContrato" : "dataHoraExclusao";
                        var vig = Nova(ep, "dataVigenciaFinal >= dataVigenciaInicial", "lógica");
                        var exc = Nova(ep, $"contratoExcluido = true <-> {campoExclusao} preenchida", "lógica");
                        foreach (var r in Registros(bruto, ep))
                        {
                            vig.Somar(DataNaoAntes(r, "dataVigenciaInicial", "dataVigenciaFinal"));
                            exc.Somar(Bool(r, "contratoExcluido") is { } x ? x == (Texto(r, campoExclusao) is not null) : null);
                        }
                    }
                    break;

                case "transparencia-sc":
                    {
                        // Só a API (por grupo de natureza da despesa); o CSV mensal fica de fora.
                        var c = Nova("api-despesa", "vlpago <= vlliquidado <= vlempenhado", "Lei 4.320/64, arts. 58-65");
                        foreach (var r in Registros(bruto, "api-despesa", "detalhe"))
                        {
                            var (p, l, m) = (Dec(r, "vlpago"), Dec(r, "vlliquidado"), Dec(r, "vlempenhado"));
                            // Tolerância de meio centavo: os valores vêm como double somado na origem.
                            c.Somar(p is null || l is null || m is null ? null : p <= l + 0.005 && l <= m + 0.005);
                        }
                    }
                    break;

                case "cnpj":
                    {
                        var arquivos = Directory.Exists(Path.Combine(pasta, "arquivos"))
                            ? Directory.EnumerateFiles(Path.Combine(pasta, "arquivos"), "Estabelecimentos*.zip", SearchOption.AllDirectories).Order().ToList()
                            : [];
                        if (arquivos.Count == 0) break;
                        var c = Nova("estabelecimentos", "situacao_cadastral em {01, 02, 03, 04, 08}", "cnpj-metadados.pdf");
                        foreach (var arquivo in arquivos)
                        {
                            using var zip = ZipFile.OpenRead(arquivo);
                            foreach (var entrada in zip.Entries.Where(x => x.Name.Length > 0))
                            {
                                using var leitor = new StreamReader(entrada.Open(), Encoding.Latin1);
                                foreach (var linha in PerfilCampos.LinhasCsv(leitor, ';'))
                                {
                                    if (linha.Count == 1 && linha[0].Length == 0) continue;
                                    var v = linha.Count > 5 ? linha[5].Trim() : "";
                                    c.Somar(v.Length == 0 ? null : int.TryParse(v, out var s) && s is 1 or 2 or 3 or 4 or 8);
                                }
                            }
                        }
                    }
                    break;
            }
        }

        var sb = new StringBuilder().AppendLine("fonte;execucao;endpoint;regra;base;avaliados;violacoes;violacoes_pct");
        foreach (var c in linhas.Where(x => x.Avaliados > 0 || File.Exists(Path.Combine(pastaDados, x.Fonte, x.Execucao, "bruto", x.Endpoint + ".jsonl"))))
        {
            var pct = c.Avaliados == 0 ? "" : (100.0 * c.Violacoes / c.Avaliados).ToString("0.0", Br);
            sb.AppendLine(string.Join(";", new[] { c.Fonte, c.Execucao, c.Endpoint, c.Regra, c.Base, c.Avaliados.ToString(), c.Violacoes.ToString(), pct }
                .Select(s => s.IndexOfAny([';', '"']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s)));
        }
        File.WriteAllText(caminho, sb.ToString(), new UTF8Encoding(true));
    }

    /// <summary>Registros das respostas 2xx em JSON: lista na raiz, em data/resultado, no atributo indicado ou o próprio objeto.</summary>
    private static IEnumerable<JsonElement> Registros(string pastaBruto, string endpoint, string? lista = null)
    {
        var arquivo = Path.Combine(pastaBruto, endpoint + ".jsonl");
        if (!File.Exists(arquivo)) yield break;
        foreach (var linha in File.ReadLines(arquivo))
        {
            if (string.IsNullOrWhiteSpace(linha)) continue;
            using var doc = JsonDocument.Parse(linha);
            var r = doc.RootElement;
            var status = r.GetProperty("status").GetInt32();
            if (status is < 200 or >= 300 || !r.GetProperty("corpoJson").GetBoolean()) continue;
            var corpo = r.GetProperty("corpo");
            IEnumerable<JsonElement> regs = corpo.ValueKind switch
            {
                JsonValueKind.Array => corpo.EnumerateArray(),
                JsonValueKind.Object when lista is not null => corpo.TryGetProperty(lista, out var l) && l.ValueKind == JsonValueKind.Array ? l.EnumerateArray() : [],
                JsonValueKind.Object when corpo.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array => d.EnumerateArray(),
                JsonValueKind.Object when corpo.TryGetProperty("resultado", out var d) && d.ValueKind == JsonValueKind.Array => d.EnumerateArray(),
                JsonValueKind.Object => [corpo],
                _ => [],
            };
            foreach (var reg in regs.ToList())
                if (reg.ValueKind == JsonValueKind.Object) yield return reg.Clone();
        }
    }

    private static JsonElement? Valor(JsonElement e, string caminho)
    {
        foreach (var parte in caminho.Split('.'))
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(parte, out e)) return null;
        return e.ValueKind == JsonValueKind.Null ? null : e;
    }

    private static string? Texto(JsonElement r, string campo) =>
        Valor(r, campo) is { ValueKind: JsonValueKind.String } v && v.GetString()!.Trim() is { Length: > 0 } s ? s : null;

    private static int? Int(JsonElement r, string campo) => Valor(r, campo) switch
    {
        { ValueKind: JsonValueKind.Number } v when v.TryGetInt32(out var i) => i,
        { ValueKind: JsonValueKind.String } v when int.TryParse(v.GetString(), out var i) => i,
        _ => null,
    };

    private static bool? Bool(JsonElement r, string campo) => Valor(r, campo) switch
    {
        { ValueKind: JsonValueKind.True } => true,
        { ValueKind: JsonValueKind.False } => false,
        _ => null,
    };

    private static double? Dec(JsonElement r, string campo) =>
        Valor(r, campo) is { ValueKind: JsonValueKind.Number } v ? v.GetDouble() : null;

    /// <summary>Data em ISO (com ou sem hora) ou dd/MM/yyyy; marcadores e vazios = sem data.</summary>
    private static DateOnly? Data(JsonElement r, string campo)
    {
        if (Texto(r, campo) is not { } s) return null;
        if (DateOnly.TryParseExact(s, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
        if (s.Length >= 10 && DateOnly.TryParseExact(s[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) return d;
        return null;
    }

    private static bool? DataNaoAntes(JsonElement r, string inicio, string fim) =>
        Data(r, inicio) is { } i && Data(r, fim) is { } f ? f >= i : null;
}
