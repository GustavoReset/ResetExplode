# Engenharia reversa estática dos instaladores IObit

Data da análise: 2026-11-09
Ferramenta principal: REA instalado globalmente
Alvo: `C:\Users\Josh\Downloads\iObit`

## Escopo e limites

Esta análise é **estática**. Nenhum instalador foi executado e nenhum processo de rede foi iniciado pelos binários analisados. Portanto, este documento não afirma o comportamento real em execução, condições de administração elevadas, download de componentes durante a instalação, telemetria ou chamadas externas que só seriam observáveis em runtime.

Foi analisado cada PE32 com REA: identidade por SHA-256, tabela de recursos, tabela de importações, certificados Authenticode, exportações e strings relevantes. O perfil disponível não conseguiu analisar PE via o modo genérico `inspect_binary_layout`, pois esse modo está limitado no ambiente a layouts ELF Linux. Esse limite não impede a conclusão das inspeções PE específicas usadas abaixo.

## Inventário

| Produto/arquivo | Tamanho | SHA-256 | Arquitetura PE | Exportações declaradas |
|---|---:|---|---|---:|
| Smart Defrag | 16.650.144 bytes | `52327AB1FDD7CC23A881DE818D4C20C8F29EB2582BD3BD25DC066A494035848A` | PE32 / x86 | 0 |
| Driver Booster | 34.182.960 bytes | `1FEBDA76C6D69BA0D981913B040292EB35DBD669A23497813C4AF651B8B8A348` | PE32 / x86 | 0 |
| IObit Uninstaller | 34.309.696 bytes | `DD422FBE4C4F9D90062EB3845E62BB209BCCC5678DF1D4F9895BBB14A9A4959A` | PE32 / x86 | 0 |
| Advanced SystemCare | 61.146.528 bytes | `FB8224C9B64D3E25EB316866D8EC829E257E42A69B9E4D295AFD64991DCB8AAE` | PE32 / x86 | 0 |
| ASC Ultimate | 157.972.088 bytes | `5E6B01264427A1B133DC739373F02210FE0A4D1846CC9E53127E08F1383AC6D7` | PE32 / x86 | 0 |

Todos foram reconhecidos pelo REA como executáveis PE x86, sem diretório de exportações. Ausência de exportações é compatível com instaladores autônomos/bootstrap executáveis, mas não prova que não existam DLLs embarcadas internamente ou componentes extraídos pelo instalador.

## Evidência PE comum

Os quatro primeiros binários tiveram as mesmas cinco DLLs diretamente importadas:

- `oleaut32.dll`
- `advapi32.dll`
- `user32.dll`
- `kernel32.dll`
- `comctl32.dll`

Isso indica uso de:

- Recursos Visuais Windows e controles comuns.
- Componentes de janela, mensagens e entrada de teclado.
- Registro do Windows.
- Privilégios administrativos.
- Strings COM/OLE e memória gerenciada por Win32.
- Operações de arquivo, processo, arquivos temporários e sistema.

O REA não encontrou tabelas de delay-load nos quatro arquivos já concluídos. O arquivo ASC Ultimate possui importação PE no mesmo formato; ficou limitado pelo orçamento padrão de leitura de 64 MiB, mas foi reexecutado com orçamento de 157.972.088 bytes.

### Contagens observadas por importação

| Arquivo | Funções importadas observadas | DLLs importadas |
|---|---:|---:|
| Smart Defrag | 120 | 5 |
| Driver Booster | 122 | 5 |
| IObit Uninstaller | 120 | 5 |
| Advanced SystemCare | 122 | 5 |

Essas contagens representam importações de nome declaradas na tabela PE. Não representam chamadas reais nem cobertura completa de código ou componentes extraídos.

## Recursos, layout visual e ícones

Os executáveis possuem árvore de recursos PE. Os recursos observados incluem entradas de ícone, diálogos e recursos de versão/manifesto; nomes e IDs são mapeados pelo tipo PE. O recurso de ícone principal está presente como grupo estático RT_GROUP_ICON, com recursos RT_ICON associados.

No Smart Defrag foram observados:

- Múltiplos recursos de tipo 3 (ícone), incluindo grupos em inglês (`1033`).
- Recursos de payload próprios, com RVA, offset de arquivo e SHA-256.
- Grupo de ícones `id 1` com várias imagens incorporadas.
- Recursos adicionais ligados à interface Win32, manifesto e metadados de aplicação.

Isso demonstra que o instalador contém ativos gráficos no próprio executável. A imagem exata só pode ser vista por extração/renderização do recurso; execução não foi feita neste relatório. Não há evidência suficiente para afirmar formatos de tela, organização de botões ou efeitos dinâmicos apenas pela árvore estática.

## Identificação do empacotador

Strings explícitas do pacote incluem a URL da ajuda de linha de comando do **Inno Setup/Setup Compiler**:

`http://www.jrsoftware.org/ishelp/index.php?topic=setupcmdline`

**Inferência forte:** os instaladores usam um bootstrap baseado em Inno Setup ou em uma arquitetura compatível derivada dele. Essa inferência é baseada em URL e APIs de instalação, não em um cabeçalho exclusivo confirmado.

O único endpoint externo visível em massa nas strings não são endpoints da IObit:

- `cacerts.digicert.com`
- `crl3.digicert.com`
- `ocsp.digicert.com`
- `cacerts.globalsign.com`
- `crl.globalsign.com`
- `ocsp.globalsign.com`
- `secure.globalsign.com`
- `www.globalsign.com`

Eles são URLs de certificados/revogação usadas por verificações de assinatura e cadeia. Não devem ser interpretados como endpoint de atualização do produto.

Não foram observadas, nos strings/amostras estáticas coletadas, URLs HTTP(S) claras da IObit para download, atualização ou API. Isso não prova ausência: strings podem estar compactadas, criptografadas, contidas em arquivos embarcados ou geradas em runtime.

## Assinatura Authenticode

Os dados PE confirmam uma tabela Authenticode em vários arquivos. O PKCS#7 embutido usa assinatura de código SHA/SHA-256, com certificados DigiCert, GlobalSign e/ou cadeias de timestamping associadas. O REA confirmou a presença das cadeias e certificados, mas deixou explícito que não valida cadeia de confiança, revogação ou autenticidade.

Observações por certificado:

- A entrada de certificado ASC Ultimate está presente e aponta para uma tabela com 25.536 bytes.
- Ela possui payload PKCS#7 de 25.527 bytes, SHA-256 `7af4F7CF4AB93A490D8A922C74ED1E25886F533707C8DD91895AA9AED4760587`.
- REA encontrou certificado signing/timestamp misto no pacote.
- A estrutura de alinhamento de três installers desencadeou `invalid PKCS#7 alignment padding`, mesmo após Windows já ter reconhecido a assinatura dos pacotes na análise anterior. Isso é uma limitação/ambiguidade do parser específico e não uma prova de assinatura inválida.

Validação de confiança completa requer `signtool verify /pa /v`, catálogo Windows ou inspeção detalhada da cadeia. Os arquivos não devem ser classificados como maliciosos ou falsificados a partir desta etapa.

## APIs externas inferidas

### Sistema Windows observado diretamente pelas importações

- Registro: leitura e escrita de chaves/configuração.
- Controle de janela e mensagens: interface gráfica Win32.
- Controles comuns: diálogos, barra de progresso, listas, botões.
- Tokens de privilégio e ajuste de privilégio: possibilidade de solicitar/exigir elevação.
- Operações de processo, biblioteca, arquivo, diretório, temporários, memória e sincronização.
- OLE/COM Automation: strings, BSTR e interop de componentes.

