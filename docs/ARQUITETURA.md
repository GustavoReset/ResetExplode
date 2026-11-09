# Fort Knox · Arquitetura do hub all-in-one (Fase 1)

Status: **proposta para aprovação**. Nada neste documento foi implementado ainda.
Base: código da `main` em `5f66fc5` (v0.16, identidade v3), `docs/PLANO-HUB-UTILIDADES.md`,
`IOBIT_REVERSE_ENGINEERING_REPORT.md` (análise estática) e o Brandbook v3 (Manifesto, Tom de voz, Naming, tokens).

## 0. Resumo em 10 linhas

1. O Fort Knox continua **C# 12 / .NET 8 / WPF**, um único `.exe` portátil. Nada de reescrever do zero.
2. O monólito atual (`MainWindow` com 15 partial classes e um `MainWindow.xaml` de ~3.000 linhas) vira um **Shell** que hospeda **módulos** com um contrato comum (`IModule`).
3. Cada módulo tem **serviço sem WPF** (testável) + **View/ViewModel**. Módulos não se conhecem: falam por um **barramento de eventos** e por **serviços do núcleo**.
4. O **estado global** fica num **Store** central (fatias imutáveis por módulo), e o que persiste vai para `settings.json` (preferências), SQLite (histórico) e arquivos cifrados (cofre).
5. Toda mudança no sistema passa por um **Diário de Ferrolhos** com desfazer, generalizando o que hoje está espalhado (`TweakBackup`, `StartupRemoved`, `ComDbBackup`, backups de driver).
6. A nota **Lastro** (0 a 100) é a soma ponderada de **indicadores de saúde** que cada módulo publica.
7. Novo processo opcional **Fort Knox Guarda** (serviço do Windows) para o que precisa rodar com a janela fechada: proteção em tempo real da MURALHA, agendamentos e alertas. Conversa com o app por **pipe nomeado** com lista fechada de comandos.
8. Sem driver de kernel na v1: tempo real via **ETW** e APIs do Windows. Sem tarefa "SkipUAC", sem propaganda, sem telemetria. **Offline por padrão.**
9. O relatório IObit serve só como **mapa funcional** (o que um hub desses tem). Clean room: nenhum código, banco, lista, ícone, texto ou endpoint deles.
10. Quatro decisões ficam para você aprovar (seção 12). O resto segue o padrão do código que já existe.

---

## 1. Ponto de partida (o que o código já faz bem e o que trava o crescimento)

| Hoje | Avaliação |
|---|---|
| `MainWindow` em partial classes (`MainWindow.Dash.cs`, `.Net.cs`, `.Vault.cs`…), páginas trocadas por `ShowPage` com um array fixo de 23 páginas. | Funciona, mas cada módulo novo mexe em 4 lugares (`MainWindow.xaml`, array de `ShowPage`, `switch` de `Nav_Checked`, `Closed`). |
| `Modules/Vault`, `Modules/Pdf`, `Modules/Network` já separam **serviço sem WPF** + `UserControl`. | **É o padrão certo.** A arquitetura proposta generaliza isso para todos os módulos. |
| Um `DispatcherTimer` de 1 s na `MainWindow` atualiza sensores, stress, GPU, painel, gargalo, Forja e RAM. | Acoplamento central: cada tela nova entra no mesmo `Tick`. Vira publicação de `SensorSnapshot` no barramento. |
| `AppSettings` único com campos de todos os módulos (Forja, Arena, Overlay, ajustes…). | Cresce sem controle e mistura preferências com histórico. Vira seções por módulo + SQLite para histórico. |
| App inteiro roda como **administrador** (`requireAdministrator`). | Necessário para sensores, mas faz o cofre depender de "qual usuário elevou" (risco já documentado). Ver decisão D1. |
| Testes (`tests/FortKnox.Tests`) compilam só as camadas de serviço por `Compile Include`. | Bom. Com projetos separados (decisão D2) isso vira `ProjectReference` normal. |
| CI publica `FortKnox.exe` single-file a cada push na `main`. | Mantém. Ganha um passo de testes antes de publicar. |

## 2. Princípios (vêm do Manifesto e do que já foi decidido no código)

1. **Offline por padrão.** Toda função útil roda sem internet. Rede é opt-in e explicada (download de driver, teste de velocidade, atualização do app).
2. **Tudo tem volta.** Nenhuma alteração no sistema sem registro no Diário de Ferrolhos e botão **Restaurar**.
3. **Segurança não se negocia.** Nunca desligar Defender, UAC, VBS ou Windows Update. Nenhum atalho para pular o UAC.
4. **Nada escondido.** Sem instalação de terceiros embutida, sem promoções, sem telemetria. Relatório de erro só gerado localmente, enviado pela pessoa se ela quiser.
5. **Serviço sem tela.** Regra de ouro dos módulos: a lógica não referencia `System.Windows`.
6. **Nada pesado na abertura.** Módulo só carrega quando a página abre (mantém a janela em ~0,5 s).
7. **Async, cancelável, falha visível.** Mantidas as regras 3 a 5 do `PLANO-HUB-UTILIDADES.md`.
8. **Um lugar, um padrão.** Mesmo layout, mesma voz (Tom de voz v3), mesmas três palavras de status: **Bom**, **Atenção**, **Ruim**.

## 3. Visão em camadas

```
┌──────────────────────────────────────────────────────────────────────────┐
│ FortKnox.exe  (WPF, processo da pessoa)                                    │
│                                                                          │
│  Shell ── barra de título, menu lateral, rodapé ao vivo, toasts, tema    │
│    │      Navegador (rota → View), Registro de módulos                   │
│    ▼                                                                     │
│  Módulos  PAINEL · SENTINELA · FORJA · ARENA · VIGIA · ARSENAL ·         │
│           ALICERCE · OFICINA · PRISMA · RADAR · PRENSA · COFRE · MURALHA │
│           (cada um: Views/ViewModels  +  Serviço sem WPF)                │
│    │                                                                     │
│    ▼                                                                     │
│  Núcleo (FortKnox.Core, sem WPF)                                         │
│    Barramento de eventos · Store de estado · Jobs · Diário de Ferrolhos  │
│    Configurações · Segredos (DPAPI) · Histórico (SQLite) · Log           │
│    Hardware (sensores/WMI/GPU) · Privilégio · Cliente do Guarda          │
└───────────────┬──────────────────────────────────────────────────────────┘
                │ pipe nomeado \\.\pipe\FortKnox.Guarda (ACL + comandos fechados)
┌───────────────▼──────────────────────────────────────────────────────────┐
│ FortKnoxGuarda.exe  (serviço do Windows, opcional, LocalSystem)            │
│   Agendador · Tempo real da MURALHA (ETW) · Quarentena · Alertas         │
│   Executor de ações privilegiadas (se D1 = app sem admin)                │
└──────────────────────────────────────────────────────────────────────────┘
```

