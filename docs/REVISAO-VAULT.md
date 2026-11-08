# Fort Knox · Revisão de código: Vault de Senhas (Etapa 1)

Escopo: `Modules/Vault/*` e `MainWindow.Vault.cs`. PDF e Rede ainda não existem; o checklist deles está no fim.

> **Atualização (testado):** o código foi compilado e testado depois da primeira versão deste relatório.
> - `tests/FortKnox.Tests/VaultTests.cs`: **23 testes automatizados** (CRUD, persistência, arquivo sem texto legível, busca, concorrência com 25 gravações simultâneas, `Lock`, arquivo ilegível/corrompido, `.bak`, backup `.fkvault` com senha certa/errada/arquivo adulterado, restauração sobre cofre ilegível, mesclagem, gerador de senhas). Todos passam.
> - **Bug real achado pelos testes e corrigido:** arquivo corrompido (bits trocados) é rejeitado pelo DPAPI como "não consigo decifrar", e esse caso **não tentava o `.bak`**. Agora tenta nos dois casos e avisa na tela que abriu pela cópia anterior.
> - **Teste de interface** (janela real do app, fora de elevação): abrir, criar, gerar senha, buscar, copiar, limpar a área de transferência sem apagar o que o usuário copiou depois, bloquear/desbloquear, navegar pelo menu e fechar o app. Tudo ok.
> - **Windows Hello (testado):** o IID da `IAsyncOperation<UserConsentVerificationResult>` é calculado pelo algoritmo do WinRT e **bate com o valor conhecido** (`fd596ffd-2318-558f-9dbe-d21df43764a5`); a chamada COM real ao Windows responde (`DeviceNotPresent` neste PC). Testados também com portões falsos: cancelado (não lê o cofre e não oferece "abrir sem Hello"), verificado (abre), **sair da página com o diálogo aberto** (cofre não é carregado depois) e erro no portão (falha fechada). Execução **elevada** (como o app real): 0 falhas.
> - **Mudança de requisito:** o app agora tem como alvo `net8.0-windows10.0.19041.0` (**Windows 10 versão 2004 ou mais nova**).
> - **Não testado:** o diálogo do Hello com PIN/digital de verdade (este PC não tem Hello configurado) — precisa de teste manual numa máquina com Hello.
> - **Não testado:** a limpeza automática aos 30 s (testei `ClearNow`, não esperei o relógio), Win+V (histórico de área de transferência) e DPAPI com senha do Windows realmente redefinida.
> - **Ainda não testado** (continuam sendo o roteiro manual da seção 5): itens 5 e 8.

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
| S1 | Média | Quem já executa como o **mesmo usuário** do Windows (qualquer programa) pode ler o arquivo e chamar `Unprotect`. É a limitação do DPAPI `CurrentUser`; a entropia fixa do app **não é segredo**. | **Mitigado na interface (decisão: Windows Hello).** Abrir o Vault exige PIN/digital/rosto (`HelloVerifier`, `UserConsentVerifier` com a janela do app como dona do diálogo); o arquivo só é lido **depois** de verificar. Tolerância de 60 s ao trocar de aba; BLOQUEAR zera a tolerância. **Limite que continua:** o Hello protege a *tela*; um programa malicioso rodando como o mesmo usuário ainda pode ler `vault.dat` e chamar o DPAPI sem passar por ela. Só uma senha mestra com chave própria (KDF) fecha isso. |
| S1b | Média | **Este PC não tem Windows Hello configurado** (teste real: `DeviceNotPresent`). Falhar fechado trancaria o usuário para fora. | Quando o Hello não existe, o Vault **não abre** e explica como configurar o PIN (Configurações > Contas > Opções de entrada). Há o botão "ABRIR SEM WINDOWS HELLO (SÓ AGORA)": vale **uma abertura**, não é lembrado e a exigência volta na próxima vez. Esse botão **não aparece** quando o usuário cancela ou erra o Hello (só quando o Hello é indisponível). |
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