### APIs externas não confirmadas

Não foram identificadas DLLs externas ao Windows no conjunto de quatro instaladores já concluído. Não há evidência estatística de chamadas a `WinInet`, `WinHTTP`, `WS2_32`, HTTP libraries ou DLLs criptográficas comuns em suas diretivas estáticas de importação.

Isso não significa que o instalador não baixará componentes, pois um componente pode ser carregado depois, chamado por ordinais, obtido por `LoadLibrary`, ou parte de um pacote comprimido e não visível no PE.

## Ferramentas e funcionalidades inferidas de cada produto

As inferências abaixo são baseadas no nome/identidade comercial, arquitetura conhecida do setup e no fato de ser um instalador bootstrap. Nenhuma funcionalidade do produto final foi executada ou extraída neste relatório.

### Smart Defrag

**Arquivo observado:** `smart-defrag-setup.exe`

**Funções de instalação esperadas, inferidas:** aceite de licença, seleção de diretório, criação de atalhos, registro no Windows, opção de idioma, extração de componentes, instalação, desinstalação e integração com o painel do produto.

**Recursos:** ícones Win32 e árvore de recursos extensa compatível com wizard de instalação.

**Elementos de produto não confirmados:** interface após instalação, agendamento, defragmentação automática, optimização SSD, atualizações e telemetria.

### Driver Booster

**Arquivo observado:** `driver_booster_setup.exe`

**Funções de instalação esperadas:** wizard, termos, diretório, componentes opcionais, atalhos, permissões administrativas e extração dos componentes principais.

**Funções do produto não confirmadas:** varredura de drivers, base de dados de atualização, backup, rollback, download do driver, integração com Windows Update e ativação.

### IObit Uninstaller

**Arquivo observado:** `iobituninstaller.exe`

**Funções de instalação esperadas:** seleção de idioma/diretório, criação de atalhos, registro de desinstalador, componentes de início rápido e integração com Menu/contexto do Windows.

**Funções do produto não confirmadas:** desinstalação, varredura de resíduos, força de remoção, desinstalação em lote, toolbar remover, atualização e limpeza.

### Advanced SystemCare

**Arquivo observado:** `advanced-systemcare-setup.exe`

**Funções de instalação esperadas:** wizard padrão e instalação de componentes do produto.

**Funções do produto não confirmadas:** otimização, limpeza de registro, limpeza de disco, privacidade, desempenho, proteção, proteção em tempo real, atualizações e telemetria.

### ASC Ultimate

**Arquivo observado:** `asc-ultimate-setup.exe`

O binário é aproximadamente 2,5 vezes maior que Advanced SystemCare, sugerindo maior volume de payload, componentes comerciais ou pacote completo. A evidência estática confirma apenas maior tamanho e recursos PKCS#7. Não é seguro afirmar quais módulos adicionais contém sem extrair o pacote de forma isolada.

**Funções não confirmadas:** pacote deve incluir componentes do suíte; nomes são inferidos pela identidade comercial, não pela confirmação de arquivo embarcado.

## Layout PE observado

Os instaladores são PE32 x86. Os importadores mostram uma tabela de importação localizada próxima ao primeiro megabyte de diferentes offsets, mas nenhuma seção foi detalhada por layout provider. Não há evidência suficiente para listar `.text`, `.data`, `.rdata`, `.rsrc` e seus offsets em todos os arquivos.

O recurso PE do Smart Defrag inclui:

- Diretórios de recurso com major/minor 4.0.
- Entrada de recursos `RT_ICON` e nome de recurso ID.
- Payloads com offsets, tamanhos e SHA-256.
- Dados de linguagem 1033 no ícone.

Essa estrutura é típica de executável Win32 com interface de instalação baseada em recursos.

## Chamadas externas conhecidas

| Tipo | Observação | Classificação |
|---|---|---|
| Certificados/CRL/OCSP | URLs de DigiCert e GlobalSign | Observado em strings/PKCS#7 |
| Site de ajuda Inno Setup | `jrsoftware.org/ishelp` | Observado em strings |
| API IObit/telemetria | Nenhuma URL confirmada | Desconhecido |
| Atualização | Nenhum endpoint confirmado | Desconhecido |
| Download de payload | Nenhuma URL confirmada | Desconhecido |

## Evidência REA e IDs

| Arquivo | Operação principal | Evidence ID |
|---|---|---|
| Smart Defrag | Importações | `ev_b12210d34cdddc044c96538ef5d0b5f213b11d6139d0767f677450197b9135bf` |
| Driver Booster | Importações | `ev_00e7b040bea0378d159e91932b405e91f4882c99737b61e046f7a` |
| IObit Uninstaller | Importações | `ev_c297b86c5b7481f5e9accf193c87d195f8b917c370db69720a859f4cf60832e8` |
| Advanced SystemCare | Importações | `ev_e34c9ff2cd7f062ec271935101f667ec4dc4b7b3b7c8b5bf2c32dc6ad0d99f` |
| ASC Ultimate | Recursos/importações/certificado | `ev_454bf86c994d1413364f1009887ca75fa15cd60cb89aef9c7deddfec2e307181`, `ev_977826632679864ec53dced16e05eb3f7097c97f798020514774b39707abf25b90`, `ev_cd2e15ebfcaa39f13ce441bf69aebe31d78989f9120025d8b9ba3b9c6836ef16` |

## Lacunas que exigem runtime ou extração adicional

1. Extrair recursos e payloads com uma ferramenta de pacote sem executar os instaladores.
2. Confirmar o formato real do empacotador por headers internos.
3. Comparar hashes de recursos, recursos por versão e diferenças entre instalações.
4. Executar em VM isolada com snapshot e monitorar:
   - processos filhos;
   - acesso ao registro;
   - arquivos/serviços criados;
   - tráfego DNS/HTTP(S);
   - alterações de firewall;
   - criação de tarefas agendadas;
   - download e assinatura dos componentes.
5. Capturar telas do wizard e registrar layout, fontes, ícones, animações, estados de erro e efeitos.
6. Confirmar assinaturas usando WinVerifyTrust/signtool.

## Complemento: Advanced SystemCare instalado em D:\Program Files\Advanced SystemCare

### Identidade da instalação

A instalação localizada em `D:\Program Files\Advanced SystemCare` corresponde à versão `20.0.1.256` e foi instalada depois da análise dos instaladores em Downloads. Esta seção complementa o relatório original com arquivos efetivamente presentes na instalação. Também inclui hashes coletados da instalação real, mas não afirma que todos os componentes foram executados.