**Por que um serviço separado:** o relatório IObit mostra o padrão comum dos hubs: UI + serviço do sistema + bandeja + vários `.exe` auxiliares e tarefas agendadas. A ideia útil é separar **o que precisa rodar com a janela fechada** (tempo real, agendamentos). O que **não** adotamos: dezenas de executáveis, tarefas que pulam o UAC, filtros de kernel, atualizador que instala outros produtos. O Fort Knox tem **dois** executáveis no máximo, e o segundo é opcional.

## 4. Organização da solução

Proposta (decisão D2), migrando aos poucos sem quebrar o build:

```
Fort-Knox/
├─ FortKnox.sln
├─ src/
│  ├─ FortKnox.Core/            ← net8.0-windows, SEM WPF. Contratos, barramento, store, jobs, ferrolhos, config, log, IPC
│  ├─ FortKnox.Hardware/        ← SensorMonitor, SystemInfoCollector, GpuInfoCollector, Nvml, Dxgi, DiskHealth… (sem WPF)
│  ├─ FortKnox.UI/              ← Theme.xaml, Brand.xaml, ThemeManager, controles comuns (cartão, anel, gráfico, status)
│  ├─ FortKnox.App/             ← o .exe: Shell, App.xaml, app.manifest, fontes, ícones; referencia todos os módulos
│  ├─ FortKnox.Guarda/          ← o serviço (Worker Service .NET 8), sem WPF
│  └─ Modules/
│     ├─ FortKnox.Modules.Sentinela/   ← Services/ (sem WPF) + Views/ + ViewModels/ + SentinelaModule.cs
│     ├─ FortKnox.Modules.Forja/
│     ├─ …                             (um projeto por módulo; PRENSA, COFRE e RADAR já quase prontos)
│     └─ FortKnox.Modules.Muralha/
├─ tests/
│  ├─ FortKnox.Core.Tests/
│  └─ FortKnox.Modules.<Nome>.Tests/   ← testam só a pasta Services/
└─ docs/
```

Regras de dependência (checadas por teste de arquitetura):

- `Core` não depende de nada do projeto. `Hardware` depende só de `Core`.
- Um módulo depende de `Core`, `Hardware` e `UI`. **Nunca de outro módulo.**
- `App` é o único que conhece todos os módulos (composição).
- `Guarda` depende de `Core` e dos **serviços** (não das Views) dos módulos que ele executa (ex.: `Muralha/Services`).
- O publish continua gerando **um** `FortKnox.exe` single-file (os projetos viram DLLs embutidas). O `FortKnoxGuarda.exe` sai como segundo arquivo na mesma pasta.

Se você preferir não ter vários projetos agora, a alternativa é manter um projeto e só criar pastas `Core/`, `Modules/<Nome>/`, com as mesmas regras verificadas por teste. Perde-se a garantia do compilador, ganha-se simplicidade.

## 5. Módulos

### 5.1 Contrato

```csharp
public interface IModule
{
    string Id { get; }                       // "sentinela", "muralha"… (estável, usado em config e eventos)
    string DisplayName { get; }              // "SENTINELA" (versalete, Naming v3)
    IReadOnlyList<NavEntry> Pages { get; }   // entradas do menu: rota, título, ícone, ordem
    ModuleCapabilities Capabilities { get; } // precisa de admin? de rede? roda no Guarda?

    Task InitializeAsync(IModuleContext ctx, CancellationToken ct); // leve; roda ao abrir a 1ª página
    Task OnPageActivatedAsync(string route, CancellationToken ct);
    Task OnPageDeactivatedAsync(string route);            // ex.: COFRE bloqueia, RADAR para o ETW
    IEnumerable<IHealthIndicator> HealthIndicators { get; } // alimenta o Lastro
    ValueTask ShutdownAsync();                            // cancela jobs, libera recursos
}

public sealed record NavEntry(string Route, string Title, string Glyph, int Order, string? Group = null);
```

- `IModuleContext` dá acesso a: `IEventBus`, `IStateStore`, `IJobRunner`, `IChangeJournal`, `ISettings<T>`, `ISecretStore`, `IHistoryDb`, `ILog`, `IPrivilegedOps`, `INotifier`, `IGuardaClient`.
- Injeção de dependência com `Microsoft.Extensions.DependencyInjection` (Microsoft, MIT; ver seção 11).
- O **Navegador** do Shell monta o menu a partir de `Pages` de todos os módulos e troca a View pela rota (`"sentinela/sensores"`), acabando com o array do `ShowPage` e o `switch` do `Nav_Checked`.
- **Ciclo de vida:** `Register` na abertura (barato) → `InitializeAsync` na primeira visita → `OnPageActivated/Deactivated` a cada troca → `ShutdownAsync` ao fechar.

### 5.2 Mapa de módulos

Nomes do Naming v3. "Origem" = código atual que migra para o módulo.

| Módulo | Páginas | Origem no código | Precisa de admin | Usa rede | No Guarda |
|---|---|---|---|---|---|
| **PAINEL** | Visão geral, Lastro, eventos recentes | `MainWindow.Dash.cs` | não | não | não |
| **SENTINELA** | Sistema, Sensores, Placa de vídeo, Discos, Energia | `SystemInfoCollector`, `SensorMonitor`, `GpuInfoCollector`, `DiskHealth`, `BatteryInfo`, `DriveMap`, `ReportBuilder` | sim (sensores) | não | alertas de temperatura/disco |
| **FORJA** | Benchmark, Stress test, Overclock | `BenchRenderer`, `Gpu*Bench`, `GameSimBench`, `MemoryBench`, `DiskBench`, `StressTester`, `GpuBurn`, `OcCheck`, `MainWindow.Oc.cs` | sim (OC, ETW) | não | não |
| **ARENA** | Biblioteca, FPS estimado, Gargalo | `GameLibrary`, `GameLauncher`, `Vdf`, `GameCatalog`, `HowManyFpsClient`, `BottleneckAnalyzer` | não* | opcional (HowManyFPS) | não |
| **VIGIA** | Overlay | `OverlayWindow`, `OverlayMetrics`, `FrameMonitor` | sim (ETW) | não | não |
| **ARSENAL** | Otimização, Inicialização, Instalar apps, **Limpeza**, **Desinstalar**, **Atualizar apps** | `Optimizer`, `WindowsTweaks`, `StartupManager`, `AppInstaller` | sim | winget | não |
| **ALICERCE** | Drivers | `DriverManager` | sim | opcional (fabricante) | verificação agendada |
| **OFICINA** | Portas COM, Drivers seriais | `UsbCleaner`, `MakerDrivers` | sim | não | não |
| **PRISMA** | Iluminação | `LightBackends`, `CorsairSdk*` | não | localhost (OpenRGB/Chroma) | não |
| **RADAR** | Conexão, Traceroute, Consumo por processo | `Modules/Network/*` | sim (ETW) | sim (teste) | não |
| **PRENSA** | PDF | `Modules/Pdf/*` | não | não | não |
| **COFRE** | Senhas | `Modules/Vault/*` | não | não | não |
| **MURALHA** | Varredura, Quarentena, Tempo real, Navegação | novo | sim | não (assinaturas offline) | **sim** (tempo real) |

