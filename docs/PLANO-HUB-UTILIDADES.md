# Fort Knox · Plano técnico: Hub de Utilidades

Novos módulos: **Vault de Senhas**, **Manipulador de PDF** e **Rede avançada**.
Base: C# 12 / .NET 8 / WPF, `MainWindow` em partial classes, execução como Administrador, DPAPI já usado em `AppSettings`.

## 1. O que já existe e muda o plano

| Achado no código | Consequência |
|---|---|
| `NetworkTest.cs` **já mede download e upload** (4 e 3 conexões para `speed.cloudflare.com`), ping, jitter, perda, DNS e bufferbloat. | A Etapa 3 **não recria** o teste de banda: refina o existente (ver §4). |
| `Microsoft.Diagnostics.Tracing.TraceEvent` já é dependência (usada em `FrameMonitor.cs`). | O consumo de banda por processo usa ETW **sem pacote novo**. O app já roda como admin, que é o que o ETW de kernel exige. |
| `AppSettings.HowManyFpsKey` já usa `ProtectedData` + `CurrentUser`. | O Vault segue o mesmo modelo, mas em arquivo próprio (não dentro do `settings.json`). |
| Projeto plano na raiz, namespace único `FortKnox`, páginas = elementos de `MainWindow.xaml` trocados por `ShowPage`. | Os módulos novos ficam em `Modules/<Nome>/`, no **mesmo namespace** (o SDK inclui subpastas sozinho; nada muda no `.csproj` para isso). Só o glue (`MainWindow.<Nome>.cs`) fica na raiz, como os outros. |
| `LICENSE` do repositório é **MIT**. | iText7 é AGPL: **não usar**. Ver §3. |

## 2. Estrutura de pastas

```
Fort-Knox/
├─ MainWindow.Vault.cs          ← glue (Init/Lock/Shutdown)           [Etapa 1 ✔ feita]
├─ MainWindow.Pdf.cs            ← glue                                [Etapa 2]
├─ MainWindow.Net.cs            ← já existe; ganha as novas ações     [Etapa 3]
└─ Modules/
   ├─ Vault/
   │   ├─ PasswordVault.cs      ← CRUD + DPAPI + backup + gerador     [✔]
   │   ├─ SecureClipboard.cs    ← copiar e limpar em 30 s             [✔]
   │   ├─ VaultView.xaml(.cs)   ← UserControl da página               [✔]
   ├─ Pdf/
   │   ├─ PdfService.cs         ← mesclar / dividir / comprimir / extrair texto (sem WPF)
   │   ├─ PdfJob.cs             ← registro de progresso + CancellationToken
   │   └─ PdfView.xaml(.cs)
   └─ Network/
       ├─ NetworkTest.cs        ← movido da raiz (conteúdo preservado)
       ├─ BandwidthTest.cs      ← download/upload extraídos de NetworkTest
       ├─ TraceRoute.cs         ← Ping com TTL crescente
       ├─ ProcessBandwidthMonitor.cs ← ETW Kernel-Network
       └─ NetView.xaml(.cs)     ← só se a página Rede crescer demais para MainWindow.xaml
```

Regras de arquitetura (valem para os três módulos):

1. **Camada de dados/serviço sem WPF.** `PasswordVault`, `PdfService`, `TraceRoute` não referenciam `System.Windows`. A tela só chama e mostra.
2. **Nada pesado na abertura.** Módulos carregam ao abrir a página (`Nav_Checked` → `InitX()`), como `InitApps()`. Custo no startup: zero, o que preserva a abertura em ~0,5 s. As DLLs de PDF só são carregadas pelo JIT quando `PdfService` é usado.
3. **Async de ponta a ponta.** E/S e CPU pesada em `Task.Run`; a continuação do `await` volta sozinha à thread da UI (não precisa de `Dispatcher.Invoke`, exceto em callbacks de progresso vindos de thread de fundo, como já faz `MainWindow.Net.cs`).
4. **Cancelável e idempotente.** Toda operação longa recebe `CancellationToken`; o botão vira "CANCELAR" (padrão de `BtnNetTest_Click`).
5. **Falha visível, nunca silenciosa.** Exceções esperadas (`IOException`, `InvalidDataException`…) viram texto na linha de status; nada de `catch {}` vazio em operação que o usuário disparou.

