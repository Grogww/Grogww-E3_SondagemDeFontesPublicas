using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Sonda.Perfilador;

/// <summary>Corpo bruto de uma resposta não-2xx, guardado para as regras de "vazio".</summary>
public sealed class CorpoBruto(string texto, JsonElement? json)
{
    public bool Contem(string trecho) =>
        texto.Contains(trecho, StringComparison.OrdinalIgnoreCase)
        || (json is { } j && TextosDe(j).Any(s => s.Contains(trecho, StringComparison.OrdinalIgnoreCase)));

    public bool Casa(System.Text.RegularExpressions.Regex re) => re.IsMatch(texto);

    private static IEnumerable<string> TextosDe(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => [e.GetString()!],
        JsonValueKind.Object => e.EnumerateObject().SelectMany(p => TextosDe(p.Value)),
        JsonValueKind.Array => e.EnumerateArray().SelectMany(TextosDe),
        _ => [],
    };
}

/// <summary>Estado de um atributo num registro. A ordem importa: o maior vence quando há vários valores (listas).</summary>
public enum Estado { Nulo = 1, Vazio = 2, Marcador = 3, Preenchido = 4 }

public sealed class EstatCampo
{
    public int[] Total { get; } = new int[5];
    public int[] Sc { get; } = new int[5];
    public SortedSet<string> Tipos { get; } = new();
}

/// <summary>Perfil dos registros de um endpoint numa execução (bruto/ e arquivos baixados).</summary>
public sealed class PerfilEndpointCampos
{
    public string Endpoint { get; init; } = "";
    public RegraRecorte Recorte { get; init; } = new();
    public int RespostasComRegistros { get; set; }
    public int RespostasSemRegistros { get; set; }
    public int RespostasNaoJson { get; set; }
    public int ArquivosLidos { get; set; }
    public int LinhasMalformadas { get; set; }
    public long Registros { get; set; }
    public long RegistrosSc { get; set; }
    public long RegistrosOutraUf { get; set; }
    public long RegistrosSemUf { get; set; }
    public long RegistrosFiltroOrigem { get; set; }
    public long ScFiltroOrigem { get; set; }
    public SortedSet<string> OutrasUfs { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, EstatCampo> Campos { get; } = new(StringComparer.Ordinal);

    /// <summary>Há como contar SC neste endpoint?</summary>
    public bool ContaSc => Recorte.Recorte is "campo" or "juncao" or "fonte" or "herdado";

    /// <summary>A UF é lida registro a registro (campo ou junção), com sem UF e outra UF.</summary>
    public bool PorRegistro => Recorte.Recorte is "campo" or "juncao";

    /// <summary>Registros em que a UF pôde ser lida (no recorte por campo ou junção, exclui os sem UF).</summary>
    public long RegistrosComUf => PorRegistro ? Registros - RegistrosSemUf : Registros;

    /// <summary>% de SC sobre os registros com UF; nulo quando não há como saber (todos sem UF).</summary>
    public double? FracaoSc => ContaSc && RegistrosComUf > 0 ? (double)RegistrosSc / RegistrosComUf : null;

    public double? FracaoSemUf => PorRegistro && Registros > 0 ? (double)RegistrosSemUf / Registros : null;
}

public sealed class PerfilCampos
{
    private readonly RegrasPerfil _regras;
    private readonly RegrasFonte _fonte;
    private readonly HashSet<string> _marcadores;

    public Dictionary<string, PerfilEndpointCampos> PorEndpoint { get; } = new();
    /// <summary>(endpoint, chave) → corpo das respostas não-2xx e dos endpoints com regra de "sem conteúdo".</summary>
    public Dictionary<(string, string), CorpoBruto> Corpos { get; } = new();

    public PerfilCampos(RegrasPerfil regras, RegrasFonte fonte)
    {
        _regras = regras;
        _fonte = fonte;
        _marcadores = new HashSet<string>(regras.Marcadores, StringComparer.OrdinalIgnoreCase);
    }