\* A ARENA grava perfil no registro do usuário e no `localconfig.vdf`; plano de energia e prioridade alta pedem admin.

**Páginas novas propostas** (lacunas que o relatório IObit mostra e o Fort Knox ainda não tem), todas dentro de módulos existentes para não inventar marcas:

- **ARSENAL · Limpeza:** temporários do Windows e dos navegadores, cache de atualização, lixeira, arquivos grandes e duplicados. Sempre com prévia e tamanho antes de apagar; manda para a Lixeira quando possível.
- **ARSENAL · Desinstalar:** lista de programas (registro Uninstall + Appx), desinstalação pelo desinstalador oficial e depois **varredura de restos** (pastas e chaves com o nome do fabricante/produto), com cada resto mostrado e confirmado. Restos vão para o Diário de Ferrolhos.
- **ARSENAL · Atualizar apps:** `winget upgrade` com lista, escolha e resultado por app (reaproveita o `AppInstaller`).
- **MURALHA · Navegação:** limpeza de rastros e checagem de extensões instaladas nos navegadores (só leitura e remoção com confirmação). Proteção de navegação "ao vivo" via extensão fica fora da v1 (ver 9.4).

### 5.3 Estrutura interna de um módulo

```
FortKnox.Modules.Arsenal/
├─ ArsenalModule.cs          ← implementa IModule (registro, páginas, indicadores de saúde)
├─ Services/                 ← sem WPF: StartupService, CleanupService, UninstallService…
├─ ViewModels/               ← INotifyPropertyChanged, comandos, sem lógica de sistema
├─ Views/                    ← UserControls XAML, só DynamicResource do tema
└─ Events.cs                 ← records de eventos que o módulo publica
```

Padrão de UI: **MVVM leve**. ViewModel chama o serviço, o serviço devolve dados ou publica eventos. O XAML não tem lógica além de bindings e estilos do tema. Para o código-atrás que já existe (`VaultView.xaml.cs`, `PdfView.xaml.cs`), a migração é gradual: não é preciso reescrever o que funciona.

## 6. Comunicação

### 6.1 Dentro do app: barramento de eventos

```csharp
public interface IEventBus
{
    void Publish<T>(T evt) where T : IFortKnoxEvent;
    IDisposable Subscribe<T>(Action<T> handler, Delivery delivery = Delivery.UiThread) where T : IFortKnoxEvent;
}
public enum Delivery { UiThread, Background }
```

- Eventos são `record`s imutáveis, nome no passado: `SensorSnapshotPublished`, `ScanCompleted`, `ThreatQuarantined`, `ChangeApplied`, `DeviceArrived`, `GameStarted`, `GameExited`.
- Assinatura devolve `IDisposable`: o ViewModel cancela ao sair da página (sem vazamento de memória por evento pendurado).
- `Delivery.UiThread` entrega via `Dispatcher` (acaba com `Dispatcher.Invoke` espalhado). `Background` entrega no pool.
- Um handler que lança exceção é registrado no log e **não** derruba os outros.
- **Sem request/response pelo barramento.** Para pedir algo a outro módulo, usa-se um serviço do núcleo (ex.: `IHardwareInfo`) ou um evento + estado. Módulo nunca chama módulo.

Eventos centrais (catálogo inicial):

| Evento | Quem publica | Quem consome |
|---|---|---|
| `SensorSnapshotPublished` (1×/s) | Núcleo/Hardware | PAINEL, SENTINELA, FORJA, ARENA (gargalo), VIGIA, rodapé do Shell |
| `HardwareDetected` | Núcleo/Hardware | todos que dependem do nome da CPU/GPU (ARENA, FORJA OC) |
| `DeviceArrived` / `DeviceRemoved` | Shell (`WM_DEVICECHANGE`) | SENTINELA (unidades), OFICINA (portas COM), MURALHA (varrer pendrive, se ativado) |
| `ChangeApplied` / `ChangeReverted` | Diário de Ferrolhos | PAINEL (eventos recentes), ARSENAL |
| `HealthChanged` | qualquer módulo | PAINEL (Lastro) |
| `GameStarted` / `GameExited` | ARENA | VIGIA, PRISMA, SENTINELA (pausar leituras pesadas, "Modo Bunker") |
| `BenchmarkRunning` (on/off) | FORJA | Núcleo/Hardware (pausa sensores durante a cena de GPU, como hoje `_gpuBenchActive`) |
| `ThreatDetected` / `ThreatQuarantined` | MURALHA (via Guarda) | PAINEL, Shell (toast) |
| `ThemeChanged` | Shell | VIGIA, toasts |

### 6.2 Trabalhos longos: `IJobRunner`

Generaliza o padrão "botão vira CANCELAR" que já existe:

```csharp
public interface IJobRunner
{
    JobHandle Start(string title, Func<IProgress<JobProgress>, CancellationToken, Task> work, JobOptions? options = null);
    IReadOnlyList<JobHandle> Running { get; }
}
public sealed record JobProgress(double? Fraction, string Status);
```

- Cada job tem dono (módulo), título, progresso, cancelamento e resultado (ok/erro/cancelado).
- `JobOptions.Exclusive` impede dois jobs pesados concorrentes (ex.: benchmark + varredura completa).
- O Shell mostra jobs em andamento num indicador discreto e cancela tudo no `ShutdownAsync`.

### 6.3 Entre processos: app ↔ Guarda