| Componente | Versão observada | Tamanho | SHA-256 |
|---|---|---:|---|
| `ASC.exe` | 20.0.1.256 | 12.026.824 | `420CEC52DF1D64815841C5045898C9CAC92F035A7A97C1CFEBD77A8214652AC4` |
| `ASCService.exe` | 20.0.0.239 | 1.857.480 | `DB940896CE082BBD245494F46B10776B550E6D0A15089C2C6068E817B962CC41` |
| `ASCTray.exe` | 20.0.0.1070 | 6.144.456 | `CCD32A117452F32DA6FB7FFFAA0A132D4C587CA11AF439D6B85712B82CC49E83` |
| `IObitLiveUpdate.exe` | 3.0.0.6467 | 3.194.824 | `43907458F92C3286DD29559363D81A201C0DA6C266A4BA1AF9D5E18026872530` |
| `AutoUpdate.exe` | 16.0.0.951 | 3.787.208 | `FF07EDC9ABB81BCCBBA4D31C4034CA29761963DD829B5272F611709CDA2A784B` |
| `AutoCare.exe` | 19.0.0.156 | 3.433.928 | `AC88625D72B90E08675825A87D90D27C1B32D03FEC8CBCED6F3BDD77B659B9E1` |
| `BrowserProtect.exe` | 20.0.0.283 | 2.041.800 | `A71A923FE616F0ED74B1B27EEED15FB8AE5AD1CE25D4B75DBE0465AE655B6816` |
| `PrivacyShield.exe` | 14.0.0.23 | 1.908.168 | `256CBC7E6771289A64891A6A7B2C86D25D072BD1004225719855B44BF51365AA` |
| `RealTimeProtector.exe` | 17.0.0.273 | 1.123.784 | `8D8FD4A7AC565808C7F03790F167F189E84610B14F4D316BF59CAA64BBACF453` |
| `ASCFeature.exe` | 4.1.0.511 | 506.312 | `8E5CE5338A1EA6542D97B961247B8277FFDCDB3F4D7CDCBA5F314230262C5201` |
| `StartupInfo.exe` | 18.0.0.71 | 553.416 | `BE280D0A991CF154EFB90A0C23439AC4F4847315B045554DB1894102701BFAAA` |
| `SendBugReportNew.exe` | 16.0.0.7 | 1.013.704 | `7F8B2DD7F309D7BF08BCD8DAF60B59DD2F578FE9D0CF0301025BB742235E040B` |
| `ASCUpgrade.exe` | 14.2.0.64 | 1.569.224 | `32EF6F93B8CDBA01BE285F65270BC994C0757FE52C427A0ED89543C33DF8EC94` |

### Arquitetura, processos e responsabilidades

| Arquivo/pasta | Função observada ou fortemente suportada |
|---|---|
| `ASC.exe` | Interface principal. PE com 34 DLLs importadas e 983 funções de nome observadas. Usa `wininet.dll`, `winhttp.dll`, SQLite, Win32 GUI, shell, rede, versão, OLE/COM, utilitários de usuário e módulos próprios como `Scan.dll`, `DiskScan.dll`, `PluginHelper.dll` e `ProductNews2.dll`. |
| `ASCService.exe` | Serviço de sistema. PE com 21 DLLs importadas e 688 funções observadas; exporta a função PE `madTraceProcess` como ordinal 1 no endereço `0x4681dc`. Cria timers para atualização, uso, limpeza automática, varreduras e monitoramento. |
| `ASCTray.exe` | Integração com área de notificação. PE com 16 DLLs importadas e 709 funções observadas. |
| `IObitLiveUpdate.exe` | Atualização integrada/live update. PE com 17 DLLs importadas e 703 funções observadas; inclui WinInet. |
| `AutoUpdate.exe` | Atualizador autônomo. Contém 1.001 funções de importação declaradas no orçamento mínimo já observado; o inventory completo requer orçamento acima desse limite. |
| `AutoCare.exe` | Cuidados, limpeza e varredura automática. As tabelas de importação declaram pelo menos 1.001 funções observadas. |
| `BrowserProtect.exe` | Proteção do navegador. Declara pelo menos 1.001 funções de importação; há inspeções separadas para Native Messaging, listas de bloqueio e DLL de plugin. |
| `PrivacyShield.exe` | Proteção de privacidade e limpeza de rastros. |
| `RealTimeProtector.exe` | Monitoramento/Proteção em tempo real. |
| `smBootTime.exe` / `smBootTimeBase.exe` / `RegistryDefragBootTime.exe` | Utilitários pré-inicialização/tempo de inicialização. |

Os diretórios contém ainda módulos especializados:

- `CustomClean.exe`, `BrowserCleaner.exe`, `StartupInfo.exe`, `DiskDefrag.exe`, `MonitorDisk.exe`, `Monitor.exe`, `AutoSweep.exe`, `AutoRamClean.exe`, `ActionCenterDownloader.exe`, `AppsUpdater.exe`, `AppsChecker.exe`, `RescueCenter.exe`, `QuickSettings.exe`, `FeedBack.exe` e `Display.exe`.
- Módulos auxiliares como `Scanner.dll`, `Scan.dll`, `sdcore.dll`, `sqlite3.dll`, `rgfpctl.dll`, `rgfpctlextend.dll`, `fctlextend.dll`, `filectl.dll`, `Webres.dll`, `InfoHelp.dll`, `JumpListDll.dll`, `PowerConfig.dll`, `CPUIDInterface.dll` e bibliotecas `libcrypto-1_1.dll`/`libssl-1_1.dll`.

### Recursos, skins, localização e ícones

REA registrou recursos PE de `ASC.exe`, incluindo árvore de recursos, tipos, nomes, localizações, linguagem e payloads. A instalação também contém skins separados:

- `skin\classic.dll`
- `skin\public.dll`
- `skin\readable.dll`

Há histórico/localização em 37 idiomas no `History.txt`, com arquivos adicionais em `History` para Árabe, Chinês Tradicional, Holandês, Francês, Alemão, Italiano, Japonês, Polonês, Português Brasil, Russo e Espanhol. O produto usa `Lang.dat` e `LocalLang.exe` para seleção/localização e `webres.dll`/`skin` para recursos de interface.

`History.txt` confirma:

- limpeza personalizada de arquivos duplicados/grandes;
- engine de varredura multi-módulo;
- monitoramento de velocidade de rede;
- monitoramento de temperatura de disco;
- limpeza de lixo incluindo Microsoft Store;
- base antisspyware ampliada;
- gerenciamento ampliado de inicialização;
- atualizador de terceiros/Microsoft Store;
- proteção de navegação;
- proteção de privacidade;
- Safe Folder;
- UI redesenhada.

O layout visual completo, estados, efeitos, fontes e animações não foram capturados porque esta complementação permaneceu estática; registrar o posicionamento visual exige captura do processo/soft UI ou inspeção de REA de interface em runtime.

### Drivers e componentes de baixo nível

A pasta `drivers` contém variantes para Windows 7, Windows 10, x86, x64 e IA64:

| Driver | Evidência de intenção |
|---|---|
| `AscFileControl.sys` | Controle de arquivos em baixo nível, presente em variantes Win7/Win10/x86/x64/IA64. |
| `AscFileFilter.sys` | Filtro de sistema de arquivos, presente em variantes por arquitetura/SKU. |
| `AscRegistryFilter.sys` | Filtro de registro, presente em variantes por arquitetura/SKU. |
| `NetDataFlow.sys` | Filtro/monitoramento de fluxo de dados de rede, presente em `win10_amd64`. |
| `Monitor_win*.sys` / `Monitor_x86.sys` | Monitor específico por família Windows/arquitetura. |
| `RegistryDefragBootTime.exe` | Desfragmentação do registro antes/na inicialização, presente em várias variantes. |

Há utilitários de instalação de driver em `DrvInstall`, incluindo DIFxAPI x32/x64, `DbzInst.dll` e instaladores DpInst. Os nomes demonstram código de filtro/controle, mas não provam quais filtros estão ativos ou carregados nesse host sem inspeção do SCM/Carregador/objetos kernel.

### Serviço observado em runtime histórico

O log existente de `ASCService.log` registra em 08/11/2026:

- usuário de serviço `SYSTEM`;
- sessão gráfica ativa para `Josh`;
- criação de `ASCService.exe`;
- Windows 10 build 19045;
- inicialização de `smBootTime.exe`;
- `ASCTray.exe` iniciado para o usuário;
- timers de limpeza configurados em 18.000 segundos;
- checagens anti-buraco de segurança em 10.000 segundos;
- varredura avançada em 45.000 segundos;
- atualização a cada 150 segundos;
- uso de ASC a cada 86.000 segundos;
- desfragmentação de memória iniciada;
- monitor de privacidade iniciado;
- timer de limpeza automática iniciado;
- parada explícita registrada pelo serviço.

