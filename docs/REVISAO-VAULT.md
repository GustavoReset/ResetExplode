# Fort Knox · Revisão de código: Vault de Senhas (Etapa 1)

Escopo: `Modules/Vault/*` e `MainWindow.Vault.cs`. PDF e Rede ainda não existem; o checklist deles está no fim.

> **Limite desta revisão:** não havia SDK do .NET na máquina onde o código foi escrito, então **nada foi compilado nem executado**. Tudo abaixo é leitura de código. O primeiro passo é `dotnet build` e o roteiro de testes manuais da seção 5.

## 1. Assincronia

| Ponto | Situação |
|---|---|
| Leitura do arquivo + `Unprotect` | `LoadAsync` → `Task.Run(ReadFromDisk)`. OK |
| `Protect` + gravação + `File.Replace` | `MutateAsync` → `Task.Run(WriteToDisk)`. OK |
| PBKDF2 (600 000 iterações, ~0,3 s) e AES-GCM do backup | Dentro de `Task.Run`. OK. É o único trecho que travaria a UI se estivesse fora |
| Busca/filtro | Síncrona, mas só sobre lista em memória (centenas de itens, < 1 ms). OK |
| Voltar à UI | Continuações de `await` retomam na thread da UI (chamadas partem dela); **não há `Dispatcher.Invoke`**. Necessário para `Clipboard.*`, que exige STA |
| Reentrância | `SemaphoreSlim(1,1)` serializa Load/Add/Update/Delete/Import/Export; `SetBusy` desativa a tela durante a operação |
| Lock durante operação em curso | `_epoch` impede que um Load/Save que terminou depois do `Lock()` repovoe a memória |

## 2. Segurança

**O que acontece se o usuário trocar a senha do Windows?**

- **Troca normal** (Configurações / Ctrl+Alt+Del, informando a senha antiga): o Windows re-protege a chave mestra do DPAPI. O cofre continua abrindo.
- **Redefinição sem a senha antiga** (outro administrador, disco de redefinição, "esqueci a senha" de conta local), **perfil recriado**, **reinstalação do Windows** ou **arquivo copiado para outro PC/usuário**: o DPAPI não consegue mais decifrar. Em geral os dados são irrecuperáveis sem backup. Contas de domínio têm recuperação pela chave de backup do domínio (fora do nosso controle).

**Rotina implementada:**

1. `TryRead` separa três resultados: `Ok`, `CannotDecrypt` (`CryptographicException`) e `Corrupt` (magic errado, JSON inválido, E/S).
2. Se `Corrupt`, tenta o `vault.dat.bak` (a versão anterior, criada pelo `File.Replace`).
3. Se nada abrir → `VaultState.Unreadable`: a UI mostra o aviso com a causa provável, **bloqueia criar/editar/excluir** (`RequireReady`) e **não toca no arquivo**. Antes, qualquer "Adicionar" teria sobrescrito um cofre recuperável por um vazio.
4. **Backup com senha própria** (`.fkvault`): AES-256-GCM, chave PBKDF2-SHA256 600 000 it., sal e nonce aleatórios, cabeçalho como AAD, senha mínima de 8 caracteres e dupla digitação ao exportar. Importar em cofre ilegível move o arquivo antigo para `vault.dat.ilegivel-AAAAMMDDHHMMSS` (nunca apaga) e cria um cofre novo; em cofre normal, mescla por Id (vence o mais recente).

**Achados e decisões**