- Transporte: **pipe nomeado** `\\.\pipe\FortKnox.Guarda`, ACL só para `SYSTEM` e o usuário interativo (Administradores para comandos privilegiados).
- Protocolo próprio e pequeno: mensagens JSON (`System.Text.Json`) com prefixo de tamanho, `{ id, cmd, args }` → `{ id, ok, result | error }`, mais notificações empurradas pelo serviço (`{ evt, data }`).
- **Lista fechada de comandos**, cada um com validação de argumentos no serviço: `muralha.scan.start`, `muralha.quarantine.list`, `muralha.quarantine.restore`, `muralha.realtime.set`, `schedule.list`, `schedule.set`, `status`. Nada de "executar comando arbitrário".
- O cliente (`IGuardaClient`, no Core) reconecta sozinho e degrada com mensagem clara quando o serviço não está instalado: "A proteção em tempo real está desligada. Ative em MURALHA."
- Versão do protocolo no handshake; app e serviço de versões diferentes recusam com mensagem clara (evita comportamento estranho depois de atualizar só um).

## 7. Estado global

### 7.1 Três tipos de estado

| Tipo | Onde vive | Exemplo | Persistência |
|---|---|---|---|
| **Sessão** (efêmero) | `IStateStore` em memória | sensores atuais, jogo em execução, jobs, Lastro calculado | não |
| **Preferências** | `settings.json` por seção | tema, cantos do overlay, perfis da ARENA | sim, `%AppData%\FortKnox` |
| **Histórico / dados** | SQLite `history.db` | resultados de benchmark, eventos, Diário de Ferrolhos, varreduras | sim |
| **Segredos** | arquivos cifrados | cofre (`vault.dat`), chave HowManyFPS | sim, DPAPI CurrentUser (como hoje) |
| **Dados da máquina** (Guarda) | `%ProgramData%\FortKnox` | quarentena, assinaturas, log do serviço, agendamentos | sim, ACL Administradores/SYSTEM |

### 7.2 Store de sessão

```csharp
public interface IStateStore
{
    T Get<T>() where T : class, IStateSlice;                       // fatia atual (record imutável)
    void Update<T>(Func<T, T> reducer) where T : class, IStateSlice;
    IDisposable Watch<T>(Action<T> onChange, Delivery delivery = Delivery.UiThread) where T : class, IStateSlice;
}
```

- Uma **fatia por assunto**, record imutável: `HardwareState` (CPU, GPU, RAM, discos detectados), `LiveSensorsState`, `SessionState` (jogo atual, benchmark rodando, elevado ou não), `HealthState` (Lastro e indicadores), `ProtectionState` (tempo real ligado, última varredura, itens em quarentena), `ThemeState`.
- Atualização só por `Update` (copiar e trocar), com trava curta: leitura sempre consistente, sem `lock` espalhado nas telas.
- O módulo é dono das suas fatias; outros só leem. Isso é verificado por convenção de nome (`Arsenal*State` só é atualizado dentro do Arsenal) e revisão.

### 7.3 Configurações

```csharp
public interface ISettings<T> where T : class, new()   // T = seção do módulo, ex.: OverlaySettings
{
    T Value { get; }
    void Update(Action<T> change);   // grava com escrita atômica (.tmp + File.Replace), como o cofre já faz
}
```

- `settings.json` passa a ter `{ "schema": 2, "modules": { "vigia": {...}, "arena": {...} } }`.
- **Migração automática** do formato atual (`AppSettings` plano) na primeira abertura, com cópia `settings.v1.json` guardada. Mesmo espírito do `LegacyMigration`.
- Histórico que hoje está no JSON (`BenchRuns`, `BenchRecords`) migra para o SQLite.

### 7.4 Diário de Ferrolhos (mudanças reversíveis)

Hoje cada módulo guarda o próprio backup de um jeito. Proposta: um serviço único.

```csharp
public interface IChangeJournal
{
    Task<Ferrolho> ApplyAsync(ChangeRequest request, CancellationToken ct); // aplica E registra como desfazer
    Task RevertAsync(Guid ferrolhoId, CancellationToken ct);
    IReadOnlyList<Ferrolho> List(string? moduleId = null);
}
public sealed record Ferrolho(Guid Id, string ModuleId, string Title, DateTime AppliedAt, ChangeKind Kind, bool Reverted);
```

- Tipos de mudança com desfazer pronto: valor de registro, item de inicialização, serviço (tipo de início), plano de energia, arquivo movido para a Lixeira/quarentena, driver removido (com backup do pacote), ComDB.
- A tela "Restaurar" de cada módulo é só um filtro do Diário. O PAINEL mostra "3 ferrolhos aplicados".
- O registro vai para o SQLite **antes** de aplicar a mudança (se o app cair no meio, dá para desfazer).

### 7.5 Lastro (nota de saúde)

```csharp
public interface IHealthIndicator
{
    string Id { get; } string Title { get; }
    double Weight { get; }                  // peso na nota
    HealthReading Read();                   // Status (Bom/Atenção/Ruim), 0..100, motivo em linguagem clara, rota para resolver
}
```

- Exemplos: SENTINELA (temperaturas, S.M.A.R.T.), ALICERCE (drivers sem assinatura ou muito antigos), ARSENAL (inicialização pesada, disco cheio), MURALHA (tempo real desligado, varredura antiga, itens suspeitos), COFRE (senhas repetidas ou fracas, só contagem, nunca o conteúdo).
- O PAINEL mostra a nota, as três piores leituras e um botão que leva para a rota que resolve.
- Texto segue o Tom de voz: calmo, com número e unidade, sem alarmismo.

## 8. APIs e dependências externas

### 8.1 APIs do Windows (já em uso ou planejadas)

| Área | API | Onde |
|---|---|---|
| Hardware e sistema | WMI (`System.Management`), LibreHardwareMonitorLib, NVML, DXGI/D3D12 | SENTINELA, FORJA |
| Eventos de baixo nível | ETW via TraceEvent (Kernel-Network, DXGI Present, **Kernel-Process e Kernel-File**) | VIGIA, RADAR, **MURALHA tempo real** |
| Dispositivos | `WM_DEVICECHANGE`, SetupAPI | Shell, OFICINA |
| Segredos | DPAPI (`ProtectedData`), Windows Hello (`UserConsentVerifier`) | COFRE, Core |
| Confiança de arquivos | `WinVerifyTrust` (Authenticode), catálogo do Windows | ALICERCE, MURALHA, atualização do app |
| Antimalware | **AMSI** (`AmsiScanBuffer`) e `MpCmdRun.exe` do Defender, quando presente | MURALHA (segunda opinião) |
| Apps | registro `Uninstall`, `PackageManager` (Appx), **winget** | ARSENAL |
| Agendamento | Agendador de Tarefas (`schtasks`/COM) **ou** timers do Guarda | Core/Guarda |
| Serviço | `Microsoft.Extensions.Hosting.WindowsServices` | Guarda |
| Restauração | Ponto de restauração do sistema (`SRSetRestorePoint`) antes de ações grandes | ARSENAL, ALICERCE |