## 3. Módulo Vault de Senhas (Etapa 1)

**Armazenamento.** `%AppData%\FortKnox\vault.dat` = `"FKV1"` + `ProtectedData.Protect(JSON, entropia do app, CurrentUser)`. O **arquivo inteiro** é cifrado (nome, usuário e URL também), ao contrário de cifrar só o campo senha. Escrita atômica (`.tmp` + `File.Replace`, o anterior vira `.bak`).

**Memória.** Os registros ficam em memória só enquanto a página está aberta. `Lock()` (ao sair da página, no botão BLOQUEAR e ao fechar o app) descarta tudo. A grade nunca recebe a senha: usa `VaultItem` com máscara `••••••••`.

**Interface.** DataGrid (nome, usuário, categoria, senha mascarada, data) + busca instantânea em memória + botões `USUÁRIO` / `SENHA` por linha. A cópia usa `SecureClipboard`: limpa após 30 s **só se o conteúdo ainda for o que copiamos** e pede ao Windows para não registrar no histórico (Win+V) nem na nuvem.

**Se o DPAPI falhar** (senha do Windows redefinida por outro admin, perfil recriado, arquivo em outro PC): o cofre entra em `Unreadable`, **nada é sobrescrito**, e a tela oferece restaurar de um backup `.fkvault` (AES-256-GCM, chave PBKDF2-SHA256 600 000 iterações, senha escolhida pelo usuário). Detalhes no relatório de revisão.

## 4. Módulo Rede avançada (Etapa 3)

**Já existe:** latência/jitter/perda (roteador, 1.1.1.1, 8.8.8.8), DNS, download, upload, latência sob carga, veredito.

| Novo | Como | Observações |
|---|---|---|
| **Largura de banda refinada** | Extrair `Download`/`Upload` para `BandwidthTest`; mostrar **velocidade ao vivo** (amostra a cada 250 ms) e **pico vs. média** em vez de só a média; escolher duração (5/10/20 s). | Hoje a média inclui a fase de aceleração do TCP e subestima o link. Reutilizar um único `HttpClient` estático (já é). |
| **Traceroute visual** | `Ping.SendPingAsync` com `PingOptions { Ttl = 1..30 }`; `TtlExpired` revela cada salto. 3 sondas por salto, em paralelo entre saltos (não esperar salto a salto). DNS reverso opcional, com timeout de 1,5 s. | Lista com barra de latência por salto e destaque do salto onde a latência salta/perde pacote. Sem geolocalização online (evita enviar IPs do usuário a terceiros). |
| **Consumo por processo** | `TraceEventSession` com `KernelTraceEventParser.Keywords.NetworkTCPIP` (ou provider `Microsoft-Windows-Kernel-Network`); eventos `TcpIpSend/Recv`, `UdpIpSend/Recv` somados por `ProcessID`, agregados 1×/s. | Mapear PID→nome com cache. Nome de sessão fixo e **parar sessão órfã** na abertura (padrão do `FrameMonitor`). Só roda com a página aberta. Mostra bytes/s, não "Mbps de link". |

## 5. Módulo Manipulador de PDF (Etapa 2)

**Bibliotecas (adicionar ao `.csproj`; confirmar a última versão estável no NuGet na hora):**

```xml
<PackageReference Include="PDFsharp" Version="6.*" />          <!-- MIT: mesclar, dividir, comprimir (reescrita) -->
<PackageReference Include="PdfPig" Version="0.1.*" />          <!-- Apache-2.0: extrair texto -->
```

Por que não iText7: licença AGPL. Este repositório é MIT; usar iText obrigaria a abrir o código sob AGPL ou comprar licença comercial. PDFsharp (MIT) e PdfPig (Apache-2.0) são compatíveis com MIT. **Fixar versões exatas** (sem `*`) antes de publicar, para builds reprodutíveis.