    private PerfilEndpointCampos Endpoint(string nome)
    {
        if (!PorEndpoint.TryGetValue(nome, out var p))
            PorEndpoint[nome] = p = new PerfilEndpointCampos { Endpoint = nome, Recorte = _fonte.Recorte(nome) };
        return p;
    }

    // ---------- bruto/<endpoint>.jsonl ----------

    public void LerBruto(string pastaExecucao)
    {
        var pasta = Path.Combine(pastaExecucao, "bruto");
        if (!Directory.Exists(pasta)) return;
        foreach (var arquivo in Directory.EnumerateFiles(pasta, "*.jsonl").Order())
        {
            // Os nomes de endpoint só têm letras, dígitos e hífen: o nome do arquivo é o endpoint.
            var p = Endpoint(Path.GetFileNameWithoutExtension(arquivo));
            foreach (var linha in File.ReadLines(arquivo))
            {
                if (string.IsNullOrWhiteSpace(linha)) continue;
                using var doc = JsonDocument.Parse(linha);
                var r = doc.RootElement;
                var status = r.GetProperty("status").GetInt32();
                var ehJson = r.GetProperty("corpoJson").GetBoolean();
                var corpo = r.GetProperty("corpo");
                var guardar = status is < 200 or >= 300 || _fonte.SemConteudo.Any(s => s.Endpoints.Contains(p.Endpoint));
                if (guardar)
                    Corpos[(p.Endpoint, r.GetProperty("chave").GetString()!)] = ehJson
                        ? new CorpoBruto(corpo.GetRawText(), corpo.Clone())
                        : new CorpoBruto(corpo.GetString() ?? "", null);
                if (status is < 200 or >= 300) continue;
                if (status == 204) continue;
                if (!ehJson) { p.RespostasNaoJson++; continue; }

                var registros = (p.Recorte.Registros is { } local ? RegistrosEm(corpo, local.Split('.'), 0) : Registros(corpo)).ToList();
                if (registros.Count == 0) { p.RespostasSemRegistros++; continue; }
                p.RespostasComRegistros++;
                var filtroOrigem = FiltroNaOrigem(r.GetProperty("url").GetString()!);
                foreach (var reg in registros) Perfilar(p, reg, filtroOrigem);
            }
        }
    }

    /// <summary>Registros de um corpo: lista na raiz, lista em data/resultado (paginado) ou o próprio objeto (detalhe).</summary>
    private static IEnumerable<JsonElement> Registros(JsonElement corpo)
    {
        if (corpo.ValueKind == JsonValueKind.Array) return corpo.EnumerateArray();
        if (corpo.ValueKind != JsonValueKind.Object) return [];
        foreach (var nome in new[] { "data", "resultado", "content", "items", "registros" })
            if (corpo.TryGetProperty(nome, out var lista) && lista.ValueKind == JsonValueKind.Array)
                return lista.EnumerateArray();
        return [corpo];
    }

    /// <summary>Registros no caminho da regra; atributo ausente = nenhum registro.</summary>
    private static IEnumerable<JsonElement> RegistrosEm(JsonElement e, string[] partes, int i)
    {
        if (i == partes.Length)
            return e.ValueKind == JsonValueKind.Array ? e.EnumerateArray() : [e];
        var nome = partes[i].EndsWith("[]") ? partes[i][..^2] : partes[i];
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(nome, out var v)) return [];
        return partes[i].EndsWith("[]") && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().SelectMany(x => RegistrosEm(x, partes, i + 1)).ToList()
            : RegistrosEm(v, partes, i + 1);
    }