Esses eventos são observações históricas. Não provam que todos os timers disparam em cada sessão nem que todas as varreduras concluíram.

### Proteção de navegador

`ASCPlugin_Protection.dll` é PE x86 de 910.792 bytes, SHA-256 `FD7C4481994AEFF79C7EC764723492C132F1B1A872371C1540938FB924E456E5`. O REA encontrou quatro exportações COM:

- `DllUnregisterServer`
- `DllRegisterServer`
- `DllCanUnloadNow`
- `DllGetClassObject`

Esta exportação é compatível com uma DLL COM/Navegador, mas não prova seu carregamento em runtime.

A DLL importa 12 DLLs e 482 funções no inventário observado, incluindo `ASCUrlScanner.dll`. A pasta `Surfing Protection` contém:

- hosts Native Messaging para Chrome/Edge e Firefox:
  - `SPNativeMessage.exe`;
  - `SPNativeMessage_Edge.exe`;
  - `FF_SPNativeMessage.exe`.
- `NativeMsg.json` com Chrome origin IDs;
- `NativeMsg_Edge.json` com Chrome e Edge origin IDs adicionais;
- `AppManifest.json`;
- EasyList, EasyList China, whitelist, exceções e `prefs.json/settings.ini`.
- atualizador `SPUpdate.exe` e instalador da extensão `PluginInstall.exe`.

Native Messaging é confirmado nos manifests; comunicação real ou permissões efetivas devem ser corroboradas por inspeção de registro/extensão instalada e runtime.

### Atualização e endpoints externos confirmados estaticamente

O arquivo `Update\Update.ini` em UTF-16 contém, entre outros:

- versão principal `20.0.1.256`;
- patch `https://update.iobit.com/dl/asc20/patch/asc20011029_patch.exe`;
- diretório de patch `https://update.iobit.com/dl/asc20/patch/`;
- diretório de atualizações `https://update.iobit.com/dl/asc20/update1014/`;
- notícias `https://download.iobit.com/appnews/default/asc_news.dat`;
- página promocional `https://www.iobit.com/goto.php?id=event1&ref=latestversion`;
- endpoint inicial `https://startup.iobit.com/proportion_asc14.php`;
- scan de drivers `https://s1.driverboosterscan.com/worker.php`;
- backup `https://s2.driverboosterscan.com/worker.php`;
- lista de drivers `https://www.cd40.com/drivers/wlst/v.json`.
- feed Toolbox/Firmware, Freeware, Software, histórico de idioma e atualizações localizadas em `update.iobit.com/infofiles/asc20`.

Os parâmetros contêm `/verysilent`, `/Patch`, `/NoRestart`, `/skip_wlc` e `/sp-`, indicando instalação silenciosa em patch/configurações específicas. O arquivo também contém whitelist, limites de atualização, promoções de outros produtos, gatilhos de download e referências de registro. Estas configurações não provam que todos os componentes foram baixados ou instalados.

### Telemetria, logs e arquivos de diagnóstico observados

A instalação contém logs históricos e mecanismos de bug report:

- `ASC.log`;
- `ASCService.log`;
- `AutoUpdate.log`;
- `LiveUpdateSrvUpt.log`;
- `AutoCare.log`;
- `PrivacyShield.log`;
- `startupInfo.log`;
- `AutoSweep.log`;
- `smBootTime.log`;
- `smBootTimebase.log`.

`Update.ini` define coleta de arquivos para bug report, incluindo `ASC.log`, `Exception.log`, `ASCService.log`, `AutoUpdate.log`, logs de usuário/tray, `MalwareScan.log` e `RegScan.log`. Define também limites de tamanho e opt-out de screenshot (`GetScreenshot=0`). Isso confirma um mecanismo de diagnóstico configurado; o conteúdo, frequência e envio real não foram verificados por runtime.

### Dados locais, banco e configuração

A instalação apresenta arquivos SQLite `sqlite3.dll` e diretórios `Database`, `ScanData`, `Config`, `LatestNews` e `Pub`. O diretório `Database` contém:

- `AutoUpdate.dat`;
- `Register.dat`;
- `DriverCleanIgnore.dat`;
- `HotHole.dat`;
- listas de drivers por Windows;
- bases de dados `InBoxDriverFeature`.

São observações de persistência/configuração; sem executar o programa ou analisar cada SQLite, não é possível afirmar integridade, conteúdo completo ou dados de usuário armazenados.

### Produtos IObit promocionais observados

`Update.ini` contém referências a IObit Uninstaller, Malware Fighter, Driver Booster, IObit Software Updater, iTop VPN, IObit Driver Booster, IObit Data Recovery, PDF Tips, IObit Malware Fighter e outras campanhas. `Update.ini` também contém URLs de `Pub` e nomes de executáveis promocionais como `ascpp.exe`, `ascevent.exe`, `bf26.exe`, `iipfr.exe` e `iioPDCS.exe`.

Isso indica promoção/configuração de produtos auxiliares dentro da distribuição, mas não confirma que esses produtos sejam instalados ou executados automaticamente.

### Lacunas do complemento

- Nenhuma captura de tela do produto e nenhum processo em execução foi usado nesta complementação.
- Nenhum registro System/SCM ou catálogo carregado foi validado contra os drivers.
- Nenhum decompilador completo foi aplicado aos executáveis, DLLs ou drivers.
- Nenhum payload compactado/assinado foi extraído integralmente.
- Nenhuma captura DNS/HTTP(S) foi realizada, portanto endpoints são configurações estáticas, não chamadas observadas.

## Complemento final do Driver Booster 14.0.1.462

### Identidade e instalação

A instalação real foi localizada em `D:\Program Files\Driver Booster\14.0.1` e contém 220 arquivos. O diretório ocupa aproximadamente 150,9 MB. O instalador original observado foi `C:\Users\Josh\Downloads\iObit\driver_booster_setup.exe`, SHA-256 do pacote `1FEBDA76C6D69BA0D981913B040292EB35DBD669A23497813C4AF651B8B8A348`.

O log `InnoSetup.log` identifica:

- instalador Inno Setup 5.6.1;
- Windows 10 build 19045;
- arquitetura de instalação x64;
- execução administrativa;
- diretório de destino em `D:\Program Files\Driver Booster\`;
- parâmetros `/verysilent /Installer /norestart /DIR="D:\Program Files\Driver Booster\" /runupt`;
- desinstalador registrado em `HKEY_LOCAL_MACHINE\Software\Microsoft\Windows\CurrentVersion\Uninstall\Driver Booster_is1`;
- instalação de HWiNFO em modo brandname e componentes auxiliares de drivers.

### Componentes executáveis principais e hashes

