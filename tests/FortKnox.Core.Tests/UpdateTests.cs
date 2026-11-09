using FortKnox;
using Xunit;

namespace FortKnox.Core.Tests;

public sealed class UpdateTests
{
    private const string Pt =
        "   - \r   \\ \r" +
        "Nome                                                               ID                           Versão        Disponível    Origem\r\n" +
        "----------------------------------------------------------------------------------------------------------------------------------\r\n" +
        "IObit Software Updater                                             IObit.IObitSoftwareUpdater   7.0.0.16      9.2.0.13      winget\r\n" +
        "Microsoft Visual C++ 2015-2022 Redistributable (x86) - 14.42.34438 Microsoft.VCRedist.2015+.x86 14.42.34438.0 14.51.36247.0 winget\r\n" +
        "Ollama version 0.40.14                                             Ollama.Ollama                0.40.14       0.40.15       winget\r\n" +
        "3 atualizações disponíveis.\r\n" +
        "1 pacotes têm números de versão que não podem ser determinados. Use --include-unknown para ver todos os resultados.\r\n";

    [Fact]
    public void Parses_PortugueseTable_WithSpinnerAndFooter()
    {
        var list = UpdateService.Parse(Pt);
        Assert.Equal(3, list.Count);
        Assert.Equal(new AppUpdate("Microsoft Visual C++ 2015-2022 Redistributable (x86) - 14.42.34438", "Microsoft.VCRedist.2015+.x86",
                                   "14.42.34438.0", "14.51.36247.0", "winget"), list[1]);
        Assert.Equal("Ollama.Ollama", list[2].Id);
    }

    [Fact]
    public void Parses_EnglishTable()
    {
        const string en =
            "Name            Id              Version Available Source\n" +
            "-------------------------------------------------------\n" +
            "Mozilla Firefox Mozilla.Firefox 120.0   121.0     winget\n" +
            "7-Zip           7zip.7zip       23.01   24.08     winget\n" +
            "2 upgrades available.\n";
        var list = UpdateService.Parse(en);
        Assert.Equal(new[] { "Mozilla.Firefox", "7zip.7zip" }, list.Select(u => u.Id));
        Assert.Equal("24.08", list[1].Available);
    }

    [Fact]
    public void NothingToUpdate_GivesEmptyList()
    {
        Assert.Empty(UpdateService.Parse("Nenhum pacote instalado encontrado que corresponda aos critérios de entrada.\r\n"));
        Assert.Empty(UpdateService.Parse(""));
    }

    [Fact]
    public void Skips_TruncatedIds()
    {
        const string t =
            "Name      Id         Version Available Source\n" +
            "---------------------------------------------\n" +
            "Long App  Vendor.Lo… 1.0     2.0       winget\n" +
            "Short     Short.App  1.0     2.0       winget\n";
        Assert.Equal(new[] { "Short.App" }, UpdateService.Parse(t).Select(u => u.Id));
    }

    [Fact]
    public void Describe_KnownCodes()
    {
        Assert.True(UpdateService.Describe(0, "").Ok);
        Assert.True(UpdateService.Describe(unchecked((int)0x8A15002B), "").Ok);
        Assert.False(UpdateService.Describe(unchecked((int)0x8A150101), "").Ok);
        var (ok, msg) = UpdateService.Describe(5, "linha 1\nErro final do instalador\n");
        Assert.False(ok);
        Assert.Contains("Erro final do instalador", msg);
    }
}
