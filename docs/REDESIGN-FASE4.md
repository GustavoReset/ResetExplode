# Fort Knox · Fase 4.1: identidade visual aplicada no app

Escopo: as telas WPF do app, exceto a MURALHA, que está sendo trabalhada em outra frente e só herda os estilos globais.
O código completo de cada arquivo está neste PR. Aqui ficam o diagnóstico, a proposta e o trecho que muda o comportamento.

Verificação:
- Compila no Linux (`EnableWindowsTargeting`) e no Windows.
- `FortKnox.exe --capturas <pasta>` percorreu as 27 páginas e o PAINEL com a mandala aberta, sem exceção.

---

## 1. Base visual: `Theme.xaml`, `UI/Components.xaml`, `UI/Templates.xaml`, janelas

**Diagnóstico**
- Cantos arredondados genéricos (raio 6 e 10) em tudo: cartões, botões, chips, campos, selos, dicas e diálogos.
- A geometria industrial do Brandbook (chanfro a 45°) não aparecia em nenhum lugar.

**Proposta**
- Novo `UI/Controls/ChamferBorder.cs`, uma moldura que desenha fundo e contorno no mesmo polígono chanfrado e recorta o conteúdo nele.
  - Por padrão corta o canto superior esquerdo e o inferior direito, como o símbolo.
  - `AllCorners` corta os quatro cantos (vira octógono).
- O que foi convertido:
  - **Todos os templates:** botões, chip, interruptor, TextBox, PasswordBox, item de menu, dica, selo, estado vazio e diálogo.
  - **Todos os cartões das telas:** usam o novo estilo `FkCard`, com chanfro de 12 px.
  - **Elementos pequenos (raio ≤ 3):** ficaram retos.

**Código**
```csharp
public static StreamGeometry Shape(Rect r, double cut, bool all)   // polígono chanfrado
protected override void OnRender(DrawingContext dc)
{   // traço meio pixel para dentro: nítido e não é recortado
    var r = pen != null ? new Rect(t / 2, t / 2, w - t, h - t) : new Rect(0, 0, w, h);
    dc.DrawGeometry(Background, pen, Shape(r, Cut - t / 2, AllCorners));
}
```
```xml
<Style x:Key="FkCard" TargetType="ui:ChamferBorder">
    <Setter Property="Background" Value="{DynamicResource CardFill}"/>
    <Setter Property="BorderBrush" Value="{DynamicResource Line}"/>
    <Setter Property="BorderThickness" Value="1"/>
    <Setter Property="Cut" Value="12"/>
    <Setter Property="Padding" Value="20"/>
</Style>
```

## 2. Animação de processo: `UI/Controls/FkActivity.cs` + `StatusLine`

**Diagnóstico**
- Processos longos ("Lendo os discos…", "Instalando…", "Rastreando…") mostravam só uma frase parada.
- Não havia sinal visual de que algo estava rodando.

**Proposta**
- **`FkActivity`:** versão nativa do componente de anéis da identidade.
  - O símbolo fica fixo no centro. Um anel tracejado gira devagar e um feixe ouro percorre a órbita.
  - Gira só com `IsActive`, com a peça visível e com "Efeitos de animação" do Windows ligados.
  - Abaixo de 28 px vira o indicador compacto, sem o símbolo.
- **`StatusLine`:** substitui o TextBlock de status sem mudar o código das telas, que continua fazendo `X.Text = "…"`.
  - Enquanto a frase termina em "…", o indicador compacto gira ao lado.
  - Quando vira resultado, o indicador some.
  - Aplicado em cerca de 30 status de 17 telas.

```csharp
private void Update()
{
    var t = Text?.TrimEnd() ?? "";
    SetValue(ActiveKey, IsBusy ?? (t.EndsWith('…') || t.EndsWith("...")));
}
```

## 3. Telas (Sentinela, Forja, Arena, Arsenal, Oficina, Radar, Vigia, Alicerce, Prisma, Cofre, Prensa)

**Diagnóstico**
- Cada tela abria com um subtítulo de uma ou duas linhas.
- Quase todo cartão tinha um parágrafo explicativo de 2 a 4 linhas, cerca de 35 no total. A Forja·Benchmark tinha sete.
- Listas (Limpeza, Apps, Otimização, resultados do Benchmark) repetiam descrição e observação em cada linha, triplicando a altura.

**Proposta**
- **Título da página:** fica só o H1 (Michroma). O subtítulo virou o ícone ⓘ ao lado, com a frase na dica (`InfoTip`).
- **Parágrafo de cartão:** saiu da tela e virou dica no rótulo da seção (mono ouro).
  - Quando o cartão não tem rótulo, o ⓘ fica no canto superior direito.
- **Listas:** cada linha mostra nome e valor. Descrição e observação aparecem ao passar o mouse.
- **Status nomeados** (contagens e resultados preenchidos pelo código) viraram `StatusLine`.
- **Amostras de cor do PRISMA e balão do gráfico do PAINEL:** chanfrados.

```xml
<StackPanel Orientation="Horizontal">
    <TextBlock x:Name="CatSummary" Text="O QUE PODE SAIR" Style="{StaticResource Label}"/>
    <TextBlock Style="{StaticResource InfoTip}" ToolTip="Só caches e temporários: …"/>
</StackPanel>
```

## 4. PAINEL: `Modules/Painel/ModuleMandala.cs` e `DashView`

**Diagnóstico**
- O painel inicial não tinha porta de entrada para os 12 módulos. O caminho era só o menu lateral.
- O cabeçalho tinha a frase de marca como subtítulo.

**Proposta**
- A mandala de módulos foi para o topo do PAINEL, dentro de um cartão chanfrado.
- **Navegação:**
  - Começa comprimida, com o logotipo vertical num núcleo octogonal.
  - Clique no centro: os 12 módulos abrem em órbita. O hover acende o módulo e apaga os demais.
  - Clique no módulo: abrem as ferramentas dele. Um módulo com uma ferramenta só abre direto.
  - Clique na ferramenta: a mandala some e a página abre pela mesma navegação do menu.
  - Esc volta um nível. Ao sair do PAINEL, a mandala volta a ficar comprimida.
- **Movimento:**
  - Só nas transições: abrir, escolher e sair.
  - Na primeira exibição, um convite de 2 pulsos que para.
  - Não há laço contínuo. Respeita as animações do Windows.

```csharp
Mandala.Navigate += page => host.Navigate(page);                      // mesma navegação do menu
IsVisibleChanged += (_, e) => { if (!(bool)e.NewValue) Mandala.ResetClosed(); };
```

## 5. Revisão visual: `--capturas`

`FortKnox.exe --capturas <pasta> [--claro]` faz o seguinte:
1. Abre cada página do menu.
2. Grava o PNG da janela, copiado da tela.
3. Fecha o app.

Nas imagens também entram o PAINEL com a mandala aberta e o PAINEL com o ARSENAL escolhido.

## Fica para a próxima fatia
- **MURALHA:** chanfros, ⓘ e `StatusLine`, quando a frente dela fechar.
- **Mensagens longas no código** (ex.: aviso do winget, texto do Cofre bloqueado): encurtar para microcopy.