    private bool FiltroNaOrigem(string url)
    {
        var q = url.IndexOf('?');
        if (q < 0) return false;
        foreach (var par in url[(q + 1)..].Split('&'))
        {
            var kv = par.Split('=', 2);
            if (kv.Length == 2 && _regras.ParametrosUf.Contains(kv[0], StringComparer.OrdinalIgnoreCase)
                && Uri.UnescapeDataString(kv[1]).Equals(_regras.Uf, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private void Perfilar(PerfilEndpointCampos p, JsonElement reg, bool filtroOrigem)
    {
        p.Registros++;
        var deSc = ClassificarUf(p, reg);
        if (filtroOrigem)
        {
            p.RegistrosFiltroOrigem++;
            if (deSc) p.ScFiltroOrigem++;
        }

        var estados = new Dictionary<string, Estado>(StringComparer.Ordinal);
        var tipos = new Dictionary<string, string>(StringComparer.Ordinal);
        Visitar(reg, "", estados, tipos);
        Somar(p, estados, deSc);
        foreach (var (caminho, tipo) in tipos) p.Campos[caminho].Tipos.Add(tipo);
    }

    private bool ClassificarUf(PerfilEndpointCampos p, JsonElement reg)
    {
        switch (p.Recorte.Recorte)
        {
            case "fonte" or "herdado":
                p.RegistrosSc++;
                return true;
            case "campo" when p.Recorte.CampoUf is { } campo:
                var v = Caminho(reg, campo);
                var uf = v is { ValueKind: JsonValueKind.String } s ? s.GetString()!.Trim() : null;
                if (string.IsNullOrEmpty(uf)) { p.RegistrosSemUf++; return false; }
                if (uf.Equals(_regras.Uf, StringComparison.OrdinalIgnoreCase)) { p.RegistrosSc++; return true; }
                p.RegistrosOutraUf++;
                p.OutrasUfs.Add(uf);
                return false;
            default:
                return false;
        }
    }

    private static JsonElement? Caminho(JsonElement e, string caminho)
    {
        foreach (var parte in caminho.Split('.'))
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(parte, out e)) return null;
        }
        return e;
    }

    private void Visitar(JsonElement e, string caminho, Dictionary<string, Estado> estados, Dictionary<string, string> tipos)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                var temFilho = false;
                foreach (var prop in e.EnumerateObject())
                {
                    temFilho = true;
                    Visitar(prop.Value, caminho.Length == 0 ? prop.Name : caminho + "." + prop.Name, estados, tipos);
                }
                // O próprio objeto também é um atributo: preenchido se tem ao menos um atributo.
                Marcar(caminho, temFilho ? Estado.Preenchido : Estado.Vazio, "objeto", estados, tipos);
                break;
            case JsonValueKind.Array:
                var n = 0;
                foreach (var item in e.EnumerateArray()) { n++; Visitar(item, caminho + "[]", estados, tipos); }
                // A própria lista também é um atributo: preenchida se tem ao menos um elemento.
                Marcar(caminho, n > 0 ? Estado.Preenchido : Estado.Vazio, "lista", estados, tipos);
                break;
            case JsonValueKind.Null:
                Marcar(caminho, Estado.Nulo, null, estados, tipos);
                break;
            case JsonValueKind.String:
                var s = e.GetString()!.Trim();
                Marcar(caminho, s.Length == 0 ? Estado.Vazio : _marcadores.Contains(s) ? Estado.Marcador : Estado.Preenchido, "texto", estados, tipos);
                break;
            case JsonValueKind.Number:
                Marcar(caminho, Estado.Preenchido, "número", estados, tipos);
                break;
            default:
                Marcar(caminho, Estado.Preenchido, "lógico", estados, tipos);
                break;
        }
    }

    private static void Marcar(string caminho, Estado estado, string? tipo, Dictionary<string, Estado> estados, Dictionary<string, string> tipos)
    {
        if (caminho.Length == 0) return;
        if (!estados.TryGetValue(caminho, out var atual) || estado > atual) estados[caminho] = estado;
        if (tipo is not null) tipos.TryAdd(caminho, tipo);
    }