### 8.2 Internet (sempre opcional e explicada)

| Uso | Destino | Quando |
|---|---|---|
| Teste de conexão | `speed.cloudflare.com` (já usado) | só quando a pessoa clica |
| FPS medido | HowManyFPS API (já usado, chave da pessoa) | só com chave |
| Drivers | sites oficiais NVIDIA/AMD/fabricantes (já usado) | só quando a pessoa pede |
| Atualizar o app | GitHub Releases do próprio repositório | verificar ao abrir (desligável), baixar só com confirmação, conferir SHA-256 e assinatura |
| Assinaturas da MURALHA | GitHub Releases do próprio repositório (pacote assinado) | opcional; a MURALHA funciona com o pacote embutido |

Nenhum endpoint de terceiros encontrado no relatório IObit é usado. Não há envio de estatísticas de uso.

### 8.3 Pacotes de terceiros

Já no projeto (mantidos): LibreHardwareMonitorLib (MPL-2.0), TraceEvent (MIT), Vortice (MIT), PDFsharp (MIT), PdfPig (Apache-2.0), Microsoft.Data.Sqlite (MIT), System.Management / ProtectedData (MIT).

Novos **propostos, dependendo da sua aprovação** (seção 11): `Microsoft.Extensions.DependencyInjection`, `Microsoft.Extensions.Hosting.WindowsServices`, e opcionalmente `CommunityToolkit.Mvvm` e um motor de regras (YARA via `dnYara`/`libyara`). Nenhum é copiado para dentro do código: entram como pacote NuGet com licença registrada no README.

## 9. MURALHA (antivírus offline): arquitetura

É o módulo novo mais sensível, por isso tem seção própria. Escopo honesto: proteção **complementar** ao Microsoft Defender, nunca substituta, e nunca desligando o Defender.

### 9.1 Componentes

```
MURALHA
├─ Motor de varredura (Services, sem WPF; roda no app ou no Guarda)
│   ├─ Enumeração: rápida (pastas de risco, inicialização, Downloads), completa, personalizada, pendrive
│   ├─ Camadas de detecção, da mais barata à mais cara:
│   │   1. Lista de confiança: assinatura Authenticode válida de editor conhecido → pula
│   │   2. Hash SHA-256 contra base local de hashes maliciosos
│   │   3. Regras de conteúdo (formato tipo YARA) sobre PE, scripts e macros
│   │   4. Heurística de PE: empacotador, seções com entropia alta, imports suspeitos, sem assinatura em pasta de sistema
│   │   5. Segunda opinião: AMSI / Defender (MpCmdRun -Scan -ScanType 3 -File) quando disponível
│   └─ Resultado: Limpo · Suspeito · Malicioso, com o MOTIVO (qual camada, qual regra)
├─ Quarentena (%ProgramData%\FortKnox\Quarentena)
│   └─ arquivo cifrado (AES-256-GCM, chave protegida por DPAPI LocalMachine) + metadados (caminho original, hash, ACL)
├─ Tempo real (no Guarda)
│   └─ ETW Kernel-Process (processo criado) + Kernel-File (arquivo criado/renomeado em pastas vigiadas)
│      → fila → motor (camadas 1 a 4) → toast "Item suspeito bloqueado" + quarentena se a regra pedir
└─ Base de assinaturas
    └─ pacote próprio versionado (hashes + regras), embutido no app e atualizável por arquivo assinado
```

### 9.2 Limites assumidos

- **ETW observa, não bloqueia.** Sem driver de kernel (minifiltro), o tempo real detecta logo depois que o arquivo é criado ou o processo inicia, e reage (encerra o processo, põe em quarentena). Não impede a primeira execução. Isso será dito na tela, sem prometer o que não faz.
- Driver de minifiltro fica como **fase futura** e só com assinatura EV + atestação da Microsoft. Não está nesta arquitetura.
- A qualidade da detecção depende da base. Base própria começa pequena; por isso a segunda opinião do Defender e a camada de confiança por assinatura são parte do desenho desde o início.

### 9.3 Linguagem

Segue o Tom de voz: "item suspeito", nunca "vírus" sem diagnóstico; motivo claro; sempre "Restaurar da quarentena".

### 9.4 Fora do escopo da v1

Proteção de navegação ao vivo (exigiria extensão de navegador + host de Native Messaging, como o relatório IObit mostra) e filtro de rede. Ficam registrados como ideia para depois.

## 10. Segurança e privilégio

- **Elevação:** ver decisão D1. Qualquer que seja a escolha, operações privilegiadas passam por `IPrivilegedOps` (interface no Core). Na v1 ela executa no próprio processo elevado; depois pode passar a delegar ao Guarda sem mudar os módulos.
- **Sem pular o UAC.** O relatório IObit mostra tarefas "SkipUAC" que abrem o produto elevado sem perguntar. O Fort Knox **não** faz isso.
- **Assinatura e integridade:** tudo que é baixado (driver, atualização, base da MURALHA) é conferido por SHA-256 publicado e por Authenticode antes de usar (o ALICERCE já faz isso para NVIDIA; vira serviço do Core `ITrustVerifier`).
- **Pipe do Guarda:** ACL restrita, comandos fechados, argumentos validados, caminhos normalizados (contra path traversal), limite de tamanho de mensagem.
- **Segredos:** nunca em log, nunca em texto puro em disco. Logs passam por um filtro que remove caminhos de perfil quando exportados.
- **Log local:** `%AppData%\FortKnox\logs` (app) e `%ProgramData%\FortKnox\logs` (Guarda), rotação por tamanho, sem envio automático. Botão "Gerar relatório de erro" cria um `.zip` que a pessoa revisa e envia se quiser.

## 11. Clean room: como usamos o relatório IObit

**Pode** (informação funcional e pública):
- Saber que categorias de função um hub desses oferece (limpeza, desinstalação com restos, atualização de apps, drivers, proteção em tempo real, quarentena) e usar isso para planejar páginas.
- Aprender com o desenho geral (UI separada do serviço, agendamentos) e, principalmente, com o que **evitar** (dezenas de executáveis, SkipUAC, promoções embutidas, endpoints de terceiros, filtros de kernel sem necessidade).

