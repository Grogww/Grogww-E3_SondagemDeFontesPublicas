// Módulo Matriz: ponto único de entrada das sondas.
// Uso: sonda <fonte> [--execucao <id>] [--config <caminho>]
//   sem --execucao: abre execução nova, datada (dados/<fonte>/<yyyyMMdd-HHmmss>/)
//   com --execucao: retoma a execução indicada, pulando o que já foi coletado
// Códigos de saída: 0 concluída · 1 erro · 2 uso/configuração · 3 interrompida por 429 · 130 cancelada
using System.Reflection;
using Sonda.Core;

var raiz = LocalizarRaiz();
var sondas = Assembly.Load("Sonda.Fontes").GetTypes()
    .Where(t => typeof(ISonda).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false })
    .Select(t => (ISonda)Activator.CreateInstance(t)!)
    .ToDictionary(s => s.Nome, StringComparer.OrdinalIgnoreCase);

string? fonte = null, id = null;
var caminhoConfig = Path.Combine(raiz, "config", "fontes.json");
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--execucao" when i + 1 < args.Length: id = args[++i]; break;
        case "--config" when i + 1 < args.Length: caminhoConfig = Path.GetFullPath(args[++i]); break;
        default: fonte ??= args[i]; break;
    }
}

if (fonte is null || !sondas.TryGetValue(fonte, out var sonda))
{
    Console.Error.WriteLine("Uso: sonda <fonte> [--execucao <id>] [--config <caminho>]");
    Console.Error.WriteLine("Sondas disponíveis: " + (sondas.Count == 0 ? "(nenhuma)" : string.Join(", ", sondas.Keys.Order())));
    return 2;
}

var geral = ConfigGeral.Carregar(caminhoConfig);
if (!geral.Fontes.TryGetValue(sonda.Nome, out var cfg))
{
    Console.Error.WriteLine($"Fonte '{sonda.Nome}' ausente em {caminhoConfig}.");
    return 2;
}

string? chave = null;
if (cfg.VariavelChave is not null)
{
    chave = Environment.GetEnvironmentVariable(cfg.VariavelChave);
    if (string.IsNullOrWhiteSpace(chave))
    {
        Console.Error.WriteLine($"Defina a variável de ambiente {cfg.VariavelChave} com a chave de acesso.");
        return 2;
    }
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Execucao exec;
try { exec = Execucao.Abrir(raiz, geral, sonda.Nome, id, chave); }
catch (InvalidOperationException e) { Console.Error.WriteLine(e.Message); return 2; }

using (exec)
{
    try
    {
        await sonda.ExecutarAsync(exec, cts.Token);
        exec.Finalizar("concluida");
        return 0;
    }
    catch (LimiteAtingidoException e)
    {
        exec.Finalizar("interrompida-429", e.Message);
        Retomar();
        return 3;
    }
    catch (OperationCanceledException) when (cts.IsCancellationRequested)
    {
        exec.Finalizar("cancelada");
        Retomar();
        return 130;
    }
    catch (Exception e)
    {
        exec.Finalizar("erro", e.ToString());
        Console.Error.WriteLine(e);
        Retomar();
        return 1;
    }

    void Retomar() => Console.WriteLine($"Para retomar: sonda {sonda.Nome} --execucao {exec.Id}");
}

// Raiz do repositório: primeira pasta acima do executável que contém Sonda.sln.
static string LocalizarRaiz()
{
    for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        if (File.Exists(Path.Combine(d.FullName, "Sonda.sln"))) return d.FullName;
    return Directory.GetCurrentDirectory();
}
