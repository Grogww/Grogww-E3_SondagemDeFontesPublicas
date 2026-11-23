using System.Globalization;
using System.Text;

namespace Sonda.Perfilador;

/// <summary>
/// Grava os agregados em CSV (UTF-8 com BOM, separador ';', vírgula decimal: abre direto no
/// Excel em pt-BR) e um resumo.md. Nenhum valor de registro é gravado, só nomes de campos e contagens.
/// </summary>
public static class Saida
{
    // Com InvariantGlobalization não há cultura pt-BR: só a vírgula decimal, sem separador de milhar.
    private static readonly NumberFormatInfo Br = new() { NumberDecimalSeparator = ",", NumberGroupSeparator = "" };
    private static readonly UTF8Encoding Utf8Bom = new(true);

    public static void Gravar(string pasta, List<PerfilExecucao> execucoes, RegrasPerfil regras)
    {
        GravarExecucoes(Path.Combine(pasta, "execucoes.csv"), execucoes);
        GravarEndpoints(Path.Combine(pasta, "endpoints.csv"), execucoes);
        Gravar429(Path.Combine(pasta, "limite-429.csv"), execucoes);
        GravarRecorte(Path.Combine(pasta, "recorte-sc.csv"), execucoes);
        GravarCampos(Path.Combine(pasta, "campos.csv"), execucoes);
        GravarResumo(Path.Combine(pasta, "resumo.md"), execucoes, regras);
    }

    // ---------- CSV ----------

    private static void GravarExecucoes(string caminho, List<PerfilExecucao> execucoes)
    {
        var csv = new Csv("fonte", "execucao", "situacao", "segmentos", "commit", "commit_sujo", "inicio", "fim",
            "duracao_min", "tentativas", "requisicoes", "limite_declarado_min", "ritmo_configurado_min",
            "ritmo_observado_min", "headers_limite", "respostas_429");
        foreach (var e in execucoes)
        {
            var ultimo = e.Segmentos[^1];
            csv.Linha(e.Fonte, e.Id, ultimo.Situacao, e.Segmentos.Count,
                string.Join(" ", e.Segmentos.Select(s => s.Commit[..Math.Min(7, s.Commit.Length)]).Distinct()),
                e.Segmentos.Any(s => s.CommitSujo) ? "sim" : "não",
                e.Segmentos[0].Inicio.ToString("yyyy-MM-dd HH:mm:ss"), ultimo.Fim?.ToString("yyyy-MM-dd HH:mm:ss"),
                Num(Duracao(e)), e.Tentativas.Count, e.Endpoints.Values.Sum(m => m.Requisicoes),
                Num(e.LimitePorMinuto), Num(e.RitmoConfigurado), Num(e.RitmoObservado),
                string.Join(" ", e.HeadersLimite), e.Eventos429.Count);
        }
        csv.Salvar(caminho);
    }

    private static void GravarEndpoints(string caminho, List<PerfilExecucao> execucoes)
    {
        var csv = new Csv("fonte", "execucao", "endpoint", "requisicoes", "tentativas", "retentativas", "status",
            "ok", "vazio", "erro_4xx", "erro_5xx", "falha_rede", "limite_429", "taxa_erro_pct", "taxa_vazio_pct",
            "latencia_p50_ms", "latencia_p95_ms", "latencia_max_ms", "ttfb_p50_ms", "ttfb_p95_ms", "bytes", "erros");
        foreach (var e in execucoes)
        foreach (var m in e.Endpoints.Values.OrderBy(x => x.Endpoint))
        {
            csv.Linha(e.Fonte, e.Id, m.Endpoint, m.Requisicoes, m.Tentativas, m.Retentativas, Status(m),
                m.PorDesfecho[Desfecho.Ok], m.PorDesfecho[Desfecho.Vazio], m.PorDesfecho[Desfecho.Erro4xx],
                m.PorDesfecho[Desfecho.Erro5xx], m.PorDesfecho[Desfecho.FalhaRede], m.PorDesfecho[Desfecho.Limite429],
                Pct(m.TaxaErro), Pct(m.TaxaVazio),
                Num(PerfilRequisicoes.Percentil(m.Latencias, 0.5)), Num(PerfilRequisicoes.Percentil(m.Latencias, 0.95)),
                Num(m.Latencias.Count == 0 ? null : m.Latencias.Max()),
                Num(PerfilRequisicoes.Percentil(m.Ttfbs, 0.5)), Num(PerfilRequisicoes.Percentil(m.Ttfbs, 0.95)),
                m.Bytes, string.Join(" | ", m.Erros));
        }
        csv.Salvar(caminho);
    }

