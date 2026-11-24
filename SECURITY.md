# Política de segurança

Obrigado por ajudar a manter o RESET EXPLODE seguro. O programa roda como administrador e mexe em drivers, serviços e
configurações do Windows, então levamos relatos de segurança a sério.

## Como reportar uma vulnerabilidade

**Não abra uma issue pública com os detalhes.** Use um destes canais privados:

1. **Relato privado do GitHub (preferido):** na aba **Security** do repositório, clique em **Report a vulnerability**.
2. **E-mail:** gustavo@resethub.com.br, com o assunto `[SEGURANÇA] RESET EXPLODE`.

Inclua, se possível: versão do programa, passos para reproduzir, o impacto que você enxergou e, se tiver, uma sugestão de correção.

## O que esperar

- Confirmamos o recebimento em até **7 dias**.
- Avaliamos e respondemos com um plano em até **30 dias**.
- Quando a correção sair, publicamos uma nova versão e damos o crédito a você (se quiser).

## Versões que recebem correções

Apenas a **versão mais recente** publicada em [Releases](../../releases/latest).

## Sobre alertas de antivírus

O executável ainda **não é assinado digitalmente** e usa funções administrativas do Windows (sensores, drivers, ajustes de
sistema), o que pode gerar falsos positivos em alguns antivírus. Para conferir o arquivo que você baixou, compare o hash
SHA-256 (`Get-FileHash .\ResetExplode.exe`) com o arquivo `SHA256SUMS.txt` anexado à release. O código-fonte completo está neste repositório.