**Não pode** (e não está neste documento):
- Copiar ou adaptar código, binários, DLLs, recursos, ícones, skins, sons, textos de interface, changelogs ou traduções.
- Usar bancos, listas ou dados deles (`WhiteList.db`, `Dll.dbd`, listas de drivers, bancos de startup, bases antimalware).
- Chamar endpoints deles (`update.iobit.com`, `driverboosterscan.com` etc.).
- Descompilar para entender algoritmos. O relatório é estático e fica assim.

**Código ou dados de terceiros sempre passam por você antes.** Itens que vão precisar de aprovação quando chegarmos neles:

| Item | Licença | Por que pode ser útil | Observação |
|---|---|---|---|
| `Microsoft.Extensions.DependencyInjection` / `Hosting` | MIT (Microsoft) | DI e serviço do Windows | baixo risco |
| `CommunityToolkit.Mvvm` | MIT (.NET Foundation) | menos código repetido no MVVM | opcional; dá para fazer sem |
| YARA (`libyara`) + binding .NET | BSD-3 (YARA) | regras de conteúdo da MURALHA | binário nativo a mais no pacote |
| Regras YARA públicas (ex.: repositórios comunitários) | variam (muitas GPL ou com restrição comercial) | base inicial de regras | **checar licença regra a regra** |
| Hashes maliciosos públicos (ex.: MalwareBazaar) | termos do provedor | base inicial de hashes | checar termos de redistribuição |
| EasyList (se um dia houver proteção de navegação) | GPLv3 / CC BY-SA 3.0 | listas de bloqueio | incompatível com embutir em app MIT sem cuidado |
| ClamAV | GPLv2 | motor pronto | **não recomendo**: GPL contaminaria a distribuição MIT |

Nenhum desses entra sem o seu "sim" explícito.

## 12. Decisões para você aprovar

| # | Decisão | Opções | Recomendo |
|---|---|---|---|
| **D1** | Elevação do app | (a) manter tudo como administrador, como hoje; (b) app como usuário comum + Guarda executa o que precisa de admin | **(a) agora, com `IPrivilegedOps` pronto para (b) depois.** (b) resolve o risco do cofre com "outro admin" e é mais seguro, mas é grande: sensores (LibreHardwareMonitor), ETW, OC e drivers precisam ir para o serviço. |
| **D2** | Organização do código | (a) vários projetos (`Core`, `Hardware`, `UI`, `App`, um por módulo, `Guarda`); (b) um projeto só com pastas e teste de arquitetura | **(a).** O compilador garante que módulo não depende de módulo. O `.exe` continua um arquivo só. |
| **D3** | Serviço Guarda | (a) criar já na fase da MURALHA; (b) não ter serviço, tempo real só com o app aberto (bandeja) | **(a)**, instalado só quando a pessoa liga o tempo real ou alertas; desinstala junto ao desligar. |
| **D4** | Motor de detecção da MURALHA | (a) próprio: hashes + heurística + regras em formato próprio, mais segunda opinião do Defender; (b) (a) + YARA (pacote de terceiros) | **(b)**, sujeito à sua aprovação de licença do YARA e de cada conjunto de regras. |

## 13. Roteiro (as três fases pedidas, uma por vez, cada uma com sua aprovação)

| Fase | Entrega | Pronto quando |
|---|---|---|
| **1** · Arquitetura | este documento | você aprovar (com ou sem ajustes) |
| **2** · Design System e UI/UX | componentes base em `FortKnox.UI` a partir do Brandbook v3 e dos tokens (`fortknox-tokens.json`): botões, modais/confirmação destrutiva, grids de dados, cartão de status Bom/Atenção/Ruim, anel/medidor, **Scanner** (progresso, item atual, achados ao vivo, cancelar), toasts, estados vazio/carregando/erro; estrutura de pastas final e tecnologias recomendadas; página de catálogo dos componentes nos dois temas | catálogo aprovado por você visualmente, claro e escuro |
| **3** · Implementação dos módulos | em sub-etapas, cada uma com PR próprio: | |
| 3.1 · Núcleo | `FortKnox.Core` (barramento, store, jobs, config com migração, Diário de Ferrolhos, log, SQLite) + Shell com Navegador e registro de módulos, sem mudar o que você vê | app abre igual, testes do Core passando, migração do `settings.json` testada |
| 3.2 · Módulos prontos | COFRE, PRENSA, RADAR viram `IModule` (já têm serviço separado) | mesmos testes de antes + navegação nova |
| 3.3 · Migração do restante | SENTINELA, FORJA, ARENA, VIGIA, ARSENAL, ALICERCE, OFICINA, PRISMA, PAINEL; o `MainWindow.xaml` gigante é dividido em Views com os componentes da Fase 2 | `MainWindow` vira só o Shell |
| 3.4 · Lastro + ARSENAL novo | indicadores de saúde, Limpeza, Desinstalar com restos, Atualizar apps (equivalentes funcionais de Uninstaller, Software Updater e da limpeza do SystemCare) | cada ação com prévia e desfazer |
| 3.5 · MURALHA | motor, quarentena, varredura sob demanda (equivalente funcional do Malware Fighter) | testes com amostra inofensiva (arquivo EICAR) |
| 3.6 · Guarda | serviço, pipe, tempo real ETW, agendamentos, alertas | instala/desinstala limpo, sobrevive a reinício |

Equivalência com as ferramentas de referência: **Driver Booster** → ALICERCE; **Smart Defrag** → SENTINELA · Discos (saúde) + otimização de unidade pela API do Windows (`defrag /O`, que faz TRIM em SSD), sem motor próprio de desfragmentação; **Advanced SystemCare / Ultimate** → PAINEL (Lastro) + ARSENAL + MURALHA.

Cada sub-etapa termina com revisão de assincronia, segurança e memória (como nas etapas do Vault, PDF e Rede) e um PR próprio.

## 14. Notas de implementação

### Fase 3.1 · Núcleo (`src/FortKnox.Core`)