    private static void Somar(PerfilEndpointCampos p, Dictionary<string, Estado> estados, bool deSc)
    {
        foreach (var (caminho, estado) in estados)
        {
            if (!p.Campos.TryGetValue(caminho, out var c)) p.Campos[caminho] = c = new EstatCampo();
            c.Total[(int)estado]++;
            if (deSc) c.Sc[(int)estado]++;
        }
    }

    // ---------- arquivos baixados (CSV, solto ou em ZIP) ----------

    /// <summary>Chaves vistas num endpoint referenciado por uma junção: todas e as de registros de SC.</summary>
    private sealed class Chaves(string campo)
    {
        public string Campo { get; } = campo;
        public HashSet<long> Todas { get; } = new();
        public HashSet<long> Sc { get; } = new();
    }

    // Leitura em fluxo (regra R3): os arquivos nunca são carregados inteiros; das junções só ficam
    // as chaves numéricas (ex.: CNPJ básico) em memória.
    public void LerArquivos(string pastaExecucao)
    {
        var chaves = new Dictionary<string, Chaves>(StringComparer.Ordinal);
        foreach (var r in _fonte.Endpoints.Values.Append(_fonte.Padrao))
            if (r is { Recorte: "juncao", Juncao: { } j }) chaves.TryAdd(j.Endpoint, new Chaves(j.Campo));

        // Endpoints com junção por último: dependem das chaves dos outros.
        foreach (var regra in _fonte.Arquivos.OrderBy(r => _fonte.Recorte(r.Endpoint).Recorte == "juncao"))
        {
            var pasta = Path.Combine(pastaExecucao, regra.Pasta);
            if (!Directory.Exists(pasta)) continue;
            var p = Endpoint(regra.Endpoint);
            var codificacao = regra.Codificacao.Equals("latin1", StringComparison.OrdinalIgnoreCase)
                ? Encoding.Latin1 : Encoding.GetEncoding(regra.Codificacao);
            var filtro = regra.Arquivo is null ? null
                : new System.Text.RegularExpressions.Regex(regra.Arquivo, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var coleta = chaves.GetValueOrDefault(regra.Endpoint);
            var juncao = p.Recorte is { Recorte: "juncao", Juncao: { } jj } ? chaves[jj.Endpoint] : null;
            foreach (var arquivo in Directory.EnumerateFiles(pasta, "*", SearchOption.AllDirectories).Order())
            {
                if (filtro is not null && !filtro.IsMatch(Path.GetFileName(arquivo))) continue;
                if (arquivo.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    using var zip = ZipFile.OpenRead(arquivo);
                    var entradas = zip.Entries.Where(x => x.Name.Length > 0
                        && (regra.Colunas is not null || x.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)));
                    foreach (var entrada in entradas)
                    {
                        using var leitor = new StreamReader(entrada.Open(), codificacao);
                        LerCsv(p, leitor, regra, coleta, juncao);
                    }
                }
                else if (arquivo.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                {
                    using var leitor = new StreamReader(arquivo, codificacao);
                    LerCsv(p, leitor, regra, coleta, juncao);
                }
            }
        }
    }

    private void LerCsv(PerfilEndpointCampos p, TextReader leitor, RegraArquivo regra, Chaves? coleta, Chaves? juncao)
    {
        p.ArquivosLidos++;
        var cabecalho = regra.Colunas?.ToArray();
        var estados = new Dictionary<string, Estado>(StringComparer.Ordinal);
        int iUf = -1, iChave = -1, iColeta = -1;
        void Indices()
        {
            iUf = p.Recorte.CampoUf is { } u ? Array.IndexOf(cabecalho!, u) : -1;
            iChave = p.Recorte.CampoChave is { } k ? Array.IndexOf(cabecalho!, k) : -1;
            iColeta = coleta is not null ? Array.IndexOf(cabecalho!, coleta.Campo) : -1;
            if (p.Recorte.Recorte == "campo" && iUf < 0)
                throw new InvalidOperationException($"{p.Endpoint}: coluna de UF '{p.Recorte.CampoUf}' não encontrada");
            if (p.Recorte.Recorte == "juncao" && iChave < 0)
                throw new InvalidOperationException($"{p.Endpoint}: coluna de chave '{p.Recorte.CampoChave}' não encontrada");
            if (coleta is not null && iColeta < 0)
                throw new InvalidOperationException($"{p.Endpoint}: coluna de chave '{coleta.Campo}' não encontrada");
        }
        if (cabecalho is not null) Indices();
        foreach (var linha in LinhasCsv(leitor, regra.Separador[0]))
        {
            if (cabecalho is null) { cabecalho = linha.Select(c => c.Trim()).ToArray(); Indices(); continue; }
            if (linha.Count == 1 && linha[0].Length == 0) continue; // linha em branco
            if (linha.Count != cabecalho.Length) p.LinhasMalformadas++;
            p.Registros++;
            var deSc = ClassificarLinha(p, linha, iUf, iChave, juncao);
            if (coleta is not null && iColeta < linha.Count && long.TryParse(linha[iColeta].Trim(), out var ch))
            {
                coleta.Todas.Add(ch);
                if (deSc) coleta.Sc.Add(ch);
            }
            estados.Clear();
            for (var i = 0; i < cabecalho.Length; i++)
            {
                var v = i < linha.Count ? linha[i].Trim() : null;
                estados[cabecalho[i]] = v is null ? Estado.Nulo
                    : v.Length == 0 ? Estado.Vazio
                    : _marcadores.Contains(v) ? Estado.Marcador : Estado.Preenchido;
            }
            Somar(p, estados, deSc);
        }
        if (cabecalho is not null)
            foreach (var c in cabecalho) if (p.Campos.TryGetValue(c, out var e)) e.Tipos.Add("texto (CSV)");
    }

    private bool ClassificarLinha(PerfilEndpointCampos p, List<string> linha, int iUf, int iChave, Chaves? juncao)
    {
        switch (p.Recorte.Recorte)
        {
            case "fonte" or "herdado":
                p.RegistrosSc++;
                return true;
            case "campo":
                var uf = iUf < linha.Count ? linha[iUf].Trim() : "";
                if (uf.Length == 0) { p.RegistrosSemUf++; return false; }
                if (uf.Equals(_regras.Uf, StringComparison.OrdinalIgnoreCase)) { p.RegistrosSc++; return true; }
                p.RegistrosOutraUf++;
                p.OutrasUfs.Add(uf);
                return false;
            case "juncao" when juncao is not null:
                if (iChave >= linha.Count || !long.TryParse(linha[iChave].Trim(), out var ch) || !juncao.Todas.Contains(ch))
                { p.RegistrosSemUf++; return false; }
                if (juncao.Sc.Contains(ch)) { p.RegistrosSc++; return true; }
                p.RegistrosOutraUf++;
                return false;
            default:
                return false;
        }
    }

    /// <summary>Leitor de CSV em fluxo, com aspas (campos com separador ou quebra de linha) e "" como aspa literal.</summary>
    internal static IEnumerable<List<string>> LinhasCsv(TextReader r, char sep)
    {
        var campos = new List<string>();
        var atual = new StringBuilder();
        var entreAspas = false;
        int c;
        while ((c = r.Read()) != -1)
        {
            var ch = (char)c;
            if (entreAspas)
            {
                if (ch == '"')
                {
                    if (r.Peek() == '"') { atual.Append('"'); r.Read(); }
                    else entreAspas = false;
                }
                else atual.Append(ch);
            }
            else if (ch == '"' && atual.Length == 0) entreAspas = true;
            else if (ch == sep) { campos.Add(atual.ToString()); atual.Clear(); }
            else if (ch == '\r') { }
            else if (ch == '\n')
            {
                campos.Add(atual.ToString()); atual.Clear();
                yield return campos;
                campos = new List<string>();
            }
            else atual.Append(ch);
        }
        if (atual.Length > 0 || campos.Count > 0) { campos.Add(atual.ToString()); yield return campos; }
    }
}