| Componente | Versão observada | Tamanho | SHA-256 |
|---|---:|---:|---|
| `DriverBooster.exe` | 14.0.1.462 | 8.840.648 | `8DCE869C65B506DA41CE3E90596A5A19862D00F6040D1C0B7A47E76D5AA351E2` |
| `Scanner.dll` | 14.0.1.101 | 991.176 | `9AE8E026DA0806198F56BA201A69765F90FA58A51086D29487641D77B3C66585` |
| `AutoUpdate.exe` | 14.0.0.491 | 2.658.248 | `71744BB6907A08384A98A0E72AC20989022E6ED78D68F409444CB9E70A990A5B` |
| `DriverUpdate.exe` | 14.0.0.42 | 1.631.688 | `5BA633BA780B5695FDDE7F0D300619439DA1B9AB2161B592EF313387F2225721` |
| `OfflineUpdater.exe` | 14.0.0.153 | 4.038.088 | `89A5313E840158181A1EA7FAC6C4A20B8ADB56B1CE5ECE2A4DA8D09E88EB9058` |
| `Backup.exe` | 13.0.0.28 | 1.149.896 | `BEB5611C66DB9046AD4DE6490759DB45C507EBB9972F5A81702BC701B17E20C3` |
| `HVCIFix.exe` | 14.0.0.93 | 3.089.864 | `79D418FC224B2DD8283844AB135B5276B1E62E422709A749D3E14C6D4316B33A` |
| `DBDownloader.exe` | 14.0.0.18 | 921.032 | `BFF206F8444F4F08425E8BC8A79C5FB6369DCF47CD0A67384097764770A449B5` |
| `FaultFixes.exe` | 12.0.0.15 | 2.042.824 | `883E232979480599EDE4F87D7B11ADAEF593B462478F69047B263FFE5CF209BA` |
| `GameDlls.exe` | 12.0.0.364 | 3.561.416 | `7139015D04F90B93E22963699E2C4C658E05C09FFF901D5CE1B2814092540D24` |
| `CareScan.exe` | 6.0.0.181 | 3.550.152 | `88BF5FB61B07011440856CC1E64911AF0E34DC23D3B5F25DC54650F64023362B` |
| `Scheduler.exe` | 12.0.0.41 | 164.808 | `C7A6FC8F250955BE2FF07A41785B8668DEEF9D3C2D402617BFE4C531107D580C` |
| `ScanWinUpd.exe` | 14.0.0.36 | 583.112 | `6C715C549BA1DA0262E3405733D2F187C405910BCD59620B5D71ECE8F9A4D8A3` |
| `ScanDisp.exe` | 12.0.0.20 | 120.776 | `A1496A7250DEA6F010E3B1A5825291260BFCE1367824130B0EA1522D4198504D` |

Outros componentes instalados incluem `Boost.exe`, `AutoNts.exe`, `Manta.exe`, `rma.exe`, `RttHlp.exe`, `Passenger.exe`, `ProductStat3.exe`, `TrialPromote.exe`, `TrialSchPt.exe`, `Bugreport.exe`, `IsuScan.exe`, `Transform.exe`, `AUpdate.exe`, `InstStat.exe`, `IObitDownloader.exe`, `SetupHlp.exe`, `x64Proxy.exe`, `DpInstX32.exe`, `DpInstX64.exe`, `Microsoft DPInst`, DIFxAPI e instaladores DpInst.

### Evidências PE do executável principal

REA identificou `DriverBooster.exe` como PE32 x86. A tabela de importação contém 109 descritores/import groups e 2.979 funções nomeadas dentro do orçamento analisado; não foi encontrada tabela de delay-load.

As APIs/DLLs observadas incluem, entre outras:

- `kernel32.dll`, `ntdll.dll`, `advapi32.dll`, `user32.dll`, `gdi32.dll`, `comctl32.dll`, `msimg32.dll`;
- `shell32.dll`, `shlwapi.dll`, `ole32.dll`, `oleaut32.dll`;
- `version.dll`, `winmm.dll`, `wsock32.dll`, `wininet.dll`, `winhttp.dll`;
- `wtsapi32.dll`, `userenv.dll`, `crypt32.dll`, `imagehlp.dll`, `iphlpapi.dll`;
- `sqlite3.dll`, `zip.dll`, `PowerMgr.dll`, `DataState.dll`, `ProductNews2.dll`;
- bibliotecas Delphi/Runtime: `rtl120.bpl`, `vcl120.bpl`, `vclx120.bpl`, `madBasic_.bpl`, `madDisAsm_.bpl`, `madExcept_.bpl`.

Isso sustenta estáticamente a existência de GUI Win32, registro, arquivos temporários/processos, shell, HTTP/WinInet, criptografia, multimídia, sessões Windows, versões, SQLite, empacotamento ZIP e tratamento de exceções Delphi. Não confirma quais chamadas são executadas, em qual frequência ou quais resultados produzem.

`AutoUpdate.exe` apresentou PE32 x86, 77 descritores de importação e 2.209 funções observadas. Importa WinInet, WinHTTP, Crypto32, shell, registro, Win32 GUI, ZIP/runtime Delphi. `DriverUpdate.exe` apresentou PE32 x86, 75 descritores e 2.052 funções observadas. Importa WinSock, WinInet, shell, registro, Win32 GUI, NTDLL/runtime Delphi.

### Assinatura Authenticode

`DriverBooster.exe` possui uma entrada PKCS#7 autenticada no diretório de segurança, com offset 8.815.104 e tamanho 25.544 bytes. O payload PKCS#7 tem tamanho 25.531 bytes e SHA-256 `230C27DE3031494B1D8C17A51AB8CEA13457C08B465DACEC57D815D873DD91BE`.

REA encontrou estrutura `signedData` e algoritmo de hash SHA-1 na estrutura PKCS#7 observada, além de assinatura RSA SHA-256 em certificados incorporados. Os certificados incluem cadeias GlobalSign/DigiCert/identidades associadas ao assinante. REA não validou cadeia de confiança, revogação, timestamp ou autenticidade completa.

### Recursos, identidade visual e efeitos

O recurso PE de `DriverBooster.exe` contém recursos nomeados `MAD`, `MEICONTINUE`, `TMADEXCEPT` e `MAINICON`, além de entradas com linguagem 1033. O ícone principal está representado por relações estáticas `RT_GROUP_ICON`/`RT_ICON`; os payloads do ícone não foram decodificados como imagem nesta passagem.

A instalação possui recursos visuais separados:

- skins Qt: `Skin\black.rcc`, `Skin\public.rcc`, `Skin\images.rcc`;
- biblioteca de botões/controles: `cbutton.dll`, `Focus.dll`, `MsgBoxEx.dll`;
- ícones em `Icons\`;
- 38 arquivos de localização registrados em `Language\`;
- `Wave.mp3` de 7.990 bytes, sugerindo efeito sonoro/reprodução associada ao produto;
- arquivos históricos em 38 idiomas.

O histórico confirma interface redesenhada em 2026, modernização da interface, suporte a 38 idiomas, varredura de drivers latest/stable, engine reencrenada, múltiplas versões de drivers, instalação multi-versão, mais de 23 milhões de drivers, suporte a SOCKS5, reparo de 60+ componentes de jogo, 680+ DLLs e detecção em tempo real de DLLs ausentes. A confirmação visual completa dos estados da interface requer captura do processo em execução.

### Atualização, endpoints e promoções

`Update\Update.ini` define:

- versão principal `14.0.1.462`;
- patch silencioso em `https://update.iobit.com/dl/db/v8/patch/db1401_20260924.exe`;
- diretório de patch em `https://update.iobit.com/dl/db/v8/patch/`;
- arquivos traduzidos em `update.iobit.com/infofiles/db/UpdateHis/`;
- patch anunciado de 34.182.960 bytes;
- parâmetros `/sp- /verysilent /suppressmsgboxes /runupt`.

A configuração contém endpoints:

- `https://s1.driverboosterscan.com/worker.php`
- `https://s2.driverboosterscan.com/worker.php`
- `https://s1.driverboosterscan.com/worker2.php`
- `https://s2.driverboosterscan.com/worker2.php`
- `https://www.cd4o.com/drivers/wlst/v.json`
- variantes test e listagem de drivers por localização.