| Função | Implementação | Limite honesto |
|---|---|---|
| Mesclar | `PdfReader.Open(..., PdfDocumentOpenMode.Import)` → `PdfDocument.AddPage` de cada arquivo | PDFs com senha: pedir senha ou recusar com mensagem clara |
| Dividir | Por intervalo ("1-3,5") ou 1 arquivo por página | idem |
| Comprimir | Reescrita com compressão máxima de streams + remoção de objetos órfãos; opcional: reamostrar imagens (JPEG qualidade 60–80) | **PDFsharp não recomprime imagens sozinho**: o ganho grande vem de reamostrar imagens via `System.Windows.Media.Imaging` (JpegBitmapEncoder). Vira sub-etapa própria. Mostrar tamanho antes/depois e **nunca sobrescrever o original** |
| Extrair texto | PdfPig `PdfDocument.Open` → `page.Text` / `ContentOrderTextExtractor` | PDF escaneado (imagem) não tem texto: avisar que precisa de OCR (fora do escopo) |

Todas rodam em `Task.Run`, com `using` em `PdfDocument`/`FileStream`, progresso por página e cancelamento entre páginas. Arquivos abertos com `FileShare.Read`.

## 6. Alterações no XAML principal (padrão para cada módulo)

1. **Menu** em `MainWindow.xaml`, dentro de `GrpToolsItems` (RESET FERRAMENTAS):
   ```xml
   <RadioButton x:Name="NavPdf" Style="{StaticResource NavItem}" Tag="PagePdf" Checked="Nav_Checked">
       <StackPanel Orientation="Horizontal">
           <TextBlock Text="&#xE8A5;" FontFamily="{StaticResource Icons}" FontSize="14" Width="28" VerticalAlignment="Center"/>
           <TextBlock Text="PDF" VerticalAlignment="Center"/>
       </StackPanel>
   </RadioButton>
   ```
2. **Página** (irmã das outras, dentro do `Grid` de conteúdo): `<local:PdfView x:Name="PagePdf" Visibility="Collapsed"/>`
3. **`MainWindow.xaml.cs`**: acrescentar `PagePdf` ao array de `ShowPage` e um `case "PagePdf": InitPdf(); break;` em `Nav_Checked`.
4. **Fechamento**: `Closed += …` chama `ShutdownPdf()` (cancela jobs). Para o Vault já está ligado.
5. A página **Rede** já existe (`PageNet`): ela só ganha cartões/abas novos.

## 7. Roadmap em 3 etapas

| Etapa | Entrega | Pronto quando |
|---|---|---|
| **1 · Vault** ✔ código escrito | `PasswordVault`, `SecureClipboard`, `VaultView`, `MainWindow.Vault.cs`, backup/restauração | Compila; CRUD persiste após reabrir; copiar limpa em 30 s; arquivo corrompido/ilegível não é sobrescrito |
| **2 · PDF** | `PdfService` (mesclar, dividir, extrair texto, comprimir básico) + `PdfView`; pacotes no `.csproj` | Mescla 2 PDFs e divide por intervalo sem travar a UI; cancela no meio; sem arquivo travado depois |
| **3 · Rede** | `BandwidthTest` ao vivo, `TraceRoute`, `ProcessBandwidthMonitor`; `NetworkTest` movido para `Modules/Network` | Traceroute chega ao destino; tabela por processo bate com o Gerenciador de Tarefas (aba Rede); sessão ETW some ao fechar |

Cada etapa termina com **revisão de assincronia, segurança e memória** antes da próxima (relatório da Etapa 1 em `docs/REVISAO-VAULT.md`).

## 8. Riscos

- **Elevação com outro usuário:** se o UAC pedir credenciais de *outro* administrador, `CurrentUser` e `%AppData%` passam a ser os dele: o Vault "some". Documentar na tela do Vault.
- **ETW por processo** perde eventos sob carga muito alta; é estatística, não contabilidade.
- **Dependência de `speed.cloudflare.com`** (já existente): oferecer falha clara quando bloqueado.
