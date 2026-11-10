namespace FortKnox;

/// <summary>
/// Catálogo único de navegação do Fort Knox (Home/mandala e menu lateral leem daqui).
/// Nível 1: as quatro áreas fixas — Office, Software, Redes e Defesa.
/// Nível 2: as ferramentas de cada área (cada uma abre uma página existente pelo Tag do menu).
/// Nada aqui cria tela nova: só organiza as páginas que já existem.
/// </summary>
public enum ToolArea { Office, Software, Redes, Defesa }

/// <summary>Ferramenta = uma página do app.</summary>
public sealed record ToolItem(string Name, string Glyph, string Page, string Description);

/// <summary>Área de nível 1 da mandala e do menu lateral.</summary>
public sealed record ToolCategory(string Name, ToolArea Area, string Glyph, string Tagline, IReadOnlyList<ToolItem> Tools);

public static class AppCatalog
{
    // Ícones: Segoe MDL2 Assets (a mesma fonte "Icons" do tema).
    public static IReadOnlyList<ToolCategory> Categories { get; } = new[]
    {
        new ToolCategory("Office", ToolArea.Office, "\uE8A5", "Documentos, PDF e senhas", new ToolItem[]
        {
            new("PDF", "\uE8A5", "PagePdf", "Mesclar, dividir, comprimir e extrair texto de PDFs"),
            new("Senhas", "\uE72E", "PageVault", "Cofre local, aberto com o Windows Hello"),
        }),

        new ToolCategory("Software", ToolArea.Software, "\uE71D", "Sistema, manutenção e desempenho", new ToolItem[]
        {
            new("Sistema", "\uE7F8", "PageSystem", "Processador, memória, placa-mãe e Windows"),
            new("Sensores", "\uE9D9", "PageSensors", "Temperaturas, voltagens e ventoinhas em tempo real"),
            new("Placa de vídeo", "\uE7F4", "PageGpu", "Especificações e sensores da GPU"),
            new("Discos", "\uEDA2", "PageDisks", "Saúde S.M.A.R.T., desgaste e temperatura"),
            new("Energia", "\uE7E8", "PageEnergy", "Bateria, fonte e plano de energia"),
            new("Benchmark", "\uE8B9", "PageBench", "CPU, GPU, ray tracing, memória e disco"),
            new("Stress test", "\uE945", "PageStress", "Carga máxima com corte por temperatura"),
            new("Overclock", "\uEC4A", "PageOc", "Limite de energia, teto de clock e estabilidade"),
            new("Biblioteca", "\uE8F1", "PageLauncher", "Todos os jogos instalados e perfis por jogo"),
            new("FPS estimado", "\uEC49", "PageGames", "Quanto cada jogo rende neste PC"),
            new("Gargalo", "\uE9D2", "PageBottleneck", "Qual peça está limitando o desempenho"),
            new("Overlay", "\uE81E", "PageOverlay", "FPS e sensores sobre o jogo (Ctrl+Shift+O)"),
            new("Otimização", "\uE9F3", "PageOptimize", "Memória, programas e ajustes para jogos"),
            new("Limpeza", "\uE74D", "PageCleanup", "Temporários, caches e Lixeira"),
            new("Desinstalar", "\uE7A6", "PageUninstall", "Remove programas e as sobras que eles deixam"),
            new("Inicialização", "\uE823", "PageStartup", "O que abre junto com o Windows"),
            new("Instalar apps", "\uE719", "PageApps", "Os essenciais pós-formatação, versão oficial"),
            new("Atualizar apps", "\uE895", "PageUpdates", "Versões novas pelo winget"),
            new("Drivers", "\uE896", "PageDriver", "Verificar, baixar e fazer backup de drivers"),
            new("Portas COM", "\uE88E", "PageMakerPorts", "Portas seriais e dispositivos USB fantasmas"),
            new("Drivers seriais", "\uE950", "PageMakerDrv", "CH340, CP210x, FTDI, Prolific e Arduino"),
            new("Iluminação", "\uE790", "PageInteg", "iCUE, OpenRGB e Razer Chroma"),
        }),

        new ToolCategory("Redes", ToolArea.Redes, "\uE839", "Conexão, latência e tráfego", new ToolItem[]
        {
            new("Teste de conexão", "\uE839", "PageNet", "Latência, jitter, perda, DNS, download e upload"),
            new("Traceroute", "\uE8B7", "PageNet", "O caminho dos pacotes até o destino, salto a salto"),
            new("Consumo por programa", "\uE9D9", "PageNet", "Quanto da rede cada programa está usando"),
        }),

        new ToolCategory("Defesa", ToolArea.Defesa, "\uEA18", "Antivírus, varredura e quarentena", new ToolItem[]
        {
            new("Varredura", "\uEA18", "PageShield", "Verificação em camadas com YARA e segunda opinião"),
            new("Quarentena", "\uE72E", "PageShield", "Restaure ou exclua os itens isolados"),
        }),
    };

    public static IReadOnlyList<ToolItem> AllTools { get; } = Categories.SelectMany(c => c.Tools).ToArray();

    public static ToolCategory? CategoryOf(string page) =>
        Categories.FirstOrDefault(c => c.Tools.Any(t => t.Page == page));

    public static ToolItem? Find(string page) =>
        AllTools.FirstOrDefault(t => t.Page == page);

    /// <summary>Rótulo curto de uma página para a barra de título ("Software · Limpeza").</summary>
    public static string LabelFor(string page)
    {
        var category = CategoryOf(page);
        var tool = Find(page);
        return category == null || tool == null ? "" : $"{category.Name} · {tool.Name}";
    }
}