| Peça | Arquivo | Observação |
|---|---|---|
| Barramento | `Events/EventBus.cs` | entrega `UiThread` (pelo `SynchronizationContext` do WPF), `Background` ou `Synchronous`; handler com erro vai para o log e não bloqueia os outros |
| Estado global | `State/StateStore.cs` | fatias `record` imutáveis; `Update` serializado; mudança sem diferença não avisa ninguém |
| Trabalhos | `Jobs/JobRunner.cs` | dono, título, progresso, cancelamento; `Exclusive` recusa com mensagem clara; erro vira `Failed`, nunca derruba o app |
| Preferências | `Config/SettingsStore.cs` | `%AppData%\FortKnox\modules.json` (`schema: 2`), uma seção por módulo, gravação atômica com `.bak`, arquivo corrompido é guardado à parte. **Convive com o `settings.json` antigo**: cada módulo migra a sua parte com `Migrate<T>` quando passar a usar o núcleo (3.2/3.3) |
| Diário de Ferrolhos | `Changes/ChangeJournal.cs` | SQLite `history.db`; grava `Pending` antes de aplicar, `Applied` + dados de desfazer depois; desfazer funciona depois de reiniciar. Primeiro tipo pronto: `registry.value` (`RegistryValueChange`), que inclui "o valor não existia" |
| Log | `Log.cs` | `%AppData%\FortKnox\logs\fortknox-AAAAMMDD.log`, 5 MB/dia, 14 dias, nunca enviado |
| Módulos e Lastro | `Modules/*.cs` | `IModule`, `NavEntry`, `ModuleRegistry` (init na 1ª visita, ativar/desativar, `PageChanged`), `Lastro` (média ponderada; qualquer Ruim limita a 59) |
| Composição | `FortKnoxHost.cs` | criado em `App.OnStartup`, exposto em `App.Core`, encerrado em `OnExit` (cancela trabalhos até 3 s) |

Diferença em relação à §5.1: **sem contêiner de DI de terceiros**. O `IModuleContext` entrega as peças do núcleo prontas,
e `IModule.Register(IServiceCollection)` saiu do contrato. Se um dia fizer falta, `Microsoft.Extensions.DependencyInjection` entra com a sua aprovação.

Testes: `tests/FortKnox.Core.Tests` (27 testes: barramento, estado concorrente, trabalhos, preferências, ferrolhos no registro real em `HKCU\Software\FortKnoxTests`, Lastro e registro de módulos).

### Fase 3.2 · COFRE, PRENSA e RADAR como módulos

- `Modules/Vault/CofreModule.cs`, `Modules/Pdf/PrensaModule.cs`, `Modules/Network/RadarModule.cs` implementam `IModule` (via `ModuleBase`) e substituem `MainWindow.Vault.cs`, `MainWindow.Pdf.cs` e o `StopNetTools` de `MainWindow.Net.cs`. As Views e os serviços não mudaram.
- `MainWindow.Shell.cs` é a ponte: registra os módulos e, a cada clique no menu, chama `ModuleRegistry.NavigateAsync`. Páginas já migradas têm rota (`cofre/senhas`, `prensa/pdf`, `radar/conexao`); as outras navegam como `legado/PageX` só para desativar o módulo anterior.
- Comportamento preservado: sair do COFRE tira as senhas da memória (tolerância do Windows Hello de 60 s), sair da Rede para o ETW, fechar o app limpa a área de transferência e cancela o PDF em andamento.
- `ModuleRegistry.ShutdownAsync` roda uma vez só (a janela chama na thread da interface; o host chama de novo no fim sem efeito).
- **Trava do COFRE configurável:** Configurações (engrenagem na barra de título, `SettingsWindow`) tem o interruptor "Usar o Windows Hello", ativo só quando o Windows informa que o Hello está configurado; senão fica cinza. Sem Hello (ou desligado), o COFRE pede a **senha mestra** (`MasterPasswordWindow`, 5 tentativas e pausa de 30 s). A preferência fica na seção `cofre` do `modules.json`. A senha de teste é provisória: ver `docs/PRODUCAO-PENDENCIAS.md`.

### Fase 3.3 · Migração das demais páginas (em fatias, um PR por fatia)