    private static void Gravar429(string caminho, List<PerfilExecucao> execucoes)
    {
        var csv = new Csv("fonte", "execucao", "endpoint", "chave", "ordem_tentativa", "segundos_desde_inicio",
            "tentativas_ultimos_60s", "ritmo_configurado_min", "retry_after", "headers_limite");
        foreach (var e in execucoes)
        foreach (var ev in e.Eventos429)
            csv.Linha(e.Fonte, e.Id, ev.Endpoint, ev.Chave, ev.Ordem, Num(ev.SegundosDesdeInicio),
                ev.TentativasUltimos60s, Num(e.RitmoConfigurado), ev.RetryAfter, ev.HeadersLimite);
        csv.Salvar(caminho);
    }

    private static void GravarRecorte(string caminho, List<PerfilExecucao> execucoes)
    {
        var csv = new Csv("fonte", "execucao", "endpoint", "recorte", "campo_uf", "nota", "respostas_com_registros",
            "respostas_sem_registros", "respostas_nao_json", "arquivos_lidos", "linhas_malformadas", "registros",
            "registros_sc", "pct_sc", "registros_outra_uf", "registros_sem_uf", "outras_ufs",
            "registros_filtro_origem", "pct_sc_no_filtro_origem");
        foreach (var e in execucoes)
        foreach (var p in e.Campos.PorEndpoint.Values.OrderBy(x => x.Endpoint))
        {
            var conta = p.ContaSc;
            var conferivel = p.Recorte.Recorte == "campo" && p.RegistrosFiltroOrigem > 0;
            csv.Linha(e.Fonte, e.Id, p.Endpoint, p.Recorte.Recorte, p.Recorte.CampoUf, p.Recorte.Nota,
                p.RespostasComRegistros, p.RespostasSemRegistros, p.RespostasNaoJson, p.ArquivosLidos, p.LinhasMalformadas,
                p.Registros, conta ? p.RegistrosSc : null, conta ? Pct(Razao(p.RegistrosSc, p.Registros)) : null,
                p.Recorte.Recorte == "campo" ? p.RegistrosOutraUf : null,
                p.Recorte.Recorte == "campo" ? p.RegistrosSemUf : null,
                string.Join(" ", p.OutrasUfs), p.RegistrosFiltroOrigem,
                conferivel ? Pct(Razao(p.ScFiltroOrigem, p.RegistrosFiltroOrigem)) : null);
        }
        csv.Salvar(caminho);
    }

    private static void GravarCampos(string caminho, List<PerfilExecucao> execucoes)
    {
        var csv = new Csv("fonte", "execucao", "endpoint", "campo", "tipos", "registros", "preenchido", "marcador",
            "vazio", "nulo", "ausente", "pct_preenchido", "registros_sc", "preenchido_sc", "pct_preenchido_sc");
        foreach (var e in execucoes)
        foreach (var p in e.Campos.PorEndpoint.Values.OrderBy(x => x.Endpoint))
        foreach (var (campo, c) in p.Campos.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var ausente = p.Registros - c.Total.Sum();
            csv.Linha(e.Fonte, e.Id, p.Endpoint, campo, string.Join(" ", c.Tipos), p.Registros,
                c.Total[(int)Estado.Preenchido], c.Total[(int)Estado.Marcador], c.Total[(int)Estado.Vazio],
                c.Total[(int)Estado.Nulo], ausente, Pct(Razao(c.Total[(int)Estado.Preenchido], p.Registros)),
                p.ContaSc ? p.RegistrosSc : null, p.ContaSc ? c.Sc[(int)Estado.Preenchido] : null,
                p.ContaSc ? Pct(Razao(c.Sc[(int)Estado.Preenchido], p.RegistrosSc)) : null);
        }
        csv.Salvar(caminho);
    }

    // ---------- resumo.md ----------

