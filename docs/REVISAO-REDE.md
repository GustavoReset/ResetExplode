# Fort Knox · Revisão de código: Rede avançada (Etapa 3)

Escopo: `Modules/Network/*` (`NetworkTest` movido para cá), `MainWindow.Net.cs`, `SensorMonitor.cs` (correção de resiliência).

## 1. O que foi entregue

| Recurso | Onde | Como funciona |
|---|---|---|
| **Banda com leitura ao vivo** | `BandwidthTest` | Várias conexões HTTP em paralelo, amostra a cada 250 ms (a tela mostra o Mbps subindo e descendo). Resultado em **sustentado / pico / média**: o sustentado descarta a aceleração inicial do TCP (≈2 s), o pico é a melhor janela de 1 s, e a média (que antes era o único número) subestimava o link. |
| **Fase de ping estilo Speedtest** | `BandwidthTest.LatencyAsync` | 1 requisição de aquecimento + 10 medidas na mesma conexão; mostra mínimo, mediana e jitter e o **datacenter** que respondeu (cabeçalho `cf-ray`, ex.: `GRU`). |
| **Traceroute** | `TraceRoute` | Ping com TTL crescente, em lotes paralelos de 6 saltos (um caminho de 10 saltos leva segundos); para no primeiro lote em que o destino responde; DNS reverso opcional com limite de 1,5 s. Sem administrador. |
| **Análise do caminho** | `TraceRoute.Analyze` | Separa o que é problema do que não é: perda só em salto intermediário (roteador limitando ICMP) **não** é problema; perda que continua até o destino **é**; salto de latência só é reportado se persistir até o fim, com a ressalva do cabo submarino; destino que bloqueia ping é nota, não erro. |
| **Consumo por programa** | `ProcessBandwidthMonitor` + `NetUsageAggregator` | ETW `Microsoft-Windows-Kernel-Network` (TCP/UDP, IPv4/IPv6). Sessão própria `FortKnox-Net`, só enquanto o monitoramento está ligado; para ao sair da página e ao fechar o app. |

## 2. Sobre "basear-se no Speedtest da Ookla"

**Não integrei a Ookla, de propósito.** Li a EULA oficial (`speedtest.net/about/eula`):

- Licença só para uso **"pessoal, não comercial, em um único computador"** (cl. 01).
- Proíbe **"distribuir… ou tornar disponível a terceiros o software ou qualquer funcionalidade dele"** (cl. 02e) e instalar em roteador/dispositivo não pessoal (02f).
- Os termos do site proíbem extração de dados em massa. **Não há API pública gratuita** de medição.

Embutir ou encapsular o CLI num app distribuído violaria isso. O que foi feito: **imitar a metodologia** com infraestrutura livre (fase de ping/jitter, várias conexões, descarte da aceleração, sustentado/pico, latência sob carga, indicação do servidor). Alternativas livres e legítimas, **ainda não integradas**: **LibreSpeed** (código aberto; a lista pública de servidores respondeu em teste; permite escolher o servidor de menor latência como a Ookla) e **M-Lab NDT7** (aberto, mas publica o IP do teste).

**Limite honesto:** o "ping HTTP" (40–50 ms no teste real) **inclui o tempo de processamento do servidor** e é bem maior que o ping ICMP do mesmo teste (5–8 ms). Por isso o cartão mostra os dois: o ICMP representa melhor a latência de rede pura. Os Mbps também não são comparáveis número a número com o site da Ookla (outros servidores, outro número de conexões).

## 3. Testes

`tests/FortKnox.Tests/NetworkTests.cs` (**49 testes**) + execução integrada elevada.