- **3.3a · ARSENAL (Inicialização e Instalar apps):** XAML das duas páginas saiu do `MainWindow.xaml` para `Modules/Arsenal/StartupView.xaml` e `AppsView.xaml`, a lógica de `MainWindow.Startup.cs`/`MainWindow.Apps.cs` foi para os code-behind e o `ArsenalModule` cuida do ciclo de vida. "Restaurar removidos" passou do `settings.json` antigo para a seção `arsenal` do `modules.json` (migração automática, uma vez). Confirmações usam o `FkDialog` do Design System. Otimização fica para a fatia da SENTINELA (depende do ciclo de 1 s dos sensores).
- **3.3b · OFICINA (Portas COM e Drivers seriais):** páginas em `Modules/Oficina/PortsView.xaml` e `SerialDriversView.xaml`, lógica do antigo `MainWindow.Maker.cs` e do trecho USB/COM do `MainWindow.xaml.cs`, `OficinaModule` no núcleo. Plugar/remover placa agora é o evento `UsbDevicesChanged` no barramento (o Shell publica; a OFICINA atualiza a página aberta), o primeiro uso real do barramento entre Shell e módulo. Backup do ComDB migra para a seção `oficina`. `AdminGuard` (aviso de administrador com `FkDialog`) é compartilhado pelas Views.
- **3.3c · ALICERCE (Drivers):** página em `Modules/Alicerce/DriversView.xaml`, lógica do antigo `MainWindow.Driver.cs`, `AlicerceModule` no núcleo. Fechar o app agora cancela a consulta, o download ou o backup em andamento. Confirmações com `FkDialog`.
- **3.3d · PRISMA (Iluminação):** página em `Modules/Prisma/PrismaView.xaml`, lógica do antigo `MainWindow.Integ.cs`, `PrismaModule` no núcleo. Os sensores (efeitos por temperatura/carga) entram por `Attach(SensorMonitor)` em vez de campo do `MainWindow`. O efeito continua ao trocar de página; ao fechar o app, cada fabricante reassume as luzes.
- **3.3e · ARENA · Biblioteca:** página em `Modules/Arena/LauncherView.xaml`, lógica do antigo `MainWindow.Launcher.cs`, `ArenaModule` no núcleo. Perfis por jogo e jogos adicionados à mão migram para a seção `arena` do `modules.json`. FPS estimado e Gargalo ficam para a fatia da SENTINELA (dependem do ciclo de 1 s dos sensores).
- **3.3f · VIGIA (Overlay):** página em `Modules/Vigia/OverlayView.xaml`, lógica de `MainWindow.Overlay.cs` e do trecho de overlay do `MainWindow.xaml.cs` na View, `VigiaModule` no núcleo. A View é ligada já na abertura (`VigiaModule.Prepare`), porque o atalho Ctrl+Shift+O funciona sem visitar a página; o atalho e os benchmarks usam `PageOverlay.IsOn`. Leituras de GPU/CPU compartilhadas saíram do `MainWindow` para `Modules/Shared/LiveReadings.cs` (base da fatia da SENTINELA). Preferências do overlay migram para a seção `vigia`.
- **3.3g · FORJA · Stress test:** página em `Modules/Forja/StressView.xaml`, toda a lógica do stress (CPU, RAM, GPU estilo FurMark, cortes por temperatura, gráfico e log) saiu do `MainWindow.xaml.cs` para a View; `ForjaModule` no núcleo. A View é ligada na abertura e recebe `Tick()` do ciclo de 1 s; avisa o menu lateral e o PAINEL por eventos (`RunningChanged`, `Finished`). Os outros testes consultam `PageStress.IsRunning`. Temperatura da GPU principal entrou no `LiveReadings`.
- **3.3h · FORJA · Overclock:** página em `Modules/Forja/OcView.xaml`, lógica do antigo `MainWindow.Oc.cs` (ajustes NVML, verificador de estabilidade, guia de BIOS) na View; `ForjaModule` passa a ter as rotas `forja/stress` e `forja/overclock`. Dependências da janela principal entram por `Attach` (leituras, histórico, "outro teste rodando", pausa dos sensores durante a cena de GPU, nome da CPU). Os modelos de dados que ficavam em `MainWindow.Resources` (cartões de GPU, medidores, células) foram para `UI/Templates.xaml`, no App, para as Views dos módulos os encontrarem.
- **3.3i · FORJA · Benchmark:** a maior página do app (6 testes) foi para `Modules/Forja/BenchView.xaml`; a lógica do render de CPU e dos testes de jogo (antes no `MainWindow.xaml.cs`), do ray tracing (`MainWindow.Rt.cs`, removido) e do teste de disco (antes em `MainWindow.Disks.cs`) está na View. Dependências entram por `BenchView.Host` (leituras, histórico, outro teste rodando, pausa dos sensores, overlay, eventos do PAINEL). O histórico (`BenchRuns`/`BenchRecords`) continua no `settings.json` porque PAINEL e relatório ainda leem dali; migra junto com a SENTINELA. Com isso a FORJA inteira é módulo.
- **3.3j · ARENA · FPS estimado e Gargalo:** páginas em `Modules/Arena/GamesView.xaml` e `BottleneckView.xaml`, lógica saída do `MainWindow.xaml.cs`; `ArenaModule` com as três rotas. A janela principal só detecta CPU e GPU (`DetectCpuGpuAndInitArena`) e liga as Views; o Gargalo recebe `Tick()` do ciclo de 1 s. Com isso a ARENA inteira é módulo.
- **3.3k · ARSENAL · Otimização:** página em `Modules/Arsenal/OptimizeView.xaml`, lógica saída do `MainWindow.xaml.cs` (memória, programas em segundo plano, ajustes do Windows com "Restaurar originais"); `ArsenalModule` com as três rotas. O backup dos ajustes continua em `AppSettings.TweakBackup` (a API `WindowsTweaks` o usa); a troca pelo Diário de Ferrolhos fica para a Fase 3.4. Confirmações com `FkDialog`. Com isso o ARSENAL inteiro é módulo.
- **3.3l · SENTINELA · Sensores, Placa de vídeo, Discos e Energia:** páginas em `Modules/Sentinela/` (`SensorsView`, `GpuView`, `DisksView`, `EnergyView`), lógica saída do `MainWindow.xaml.cs`, `MainWindow.Disks.cs` e `MainWindow.Net.cs`; `SentinelaModule` no núcleo. As Views recebem o `SensorMonitor` por `Attach`; a linha de status dos sensores virou `PageSensors.Status`. Sistema (informações, unidades, avisos de USB e relatório) fica para a última fatia, com o PAINEL.
- **3.3m · SENTINELA · Sistema e RADAR · teste de conexão:** `Modules/Sentinela/SystemView` (categorias do WMI, cartões das unidades e o relatório exportável, antes em `MainWindow.Disks.cs`, removido) e `Modules/Network/RadarView` (teste de conexão clássico, antes em `MainWindow.Net.cs`, com o `NetToolsView` dentro). Os avisos de USB/unidade continuam no Shell (dependem do `WM_DEVICECHANGE` da janela) e entregam as unidades novas à View. O relatório recebe GPUs, histórico e o último teste de rede pelo `SystemView.Host`. Com isso a SENTINELA e o RADAR inteiros são módulos.
- **3.3n · PAINEL e ciclo de sensores:** `Modules/Painel/DashView` (gráfico CPU × GPU, notas, componentes, anéis, eventos recentes, saúde do PC; antes `MainWindow.Dash.cs`, removido) e `PainelModule`. O `DispatcherTimer` que chamava cada página virou `Modules/Shared/SensorPump`: relê os sensores e publica `SensorsRefreshed` no barramento do núcleo; cada página assina no Shell (`StartSensorPump`). O PAINEL navega por `NavigateTo(page)` em vez de conhecer os itens do menu.

**Fim da Fase 3.3:** todas as páginas são Views de módulos (`Modules/<Módulo>/`), cada módulo segue `IModule` e a janela principal ficou com o Shell: menu, barra de título, avisos de USB, atalho do overlay, detecção de CPU/GPU e a composição das dependências (`MainWindow.Shell.cs`). `MainWindow.xaml` foi de ~2.960 para ~340 linhas e o `MainWindow.xaml.cs` de ~1.500 para ~390.

### Fase 3.4 · Lastro e ARSENAL novo (em fatias)

- **3.4a · Lastro no PAINEL:** cada módulo publica seus indicadores (`IModule.HealthIndicators`, com `Modules/Shared/HealthIndicators.cs`): SENTINELA (sensores da CPU, S.M.A.R.T. dos discos), FORJA (erros WHEA e reinícios do driver em 7 dias), ARSENAL (espaço livre no disco do Windows, Modo de Jogo, Game DVR) e OFICINA (números COM presos). O núcleo calcula a nota (`Lastro.Calculate`: média ponderada; qualquer Ruim limita a 59) e o cartão "Saúde do PC" do PAINEL mostra a nota, o nível e cada verificação com o motivo e o botão **Resolver**, que abre a página certa. Indicador sem leitura (ex.: discos ainda não lidos) tem peso 0 e fica fora da conta. A saúde dos discos é lida uma vez em segundo plano quando os sensores abrem.