`Update.ini` também contém componentes promocionais e auxiliares como `iipfr`, `bf26`, `iiopdcs`, `dbpm.exe`, `IOBPP.dll`, `cbutton_all.dll`, `Config_all.ini`, anúncios BF26 e comandos de retorno. Essas linhas são uma configuração estática de pacotes/promoções; não provam que os componentes foram baixados ou executados neste host.

### Bancos de dados, filtro e dados locais

Componentes persistidos observados incluem:

- `Database\Scan\WhiteList.db`, 19.362.788 bytes;
- `Database\sMarUpdateInfo.dbd`, 1.387.250 bytes;
- `Database\Dll.dbd`, 80.026 bytes;
- `Database\startupBlack.db`, `StartupDRate.db`, `startupService.db`, `startupSignature.db`, `startupWhite.db`;
- `Boost\Score.db`, listas de processos/serviços permitidos e bloqueados;
- arquivos de configuração em `LocalData\`, incluindo scheduler, dispositivos, pop-ups, filtros, vendors, whitelist e atualização offline.
- `ScanData` contém dados temporários de varredura.

`InstConf.ini` contém distinção de dispositivos PCI/HDAUDIO importantes, códigos especiais ignorados, códigos especiais com pop-up, filtros de dispositivo, filtros de erro e códigos para Windows Update/hardware. É uma configuração de decisão da varredura, não uma confirmação de exclusão ou instalação real.

### Agendamento, tarefas e startup

Foram identificadas três tarefas agendadas principais:

- `Driver Booster Scheduler` executa `Scheduler.exe /scheduler`;
- `Driver Booster SkipUAC (Josh)` executa `DriverBooster.exe /skipuac`;
- `Driver Booster Update` executa `AutoUpdate.exe /auto`.

`Scheduler.ini` define atrasos adicionais:

- atualização automática: 240 segundos;
- varredura na inicialização: 30 segundos;
- varredura agendada: 10 segundos;
- exibição na barra de tarefas: 60 segundos;
- DLLs ausentes: 30 segundos;
- drivers incompatíveis: 180 segundos;
- AutoNts: 150 segundos;
- monitor público: 30 segundos;
- download ocioso: 720 segundos.

### Arquitetura funcional observada

A arquitetura estática é compatível com esta divisão:

1. `DriverBooster.exe` centraliza interface/configuração e coordena o produto.
2. `Scanner.dll`, `CareScan.exe`, `IsuScan.exe`, `ScanWinUpd.exe` e `ScanDisp.exe` são os componentes de varredura/discriminação.
3. `DBDownloader.exe`/`IObitDownloader.exe` fazem download de pacotes.
4. `DriverUpdate.exe` e utilitários DpInst/DIFxAPI realizam a cadeia de instalação de drivers.
5. `Backup.exe` realiza backup/restore de drivers.
6. `OfflineUpdater.exe` oferece fluxos offline.
7. `HVCIFix.exe` corresponde a correções ligadas a HVCI/Virtualização.
8. `FaultFixes.exe` trata falhas, `GameDlls.exe` DLLs/arquivos de jogos e componentes de compatibilidade.
9. `AutoUpdate.exe`/`AutoNts.exe`/`Scheduler.exe` controlam atualização e agendamento.
10. `WhiteList.db`, `Dll.dbd`, listas de WHL, `sMarUpdateInfo.dbd` e bancos startup alimentam filtros/regras/estado da varredura.

### Lacunas, limites e verificações ainda não feitas

- Nenhum processo do produto foi analisado em execução nesta complementação; tarefas, drivers ativos e serviços de baixo nível não foram validados em runtime.
- Nenhuma captura HTTP/DNS foi realizada; os endpoints são configuração estática.
- Nenhum conteúdo de todos os bancos SQLite/DBD foi consultado por uma ferramenta de banco dedicada.
- Nenhuma tela, animação, fonte ou coordenada visual foi extraída/renderizada.
- Nenhum decompilador completo ou chamada de função foi aplicado aos principais executáveis.
- Nenhum instalador de drivers foi confirmado como instalado ou carregado.
- Nenhuma cadeia de verificação Authenticode foi validada por `signtool verify /pa /v`, revogação ou catálogo externo.
- Não foi confirmado envio de telemetria, frequência real dos timers nem execução automática das promoções.

## Complemento final dos cinco produtos restantes

Esta complementação incorpora as instalações atualmente presentes no Windows e preserva os limites da análise anterior: a maior parte das conclusões vem de inventário de arquivos, metadados PE, importações, recursos, tarefas agendadas, serviços, arquivos históricos/configuração e assinaturas embutidas. Nenhum processo foi executado para observar telas ou tráfego de rede.

### IObit Uninstaller 16.1.0.4

#### Identidade da instalação

| Propriedade | Evidência |
|---|---|
| Diretório | `D:\Program Files\IObit Uninstaller` |
| Arquivos inventariados | 138 |
| Tamanho observado | 128.526.640 bytes |
| Executável principal | `IObitUninstaler.exe` |
| Versão | 16.1.0.4 |
| Tamanho do executável | 10.646.976 bytes |
| SHA-256 | `320C59F6D0F29AF8A75342FC0242C4BF5DFFA100D300A25666C514EB84EC2617` |

#### Componentes observados

| Componente | Versão | Função estática inferida |
|---|---:|---|
| `IObitUninstaler.exe` | 16.1.0.4 | Interface central, varredura, desinstalação, limpeza e coordenação. |
| `iush.exe` | 16.0.0.16 | Atualização/integração auxiliar de saúde do software. |
| `UninstallMonitor.exe` | 16.0.0.49 | Monitoramento de instalação/desinstalação. |
| `UninstallExplorer.dll` | 1.0.7.16 | Integração com Explorer/listagem e desinstalação. |
| `AutoUpdate.exe` | 16.0.0.162 | Atualização automática. |
| `CrRestore.exe` | 16.0.0.1 | Restauração/roll-back ligado ao monitoramento. |
| `SpecUTool.exe` | 16.0.0.3 | Ferramentas especiais da caixa de ferramentas. |
| `PPUninstaller.exe` | 16.0.0.3 | Desinstalação de componentes promocionais/enxertos. |
| `IObitDownloader.exe` | 16.1.0.14 | Download de componentes e atualizações. |
| `Feedback.exe` | 15.0.0.21 | Feedback/relatos enviados pelo usuário. |
| `SendBugReportNew.exe` | 10.0.10.101 | Coleta/envio de diagnóstico e relato. |
| `ScreenShot.exe` | 10.0.10.101 | Captura gráfica auxiliar para suporte. |
| `libcrypto-1_1.dll` | 1.1.0h | Primitivas OpenSSL/criptografia de componentes. |

Outros diretórios relevantes: `Action Center`, `Database`, `Drivers`, `DS`, `History`, `Language`, `Pub`, `Skin` e `TaskbarPin`.

#### Evidência PE e APIs

REA encontrou 63 descritores/import groups e 935 funções nomeadas observadas no executável principal; não encontrou diretório de delay-load no orçamento analisado.

As DLLs incluem `kernel32.dll`, `user32.dll`, `gdi32.dll`, `advapi32.dll`, `shell32.dll`, `shlwapi.dll`, `wininet.dll`, `ole32.dll`, `oleaut32.dll`, `crypt32.dll`, `wintrust.dll`, `version.dll`, `ws2_32.dll`, `sqlite3.dll`, `PluginHelper.dll`, `SysRest.dll`, `ProductNews2.dll` e componentes Delphi como `rtl120.bpl`/`vcl120.bpl`.

Esta evidência sustenta operações Win32, rede HTTP, shell, registro, criptografia/trust, restauração, SQLite, monitoramento, atualização e interface. Não confirma que todos os módulos foram carregados ou chamadas executadas.

#### Atualização e chamada externa estática

`Update.ini`/configuração de atualização declara patch `https://update.iobit.com/dl/iu/Patch/IU161_20261105.exe` para a versão 16.1.0.4. A presença da URL e de um executável declarado é observação estática, não prova de download ou execução.

