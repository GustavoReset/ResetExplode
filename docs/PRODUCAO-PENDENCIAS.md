# Fort Knox · Pendências antes da produção

Itens provisórios que **não podem ir para uma versão pública**. Cada um está marcado no código com o comentário `PRODUÇÃO:`
(procure com `grep -rn "PRODUÇÃO:"`).

| # | Item | Onde | O que fazer antes de publicar |
|---|---|---|---|
| 1 | **Senha mestra de teste do COFRE = `1234`** | `Modules/Vault/MasterPassword.cs` (`DevPassword`, `IsDevPassword`, `Salt`) | Primeiro uso pede para a pessoa criar a própria senha mestra (com confirmação e regra mínima de força). Guardar só o hash PBKDF2-SHA256 (600.000 iterações, sal aleatório por instalação) em `%AppData%\FortKnox`. Remover `DevPassword`, fazer `IsDevPassword` refletir a realidade e tirar os avisos "VERSÃO DE TESTE" das telas. Oferecer troca de senha nas Configurações. |

Enquanto a senha de teste estiver ativa:
- a janela da senha mestra e a tela de Configurações mostram **"VERSÃO DE TESTE"**;
- o app grava um aviso no log (`COFRE: senha mestra de TESTE em uso`) na primeira vez que o Cofre é aberto pela senha.