    private static void GravarResumo(string caminho, List<PerfilExecucao> execucoes, RegrasPerfil regras)
    {
        var md = new StringBuilder();
        md.AppendLine("# Perfil das execuções da sondagem");
        md.AppendLine();
        md.AppendLine($"Gerado em {DateTime.Now:yyyy-MM-dd HH:mm} a partir de `dados/`, sem acesso à rede. Detalhes completos nos CSV desta pasta.");
        md.AppendLine();
        md.AppendLine("**Como ler:**");
        md.AppendLine("- **Requisição** é a tentativa final de cada chamada; repetições por 5xx ou timeout aparecem em *retentativas*.");
        md.AppendLine("- **Vazio** é resposta sem dados sinalizada pela fonte: 204, ou o status listado em `config/perfil.json` (ex.: 404 \"Não há contrato publicado\" no PNCP). Não conta como erro.");
        md.AppendLine("- **Taxa de erro** = (4xx não vazios + 5xx + falhas de rede + 429) ÷ requisições.");
        md.AppendLine("- **p50/p95** da latência total (até o fim do corpo) de todas as tentativas com resposta HTTP, por interpolação linear (igual ao PERCENTIL.INC). Em downloads, a latência inclui a transferência do arquivo.");
        md.AppendLine($"- **Preenchido** = valor não nulo, não vazio e diferente dos marcadores ({string.Join(", ", regras.Marcadores.Select(x => $"\"{x}\""))}). Em listas, o registro conta como preenchido se algum elemento tiver valor.");
        md.AppendLine($"- **Registros de {regras.Uf}** dependem do recorte de cada endpoint (coluna *recorte*); a *nota* diz a que UF o campo se refere.");
        md.AppendLine();

        foreach (var e in execucoes)
        {
            var ultimo = e.Segmentos[^1];
            md.AppendLine($"## {e.Fonte} · {e.Id}");
            md.AppendLine();
            md.AppendLine($"- Situação: **{ultimo.Situacao}**{(ultimo.Detalhe is null ? "" : $" ({Linha(ultimo.Detalhe)})")} · {e.Segmentos.Count} segmento(s) · commit {string.Join(", ", e.Segmentos.Select(s => s.Commit[..Math.Min(7, s.Commit.Length)] + (s.CommitSujo ? "*" : "")).Distinct())}");
            md.AppendLine($"- Início {e.Segmentos[0].Inicio:dd/MM/yyyy HH:mm} · duração {Num(Duracao(e))} min · {e.Tentativas.Count} tentativas");
            md.AppendLine($"- Ritmo: limite declarado {(e.LimitePorMinuto is null ? "nenhum (base 60/min)" : Num(e.LimitePorMinuto) + "/min")}, configurado {Num(e.RitmoConfigurado)}/min, observado {Num(e.RitmoObservado)}/min");
            md.AppendLine($"- Cabeçalhos de limite nas respostas: {(e.HeadersLimite.Count == 0 ? "nenhum" : string.Join(", ", e.HeadersLimite))}");
            if (e.Eventos429.Count > 0)
                foreach (var ev in e.Eventos429)
                    md.AppendLine($"- **429** em `{ev.Endpoint}` na {ev.Ordem}ª tentativa, {Num(ev.SegundosDesdeInicio)} s após o início, com {ev.TentativasUltimos60s} tentativas nos 60 s anteriores; Retry-After: {ev.RetryAfter ?? "ausente"}");
            md.AppendLine();

            md.AppendLine("| Endpoint | Req. | OK | Vazio | Erro | Taxa de erro | p50 (ms) | p95 (ms) | Status |");
            md.AppendLine("|---|--:|--:|--:|--:|--:|--:|--:|---|");
            foreach (var m in e.Endpoints.Values.OrderBy(x => x.Endpoint))
                md.AppendLine($"| {m.Endpoint} | {m.Requisicoes} | {m.PorDesfecho[Desfecho.Ok]} | {m.PorDesfecho[Desfecho.Vazio]} | {m.Falhas} | {Pct(m.TaxaErro)}% | {Num(PerfilRequisicoes.Percentil(m.Latencias, 0.5))} | {Num(PerfilRequisicoes.Percentil(m.Latencias, 0.95))} | {Status(m)} |");
            var total = e.Endpoints.Values;
            var todas = total.SelectMany(m => m.Latencias).ToList();
            var req = total.Sum(m => m.Requisicoes);
            md.AppendLine($"| **total** | {req} | {total.Sum(m => m.PorDesfecho[Desfecho.Ok])} | {total.Sum(m => m.PorDesfecho[Desfecho.Vazio])} | {total.Sum(m => m.Falhas)} | {Pct(Razao(total.Sum(m => m.Falhas), req))}% | {Num(PerfilRequisicoes.Percentil(todas, 0.5))} | {Num(PerfilRequisicoes.Percentil(todas, 0.95))} | |");
            md.AppendLine();

            var comRegistros = e.Campos.PorEndpoint.Values.Where(p => p.Registros > 0).OrderBy(p => p.Endpoint).ToList();
            if (comRegistros.Count > 0)
            {
                md.AppendLine($"| Endpoint | Registros | {regras.Uf} | % {regras.Uf} | Recorte | Campos | 100% preenchidos | Nunca preenchidos |");
                md.AppendLine("|---|--:|--:|--:|---|--:|--:|---|");
                foreach (var p in comRegistros)
                {
                    var cheios = p.Campos.Count(c => c.Value.Total[(int)Estado.Preenchido] == p.Registros);
                    var nunca = p.Campos.Where(c => c.Value.Total[(int)Estado.Preenchido] == 0).Select(c => c.Key).OrderBy(x => x, StringComparer.Ordinal).ToList();
                    var nuncaTxt = nunca.Count == 0 ? "—" : string.Join(", ", nunca.Take(8).Select(x => $"`{x}`")) + (nunca.Count > 8 ? $" (+{nunca.Count - 8})" : "");
                    var recorte = p.Recorte.Recorte + (p.Recorte.CampoUf is null ? "" : $" (`{p.Recorte.CampoUf}`)");
                    if (p.Recorte.Recorte == "campo" && p.RegistrosFiltroOrigem > 0)
                        recorte += $"; filtro na origem: {Pct(Razao(p.ScFiltroOrigem, p.RegistrosFiltroOrigem))}% de {regras.Uf}";
                    md.AppendLine($"| {p.Endpoint} | {p.Registros} | {(p.ContaSc ? p.RegistrosSc : "—")} | {(p.ContaSc ? Pct(Razao(p.RegistrosSc, p.Registros)) + "%" : "—")} | {recorte} | {p.Campos.Count} | {cheios} | {nuncaTxt} |");
                }
                md.AppendLine();
            }
            var semDados = e.Campos.PorEndpoint.Values.Where(p => p.Registros == 0 && p.RespostasNaoJson > 0).Select(p => p.Endpoint).Order().ToList();
            if (semDados.Count > 0)
            {
                md.AppendLine($"Respostas em HTML ou texto, sem registros para perfilar: {string.Join(", ", semDados)}.");
                md.AppendLine();
            }
        }
        File.WriteAllText(caminho, md.ToString(), new UTF8Encoding(false));
    }

    // ---------- auxiliares ----------

    private static double? Duracao(PerfilExecucao e)
    {
        var fim = e.Segmentos[^1].Fim;
        return fim is null ? null : e.Segmentos.Sum(s => s.Fim is { } f ? (f - s.Inicio).TotalMinutes : 0);
    }

    private static string Status(MetricasEndpoint m) => string.Join(" ", m.PorStatus.Select(s => $"{(s.Key == 0 ? "sem-resposta" : s.Key)}×{s.Value}"));
    private static double? Razao(double a, double b) => b == 0 ? null : a / b;
    private static string? Pct(double? x) => x is null ? null : (x.Value * 100).ToString("0.0", Br);
    private static string? Num(double? x) => x?.ToString("0.#", Br);
    private static string Linha(string s) => s.Split('\n')[0].Trim();

    private sealed class Csv(params string[] colunas)
    {
        private readonly StringBuilder _sb = new StringBuilder().AppendLine(string.Join(";", colunas));

        public void Linha(params object?[] valores) =>
            _sb.AppendLine(string.Join(";", valores.Select(v => Escapar(v switch
            {
                null => "",
                IFormattable f => f.ToString(null, Br),
                _ => v.ToString() ?? "",
            }))));

        private static string Escapar(string s) =>
            s.IndexOfAny([';', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

        public void Salvar(string caminho) => File.WriteAllText(caminho, _sb.ToString(), Utf8Bom);
    }
}