O histórico de versões descreve melhorias de detecção/limpeza, maior precisão em Software Health, Software Relocator, Bloatware Remover, redesigned UI, modos claro/escuro e suporte a 39 idiomas.

#### Tarefas, layout e recursos

- Foi identificada a tarefa agendada `IObit Uninstaller SkipUAC (Josh)`, executando o produto com parâmetro `/skipuac`.
- O diretório `Skin` contém recursos de apresentação; `Language` contém localizações; `History` contém changelogs.
- `Database`/`Drivers`/`DS` sugerem bancos/regras locais de software, drivers e módulos de desinstalação, mas o conteúdo não foi interpretado integralmente.

### Smart Defrag 12.1.0.619

#### Identidade da instalação

| Propriedade | Evidência |
|---|---|
| Diretório | `D:\Program Files\Smart Defrag` |
| Arquivos inventariados | 136 |
| Tamanho observado | 109.097.607 bytes |
| Executável principal | `SmartDefrag.exe` |
| Versão | 12.1.0.619 |
| Tamanho do executável | 7.130.048 bytes |
| SHA-256 | `95E532336400A935A4DF58FC5C2EA6F23084419AC224DFC9607F56186C88A4B5` |

#### Componentes e finalidade inferida

`SmartDefrag.exe`, `AutoUpdate.exe`, `AutoDefrag.exe`, `AutoAnalyze.exe`, `DiskInfo.exe`, `diskinfo.dll`, `GameScaner.dll`, `sdcore.dll`, `sdassist.dll`, `SDDriverMgr.dll`, `CareScan.exe`, `FeedBack.exe`, `SDInit.exe` e bibliotecas de interface/promocionais representam o núcleo de desfragmentação, atualização, análise automática, informações de disco, jogos, drivers, startup/serviços auxiliares e ações promocionais.

O diretório contém `Database`, `DB`, `drivers`, `Extension`, `Language`, `LatestNews`, `pub`, `skin` e recursos associados.

#### Evidência PE e APIs

REA encontrou 109 descritores/import groups e 2.793 funções nomeadas observadas no principal; não encontrou delay-load.

As importações observadas incluem WinInet/WinHTTP, Delphi/Runtime, shell, registro, UI, SQLite, `sdcore.dll`, `sdassist.dll`, `SDDriverMgr.dll`, `GameScaner.dll`, mídia, controle de arquivos e bibliotecas de atualização/promocionais.

#### Atualização, agendamento e interface

- Foram identificadas tarefas: `Smart Defrag Auto Analyze`, `Smart Defrag Startup` e `Smart Defrag Update`.
- `www.iobit.com/update/sd/index.php` aparece como URL Free/Pro de atualização.
- `AutoAnalyze.exe`, `AutoDefrag.exe`, `AutoUpdate.exe` e `Shuffle.exe` são componentes separados de políticas agendadas.
- Recursos gráficos em `skin`, linguagens em `Language`, `LatestNews`, ícones/Taskbar e notícias locais indicam interface customizada e campanhas News/Action Center.
- `CareScan.exe` sugere integração com varredura de otimização, mas não estabelece execução conjunta com ASC ou outro produto.

### IObit Software Updater 7.0.0.16

#### Identidade da instalação

| Propriedade | Evidência |
|---|---|
| Diretório | `C:\Program Files (x86)\IObit\Software Updater` |
| Arquivos inventariados | 97 |
| Executável principal | `SoftwareUpdater.exe` |
| Versão | 7.0.0.16 |
| Tamanho | 4.566.896 bytes |
| SHA-256 | `840FA1208F3845278A991505E0644292EB18A957C0DCC971088D6254E87EB004` |

#### Componentes observados

`SoftwareUpdater.exe`, `ForceUpdate.exe`, `SUInit.exe`, `AutoUpdate.exe`, `Setup.exe`, `Restore.exe`, `CareScan.exe`, `Feedback.exe`, `Scn.dll`, `IObitDownloader.exe`, `ProductNews2.dll`, `library_itBanner.dll`, `BigUpgrade_ISU.exe` e componentes promocionais em `Pub`.

Diretórios incluem `Database`, `Icons`, `Language`, `LatestNews`, `Pub`, `ScanData`, `Skin` e `TaskbarPin`.

#### Evidência PE

REA encontrou 78 descritores/import groups e 2.613 funções nomeadas no executável principal; não encontrou delay-load.

As importações incluem WinInet, URLMon, shell, registro, UI Win32/VCL, OpenSSL, banco de dados, componentes promocionais, download e atualização.

#### Funcionalidades observadas/static

O histórico confirma grande base de software, suporte a software de IA como ChatGPT/Chatbox/Grammarly/Canva, software de produtividade como LibreOffice/XMind/Notion/Dropbox/Slack/Zoom/TeamViewer, testes de estabilidade/segurança, otimização de interface e 36 idiomas.

Arquitetura estática compatível:

1. inicialização por `SUInit.exe`;
2. detecção por `SoftwareUpdater.exe` e `Scn.dll`;
3. dados em `Database` e `ScanData`;
4. download por `IObitDownloader.exe`;
5. atualização/força por `ForceUpdate.exe`/`AutoUpdate.exe`;
6. restauração por `Restore.exe`;
7. promoção/news por `Pub`, `LatestNews` e banners;
8. pinagem por `TaskbarPin`.

Atualização estática observada em `Update.ini`: versão declarada 9.2.0 com patch `https://update.iobit.com/dl/isu6/ISU9.2.0_Patch.exe`. Isso representa uma configuração de atualização compatível com a instalação, não confirmação do número final em runtime.

### IObit Malware Fighter 14.0.0.1766

#### Identidade da instalação

| Propriedade | Evidência |
|---|---|
| Diretório | `D:\Program Files\IObit Malware Fighter` |
| Arquivos inventariados | aproximadamente 345 |
| Tamanho aproximado | 288 MB |
| Executável principal | `IMF.exe` |
| Versão | 14.0.0.1766 |
| Tamanho | 7.193.536 bytes |
| SHA-256 | `1675C8AB021D6D893047B047971A3D2FE83D9C93BD8BFA66819C7A48E53EA0AA` |

#### Componentes principais

`IMF.exe`, `IMFCore.exe`, `ImfElamSvc.exe`, `AutoUpdate.exe`, `IObitLiveUpdate.exe`, `IMFRegister.exe`, `BlueBirdInit.exe`, `FeedBack.exe`, `IMFTips.exe`, `IMFInstaller.exe`, núcleo de varredura/quarentena, proteção de navegação e bancos antivírus.

Diretórios: `Database`, `db`, `Drivers`, `Language`, `LatestNews`, `log`, `pub`, `Quarantine`, `ScanData`, `skin`, `Surfing Protection` e `TaskbarPin`.

#### Evidência PE

REA encontrou 64 descritores/import groups e 862 funções nomeadas no principal; não encontrou delay-load.

As importações incluem WinInet/WinHTTP, Quartz, shell, registro, Crypto, UI, sessões, filtros e componentes próprios como `Imfrgfpctl`, núcleo anti-malware e produto.