| # | Severidade | Achado | Situação |
|---|---|---|---|
| S1 | Média | Quem já executa como o **mesmo usuário** do Windows (qualquer programa) pode ler o arquivo e chamar `Unprotect`. É a limitação do DPAPI `CurrentUser`; "bloquear" não é autenticação (reabrir a página desbloqueia sem pedir nada). A entropia fixa do app **não é segredo** e só atrapalha leitura "às cegas". | **Aberto.** Recomendado: Windows Hello (`UserConsentVerifier`, exige TFM `net8.0-windows10.0.19041.0`) ou senha mestra antes de abrir. Só decidir com você |
| S2 | Baixa | `vault.dat.bak` guarda a versão anterior: uma senha **excluída** continua no `.bak` (cifrada) até a próxima gravação. | **Aberto.** Aceitável (mesma proteção DPAPI), mas deve constar na tela/README. Alternativa: apagar o `.bak` após gravação bem-sucedida e perder a recuperação por corrupção |
| S3 | Baixa | Strings .NET não podem ser zeradas; senhas ficam na memória gerenciada até o GC enquanto a página está aberta. | Mitigado: `Lock()` solta tudo ao sair da página/fechar; buffers `byte[]` (JSON em claro, chave PBKDF2) são zerados com `CryptographicOperations.ZeroMemory` |
| S4 | Média | Elevação por **outra conta de administrador** muda o `CurrentUser` e o `%AppData%`: o Vault parece vazio. | **Aberto**: documentado no plano; avisar na UI se for relevante |
| S5 | Baixa | Área de transferência: formatos `ExcludeClipboardContentFromMonitorProcessing`/`CanIncludeInClipboardHistory`/`CanUploadToCloudClipboard` evitam histórico e nuvem, mas **um programa que leia a área de transferência nos 30 s ainda vê a senha**. Limpeza só apaga se o conteúdo ainda for o nosso. | Aceito; inerente a "copiar senha" |
| S6 | — | Gerador de senhas usa `RandomNumberGenerator` (não `System.Random`), garante 4 classes de caracteres e embaralha com Fisher–Yates. | OK |
| S7 | — | Sem senha/chave em log, em `settings.json` ou em mensagem de erro; `JsonIgnore` não é necessário porque o cofre não passa por `AppSettings`. | OK |

## 3. Memória e recursos

| Ponto | Situação |
|---|---|
| `FileStream` de escrita | `using` + `Flush(true)` antes do `File.Replace` |
| Leitura | `File.ReadAllBytes` (fecha o handle); arquivo pequeno |
| `AesGcm` | `using` (descartável) |
| `CancellationTokenSource` do `SecureClipboard` | Cancelado e `Dispose`d a cada nova cópia, em `ClearNow()` e ao fechar o app; `Task.Delay` cancelado não vaza |
| `SemaphoreSlim` | Não usa `AvailableWaitHandle`; não precisa de `Dispose` |
| Troca de lista | Copy-on-write: a lista nova só é publicada **depois** de gravar. Se a gravação falhar, memória = disco |
| Grade | `ItemsSource` recebe `List<VaultItem>` novo a cada filtro; `Lock()` põe `null` |

## 4. Refatorações já aplicadas durante a revisão

- Formulário: ordem de `VShow.IsChecked = false` antes de preencher a senha (o handler copiava o campo antigo por cima da senha que acabara de ser carregada).
- `Refresh(note)`: o resultado da ação ("salvo", "importado", erro de senha) não é mais apagado pela linha de status.
- Erros esperados (`IOException`, `InvalidDataException`, `ArgumentException`…) mostrados na tela; nada de `catch {}` vazio em ação do usuário.

## 5. Roteiro de teste manual (antes de dar a Etapa 1 por validada)

1. `dotnet build` sem erros novos; abrir o app: a aba Vault aparece em RESET FERRAMENTAS e a abertura do app não ficou mais lenta.
2. Criar 3 registros, fechar e reabrir o app: persistem. Confirmar que `vault.dat` não mostra texto legível (abrir no Bloco de Notas).
3. Buscar por parte do nome/usuário/site. Editar e excluir.
4. Copiar SENHA, colar em outro app, esperar 30 s: a área de transferência esvazia. Copiar, depois copiar outra coisa: **não** pode ser apagada.
5. Win+V: a senha copiada **não** deve aparecer no histórico.
6. Corromper `vault.dat` (editar um byte): sem `.bak` → aviso "ilegível" e botões de edição desativados; arquivo intacto. Com `.bak` íntegro → abre.
7. Exportar backup → apagar `vault.dat` → importar com a senha certa (e com a errada: mensagem de senha incorreta).
8. Simular DPAPI falho: copiar `vault.dat` para o perfil de **outro usuário** do Windows e abrir: deve cair em `Unreadable`.
9. Sair da página e voltar: lista recarrega. Fechar o app com uma senha copiada: área de transferência limpa.

## 6. Checklist reservado para as Etapas 2 e 3 (ainda sem código)

**PDF:** `PdfDocument`/`FileStream` em `using`; `FileShare.Read`; operação inteira em `Task.Run` com progresso por página e `CancellationToken`; saída em arquivo novo (nunca sobrescrever); liberar PDFs grandes (`Dispose` + não guardar páginas em lista); limite de tamanho/aviso para PDF > 200 MB.
**Rede:** um `HttpClient` estático; `Ping` em `using`; sessão ETW com nome fixo, `Dispose` ao sair da página **e** limpeza de sessão órfã ao abrir; contadores por processo em dicionário com poda de PIDs encerrados; temporizador de 1 s parado quando a página não está visível.
