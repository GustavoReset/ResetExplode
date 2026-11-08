# Fort Knox · Revisão de código: PDF (Etapa 2)

Escopo: `Modules/Pdf/*`, `MainWindow.Pdf.cs`, pacotes no `FortKnox.csproj`.
Dependências: **PDFsharp 6.2.4** (MIT) e **PdfPig 0.1.16** (Apache-2.0), versões fixas. Sem iText (AGPL, incompatível com o repositório MIT).

## 1. O que foi testado

`tests/FortKnox.Tests/PdfTests.cs` monta PDFs reais à mão (texto Helvetica, imagens JPEG RGB e cinza, PDF com senha) e roda cada operação:

| Área | Casos cobertos |
|---|---|
| Intervalos (`1-3,5,8-`) | 14 casos válidos/inválidos (zero, fora do documento, invertido, `-3`, `1-2-3`, vírgulas vazias) |
| Mesclar | ordem das páginas, mesmo arquivo 3×, saída = entrada (recusa, inclusive maiúsculas/minúsculas), 1 arquivo só, entrada inválida (sem saída criada, sem `.tmp`), cancelamento, saída existente substituída |
| Dividir | por intervalos, uma página por arquivo, "8-" aberto, **nunca sobrescreve** (cria `(2)`), intervalo inválido, **cancelar no meio apaga as partes já criadas** |
| Extrair texto | texto por página, páginas sem texto contadas (aviso de OCR) |
| Comprimir | JPEG RGB e cinza recomprimidos (ganho > 20%, imagem 2400→2000 px, continua JPEG decodificável com o mesmo nº de componentes, texto intacto), nível forte < leve, PDF sem imagens não gera arquivo, saída = entrada recusada, cancelamento |
| Robustez | arquivo que não é PDF, **PDF truncado em 4 pontos**, arquivo inexistente, PDF com senha |
| Recursos | depois de **toda** operação o arquivo de origem pode ser apagado (nenhum handle preso) e não sobra `.tmp` |

Resultado: **38 testes de PDF + 23 de Vault**, mais **5 de migração de `%AppData%`**: 66/66 passando.

Teste de interface (`MainWindow` real, não elevada): navegação pelo menu, adicionar/reordenar/limpar na mesclagem, ler páginas em segundo plano, dividir por intervalos, intervalo inválido, extrair de PDF sem texto, arquivo inválido, alternar tema claro/escuro, fechar o app. Tudo ok.

## 2. Bugs reais encontrados pelos testes (já corrigidos)

| # | Problema | Correção |
|---|---|---|
| B1 | PDFsharp lança `InvalidOperationException` para arquivo que não é PDF e `System.Exception` puro para PDF truncado: o usuário veria mensagem técnica em inglês (ou o erro escaparia). | `OpenPdf` converte **qualquer falha de parse** em `InvalidDataException` com mensagem em português; só erro de E/S (arquivo travado, sem permissão) e cancelamento passam como são. Mesma regra na extração de texto (PdfPig). |
| B2 | `MergeAsync`/`CompressAsync` lançavam `ArgumentException` de forma **síncrona** (antes de devolver a `Task`), um caminho de erro diferente dos demais. | Validação movida para dentro da tarefa: todo erro chega pelo `await`. |
| B3 | `PdfDocumentOpenMode.InformationOnly` é obsoleto no PDFsharp 6 (apelido de `Import`; o compilador avisou). | Trocado por `Import`. |

## 3. Assincronia

- Toda operação roda em `Task.Run` e aceita `CancellationToken`; o cancelamento é verificado **a cada página / parte / imagem**.
- Progresso por `IProgress<PdfProgress>` criado na thread da interface: os relatórios chegam já nela, sem `Dispatcher` manual. Relatório atrasado depois do fim da operação é descartado (`if (!_running) return`).
- Handlers são `async void` (padrão de evento); por isso `RunAsync` captura **todas** as exceções (E/S, argumento, memória e o resto) e as mostra na linha de status. Nada escapa e derruba o app.
- Botões de ação ficam desativados durante a operação (sem execução dupla); CANCELAR aparece.
- A abertura do app **não** paga nada: as DLLs de PDF só são carregadas quando uma operação roda (`Activate()` não faz nada).

## 4. Memória e recursos

- Todo `PdfDocument`, `FileStream` e `PdfPigDocument` está em `using`; origem fechada assim que termina (testado).
- Saída gravada em `.tmp` + `File.Move(overwrite)`: nunca fica PDF pela metade; falha ou cancelamento apagam o `.tmp`.
- Na compressão o resultado é montado em `MemoryStream` e só vai ao disco se for **menor** que o original.
- **Limite honesto:** PDFsharp carrega o documento inteiro em memória. PDFs de centenas de MB podem esgotar a memória: o `OutOfMemoryException` é capturado e vira mensagem ("memória insuficiente"), mas **não há limite prévio de tamanho**. Item aberto (S-P1).
- Extração de texto: o texto completo fica em memória (`_fullText`) para salvar/copiar; a caixa mostra só os primeiros 200 000 caracteres.

## 5. Segurança e integridade dos dados

- **O original nunca é alterado**: a saída não pode ser um arquivo de entrada (comparação por caminho completo, sem diferenciar maiúsculas). Ao dividir, nomes existentes nunca são sobrescritos.
- PDF com senha é **recusado** com mensagem clara (não há suporte a senha nesta versão). Não tenta quebrar nada.
- Nada é enviado para fora: processamento 100% local.
- Compressão: só recomprime JPEG (`DCTDecode`) de 1 ou 3 componentes com espaço de cor compatível; CMYK, imagens com `/Mask`, e qualquer imagem que fique maior ficam como estão.

## 6. Limitações conhecidas (não são bugs, estão descritas na tela quando relevante)

| # | Limitação |
|---|---|
| L1 | **Comprimir só reduz imagens JPEG.** PDFs só de texto/vetores, ou com imagens em Flate/JPEG2000, geralmente não ganham nada: a tela diz isso e **não cria arquivo**. PDFsharp não otimiza fontes nem objetos. |
| L2 | **Extrair texto não faz OCR.** PDF escaneado (só imagem) devolve vazio; a tela avisa quantas páginas ficaram sem texto. |
| L3 | **PDF com senha não é suportado.** |
| L4 | Mesclar/dividir **perdem** marcadores (bookmarks), formulários interativos e metadados do original (o PDFsharp copia só as páginas). |
| L5 | Não foi testado com PDFs grandes (> 100 MB), PDF/A, assinados digitalmente (a assinatura é invalidada ao regravar) nem com fontes CJK. |
| L6 | Mostrar na pasta (`explorer /select`) e as caixas de diálogo de salvar/abrir não foram exercitadas nos testes automatizados (exigem interação). Mesclar pela tela depende do diálogo "Salvar"; a operação em si é coberta pelos testes de serviço. |

## 7. Pendências sugeridas

1. **S-P1:** avisar antes de abrir PDF muito grande (ex.: > 150 MB).
2. **L4:** preservar marcadores na mesclagem (PDFsharp permite, exige trabalho com `Outlines`).
3. Teste manual com seus PDFs reais (escaneado, com fotos, com formulário) antes de validar a Etapa 2.