#### Funcionalidades documentadas nos recursos/changelog

O histórico de versão 14 indica integração do engine Bitdefender, engine anti-malware IObit reforçado, varredura de startup ampliada, anti-tracking/browser cookie cleanup, Browser Password Guard, proteção em tempo real, bancos ampliados, proteção contra ameaças impulsionadas por IA e suporte a 33 idiomas.

A presença de `ImfElamSvc.exe`, drivers associados, `Quarantine` e `Surfing Protection` é compatível com ELAM/boot-start protection, núcleo de scan, quarentena e proteção de navegação. A execução desses serviços não foi observada.

#### Assinatura e configurações

O executável principal possui tabela Authenticode/PKCS#7 embutida. REA observou identidades IObit/GlobalSign/DigiCert e cadeia de timestamping, mas não validou revogação ou confiança.

Foram observadas URLs IObit do gerenciador de atualização e páginas de licenciamento. Nenhuma captura DNS/HTTP confirmou o uso real por este executável.

### Advanced SystemCare Ultimate 18.5.0.120

#### Identidade da instalação

| Propriedade | Evidência |
|---|---|
| Diretório | `D:\Program Files\Advanced SystemCare Ultimate` |
| Arquivos inventariados | 699 |
| Tamanho observado | 357.372.994 bytes |
| Executável principal | `ASC.exe` |
| Versão | 18.5.0.120 |
| Tamanho | 11.593.664 bytes |
| SHA-256 | `C37F8FB2AC824CAA4B90D944D026B2DD6B1BD3A4500A98DDEAB5A289DCE3227A` |

#### Arquitetura e componentes

A instalação contém `ASC.exe`, `ASCTray.exe`, `ASCService.exe`, componentes `Antivirus`, `Surfing Protection`, `Rescue Center`, `Monitor`, `AutoCare`, `Backup`, `Boottime`, `Toolbox_Language`, bancos em `Database`, logs em `ASCServiceLog`, drivers, ScanData, skins e Action Center.

Diretórios relevantes incluem `ActionCenter`, `Antivirus`, `ASCServiceLog`, `Backup`, `Boottime`, `Config`, `Database`, `drivers`, `DrvInstall`, `Language`, `LatestNews`, `PinLink`, `ScanData`, `skin`, `Surfing Protection` e `Toolbox_Language`.

#### Evidência PE

REA encontrou 91 descritores/import groups e 997 funções nomeadas observadas no executável principal; não encontrou delay-load.

Importações incluem WinInet/WinHTTP, SQLite, Cabinet, PluginHelper, Scan.dll, sdcore.dll, Crypto, PSAPI, multimídia, redes e componentes próprios. Isso é compatível com GUI, limpeza/varredura, atualização, redes, serviços, drivers, backup e componentes antivírus.

#### Funcionalidades documentadas staticamente

1. Limpeza e otimização do sistema.
2. Varredura, monitoramento e AutoCare.
3. Recuperação/Rescue Center.
4. Proteção em tempo real/Antivirus e ELAM.
5. Surfing Protection/Browser Protection.
6. Drivers e manutenção de hardware.
7. Backup, quarentena, logs e restauração.
8. Update/licenciamento/product news.
9. Skins, idiomas e Action Center.

#### Serviços e agendamento

Foram observados serviços locais associados ao produto, inclusive `Advanced SystemCare Service` e componentes Antivirus; o estado do serviço varia no host e não deve ser inferido como persistente em todos os sistemas.

Tarefas agendadas observadas:

- `Advanced SystemCare Ultimate Performance Monitor`;
- `Advanced SystemCare Ultimate SkipUAC`.

Estas tarefas indicam monitoramento/performance e bypass de prompt UAC para o usuário atual, mas não provam periodicidade efetiva no momento da análise.

#### Assinatura e endpoints

REA encontrou certificado PKCS#7 em `ASC.exe`, com identidade incorporada `IObit Co., Ltd.`, cadeia GlobalSign GCC R45 EV CodeSigning CA 2020/DigiCert/GCC timestamping, conforme os pares recolhidos. Não foi validada cadeia, revogação ou autenticidade completa.

Os endpoints compartilhados observados em componentes/configurações do ecossistema incluem atualização IObit, páginas de licenciamento/product news e workers de detecção. Nenhuma chamada HTTP/DNS foi capturada.

## Evidência de identidade visual e recursos dos cinco produtos

Os diretórios e executáveis confirmam ativos visuais próprios:

- `Skin`/`skin`: temas e imagens locais.
- `Language`: localizações empacotadas.
- `LatestNews`/`pub`/`Action Center`: notícias, banners, promoções e notificações.
- `TaskbarPin`/`PinLink`: atalhos de pinagem.
- recursos PE de ícones e versões nos executáveis.
- fontes, libcrypto, OpenSSL, componentes Delphi/Win32 e componentes BPL conforme produto.

Não foram extraídas todas as imagens, renderizadas as interfaces ou capturadas animações/transições. “Design redesenhado”, “modos claro/escuro” ou efeitos específicos só são considerados confirmados quando constam explicitamente do changelog instalado, não como resultado visual desta investigação.

## Tabela consolidada de executáveis principais

| Produto | Executável | Versão | Tamanho | SHA-256 | DLLs/grupos de importação | Funções |
|---|---|---:|---:|---|---:|---:|
| IObit Uninstaller | `IObitUninstaler.exe` | 16.1.0.4 | 10.646.976 | `320C59F6D0F29AF8A75342FC0242C4BF5DFFA100D300A25666C514EB84EC2617` | 63 | 935 |
| Smart Defrag | `SmartDefrag.exe` | 12.1.0.619 | 7.130.048 | `95E532336400A935A4DF58FC5C2EA6F23084419AC224DFC9607F56186C88A4B5` | 109 | 2.793 |
| Software Updater | `SoftwareUpdater.exe` | 7.0.0.16 | 4.566.896 | `840FA1208F3845278A991505E0644292EB18A957C0DCC971088D6254E87EB004` | 78 | 2.613 |
| Malware Fighter | `IMF.exe` | 14.0.0.1766 | 7.193.536 | `1675C8AB021D6D893047B047971A3D2FE83D9C93BD8BFA66819C7A48E53EA0AA` | 64 | 862 |
| ASC Ultimate | `ASC.exe` | 18.5.0.120 | 11.593.664 | `C37F8FB2AC824CAA4B90D944D026B2DD6B1BD3A4500A98DDEAB5A289DCE3227A` | 91 | 997 |

Nenhum dos cinco executáveis principais apresentou diretório de delay-load dentro do orçamento de REA utilizado.

## Lacunas consolidadas da complementação final

- Nenhuma execução controlada foi feita para confirmar comportamento real dos executáveis.
- Nenhuma captura HTTP/DNS/Proxy foi feita para confirmar endpoints usados em runtime.
- Nenhum processo em execução, sessão Elevada, service control ou ELAM boot behavior foi instrumentado.
- Nenhum SQLite/DBD do conjunto completo foi consultado por ferramenta de banco; apenas existência/tamanho/papel nominal foram usados.
- Nenhum pacote completo de recursos visuais foi renderizado/examinado por captura visual automatizada.
- Nenhum decompilador completo foi aplicado aos cinco executáveis principais.
- Nenhuma cadeia, revogação ou timestamp foi validada externamente por signtool/Windows.
- Os endpoints são evidência estática de configuração, strings ou componentes; não confirmam chamadas atuais.
- Os textos de changelog não são evidência de implementação detalhada das mudanças descritas.