- **Matemática da banda:** aceleração lenta (média sofre, sustentado não), taxa constante, rajada (pico), medição curta, nada transferido, leitura ao vivo.
- **Servidor HTTP local de verdade** (sem internet): download com leituras ao vivo, upload (bytes conferem com o que o servidor recebeu), porta recusada, HTTP 500, **cancelamento em < 5 s**.
- **Ping:** mínimo/mediana/jitter, número par de amostras, uma amostra, vazio; leitura do `cf-ray`; servidor caído/erro → `null` sem exceção; cancelamento.
- **Traceroute real em loopback**, entradas inválidas (vazia, espaços, 300 caracteres, nome inexistente `.invalid`), cancelamento, corte dos saltos depois do destino, **8 cenários da análise**.
- **Contabilidade por processo:** taxas, totais, ordenação, ignora zero/negativo, 8 threads × 10 000 eventos sem perder bytes, teto de 1000 entradas.
- **Integrado, elevado, `MainWindow` real:** traceroute loopback e internet (10 saltos até 1.1.1.1), cancelar, host inválido; monitor ETW com **tráfego real gerado pelo próprio teste: o processo mediu 250,7 MB contra 249 MB baixados**; sair da página encerra a sessão ETW (confirmado no Windows); sessão órfã homônima não impede iniciar; `PARAR` não deixa sessão; `NetworkTest` completo com internet (download 589 Mbps sustentado, upload 210 Mbps, ping, jitter, veredito).

Total: **115 testes automatizados passando, 0 falhas** (Vault 23, PDF 38, migração de pasta 5, rede 49: banda 12, ping 10, traceroute 19, consumo por processo 8).

## 4. Bugs reais encontrados e corrigidos

| # | Problema | Correção |
|---|---|---|
| R1 | **O app caía** com `NullReferenceException` dentro do `LibreHardwareMonitor` (`GenericCpu.Update`), lançada no temporizador de sensores a cada segundo. O app **não tem tratador global** de exceções. **Aconteceu também com o app elevado** (e com PawnIO instalado): minha conclusão anterior de que era só artefato de teste sem administrador estava **errada**. | `SensorMonitor.Walk` captura a falha da biblioteca/driver por hardware, mantém os últimos valores, continua com os demais e expõe `UpdateFailures`/`LastUpdateError`. Na execução final: 0 falhas de leitura, app sobreviveu. É intermitente: o teste prova que sobrevive, não que a causa do NRE sumiu. |
| R2 | Teste de banda sem resultado e **sem motivo** quando o servidor não respondia dentro do tempo (firewall que descarta em vez de recusar). O usuário via só "—". | Passa a reportar "o servidor não respondeu a tempo (bloqueado por firewall/proxy ou sem internet)"; falhas HTTP/rede também aparecem na mensagem do veredito. |
| R3 | O `NetworkTest` original engolia `HttpRequestException` dos workers, então a mensagem "não foi possível medir" quase nunca aparecia. | O primeiro erro de rede é capturado e mostrado. |

## 5. Assincronia, memória e recursos

- Banda, ping e traceroute são `Task` canceláveis; `HttpClient` estático e único para a banda; `Ping` em `using` e um por salto (a classe não é segura em paralelo).
- Progresso do traceroute por `Progress<T>` criado na thread da interface (sem `Dispatcher` manual); o monitor de consumo é lido por `DispatcherTimer` de 1 s **só enquanto ligado**.
- Sessão ETW: nome fixo, `StopOnDispose`, encerrada ao parar, ao sair da página e ao fechar o app; sessão órfã de execução anterior é encerrada ao iniciar. A thread do ETW termina sozinha se o Windows derrubar a sessão e a tela avisa.
- Contabilidade sob `lock` (thread do ETW × interface), com teto de 1000 entradas (o mais antigo é descartado).
- Handlers `async void` capturam todas as exceções (nada escapa e derruba o app).

## 6. Limitações conhecidas

| # | Limitação |
|---|---|
| L1 | O consumo por processo é **estimativa em tempo real**: sob carga muito alta o ETW pode descartar eventos; inclui tráfego local (loopback) quando o provedor o reporta. |
| L2 | Só mostra o tráfego **a partir do momento em que o monitoramento é ligado** (não há histórico). |
| L3 | Roteadores que não respondem ao TTL expirado aparecem como `*` (comum em provedores: no teste real, 8 de 10 saltos); a análise não trata isso como falha. |
| L4 | Traceroute usa ICMP: redes que bloqueiam ICMP mostram o caminho incompleto. Não há geolocalização dos saltos (de propósito: evitaria enviar IPs do usuário a terceiros). |
| L5 | Não há seletor de duração (5/10/20 s) nem de servidor; teste fixo de 8 s (download) e 6 s (upload). O resultado do traceroute não entra no relatório exportado (a banda e o ping, sim, via `NetworkTest`). |
| L6 | Testado em um único PC (Ethernet 1 Gbps, Windows 10 19045). Não testado em Wi-Fi, IPv6, conexão lenta/instável nem com proxy. |
