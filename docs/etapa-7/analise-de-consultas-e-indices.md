# Entrega 7C — matriz de consultas e índices

Baseline inspecionada antes das alterações: main@9db921e996d364654bd1ba3aee17af54e1229c06, working tree limpo e staging vazio. 7B publicada, CI #14 / 45925546392 Success e 858/858 externos, conforme confirmação do usuário. Nenhum banco persistente foi acessado. Esta matriz foi construída antes de alterar código de produção.

## Matriz inicial

Em todas as rotas o proprietário é obtido do JWT, não da query. Paginação existente: page=1, pageSize=20, máximo 100, deslocamento validado em long contra int.MaxValue. Desempates de IDs usam a ordem uniqueidentifier do SQL Server. Totais são do conjunto filtrado, não da página.

| GET sob /api/v1 | Filtros | Ordenação padrão / alternativas / desempate | Paginação e totais | Validação e isolamento | Consistência inicial | Cobertura existente | Índices relacionados | Classificação inicial |
|---|---|---|---|---|---|---|---|---|
| accounts | isActive, type | name asc; name/id, asc/desc; id asc no empate de nome | SQL count + página | tipos fechados, owner no SQL; inclui inativas; política legada de query/model binding | Dois comandos independentes | FinancialAccountsSqlTests; FinancialAccountsServiceTests | UserId/IsActive, PK/AK | **Defeito de consistência**: uma criação entre count e página pode misturar estados; demais regras atendidas |
| categories | isActive, kind | name asc; name/id, asc/desc; id asc no empate | SQL count + página | tipos fechados; query whitelist/duplicados/vazios; owner; inclui inativas | Dois comandos independentes | CategoriesSqlTests; CategoriesServiceTests | único UserId/Kind/NormalizedName, PK/AK | **Defeito de consistência** entre count e página; demais regras atendidas |
| transactions | accountId, categoryId, kind, from/to opcionais, minAmount/maxAmount | occurredOn desc; occurredOn/amount/id, asc/desc; id asc no empate | SQL count + página após filtros | referências próprias, decimal exato, datas opcionais sem limite de 366; exclui IsDeleted | 2–4 comandos independentes | TransactionsSqlTests; FinancialReadValidationSqlTests; FinancialTransactionsServiceTests | UserId/AccountId/OccurredOn/Id; UserId/CategoryId/OccurredOn; UserId/OccurredOn/Id | **Defeito de consistência** entre referência/count/itens; demais filtros atendidos |
| transfers | accountId como origem OU destino, from/to opcionais | occurredOn desc; occurredOn/amount/id, asc/desc; id asc no empate | SQL count + página; uma linha por operação | owner, conta própria, datas opcionais sem limite de 366; exclui IsCanceled | Snapshot, 2–3 SELECTs | TransferListingSqlTests; StatementConsistencySqlTests | UserId/SourceAccountId/OccurredOn/Id e UserId/DestinationAccountId/OccurredOn/Id | Atendido; OR/ordenação exige medição, não implica índice novo |
| accounts/{id}/balance | asOf opcional (hoje financeiro) | Não aplicável | Conta + agregados num SELECT | owner, corte inclusivo, zero antes da abertura, centavos e históricos inativos | Comando consolidado, sem promessa de visão entre requisições | FinancialReconciliationSqlTests; ReportsSqlTests | PK/AK da conta; índices de movimentos acima | Atendido; agregados correlacionados exigem medição de escala |
| accounts/{id}/statement | from/to obrigatórios, page/pageSize | occurredOn desc, OperationId asc, OperationType asc | 3 SELECTs; totais/abertura/fechamento de todo período | 366 datas inclusivas, calendário financeiro, owner, conta inativa permitida | Snapshot | AccountStatementSqlTests; StatementConsistencySqlTests; FinancialReadValidationSqlTests | Índices de movimentos por owner/conta/data | Atendido; UNION ALL/OR e histórico anterior exigem medição |
| reports/summary | from/to obrigatórios, accountId opcional | Não aplicável | Uma agregação condicional; duas leituras se filtra conta | 366 datas, owner, conta inativa permitida; kind desconhecido | Snapshot | ReportsServiceTests; ReportsSqlTests; ReportConsistencySqlTests; ReportFailureSqlTests | UserId/OccurredOn/Id; UserId/AccountId/OccurredOn/Id | Atendido |
| reports/by-category | from/to obrigatórios, accountId/kind opcionais | kind asc, total desc, categoryId asc, fixa | Coleção completa, sem paginação ou categorias vazias | 366 datas, owner e JOIN Id/UserId/Kind, nome atual | Snapshot | CategoryReportsServiceTests; CategoryReportsSqlTests; CategoryReportConsistencySqlTests; CategoryReportFailureSqlTests | Índices de transactions; PK/AK/índice único Categories | Atendido; agrupamento/ordenação exige medição |

## Limite das alterações autorizadas por este diagnóstico

1. Corrigir exclusivamente a visão count/página das três listagens antigas, reutilizando SnapshotReadSession; preservar contratos HTTP, filtros e ordenação. Falhas conhecidas encapsuladas precisam continuar em 503, sem capturar cancelamentos ou erros inesperados.
2. Completar a cobertura de reconciliação **conjunta** (ambas as contas, listas e relatórios em cada evolução), de limite extremo de paginação e de número de comandos. Os casos individuais já cobertos não serão reimplementados.
3. Medir consultas/índices externamente em recursos descartáveis. Docker no sandbox está inacessível (config.json/pipe com acesso negado); nenhuma métrica real é alegada. Não alterar índices com base apenas no LINQ.

As diferenças contratuais de query entre accounts e endpoints posteriores são históricas. Não se aplica uma whitelist nova a accounts ou novas regras de casing/espaços apenas por uniformização. A 7C não adiciona endpoints, sort a relatórios, paginação por categoria ou limite de 366 dias a listagens opcionais.

## Índices e critérios de medição

Os índices listados são os explícitos do modelo e migrations; o catálogo externo deve incluir também PKs, AKs e índices de FKs criados por convenção. Não há colunas INCLUDE adicionadas preventivamente. Projeções de DTOs podem exigir lookups; nome/valor e ordenação por campos não líderes podem demandar sort; conta de origem/destino usa OR. São **riscos para medição**, não gargalos demonstrados. Scans em coleções pequenas ou agrupamentos amplos podem ser adequados; seeks com muitos lookups podem não ser.

Não se afirma seletividade, plano escolhido, leituras lógicas, CPU, duração ou ganho percentual sem execução real. Não se limpam caches globais nem se impõe tempo rígido na suíte. Qualquer proposta estrutural dependerá de plano/métricas e autorização adicional, com análise do custo de escrita/armazenamento e rollback. Sem evidência, os índices ficam mantidos.

A inspeção offline do modelo e das migrations também confirmou os índices de relacionamento **FinancialTransactions(AccountId,UserId)**, **FinancialTransactions(CategoryId,UserId,Kind)**, **Transfers(SourceAccountId,UserId)** e **Transfers(DestinationAccountId,UserId)**. PKs e chaves alternativas estão registradas em model-keys.json. Eles não são declarados redundantes apenas porque existem índices líderes por UserId: suportam relações e acessos com outra ordem de chaves. Nenhum índice foi removido. O catálogo físico real continua pendente do procedimento externo.

## Resultado da consolidação local

As três lacunas de count/página foram corrigidas em FinancialAccountStore, CategoryStore e FinancialTransactionStore: a leitura agora usa SnapshotReadSession, com a conexão física preservada até a restauração de ReadCommitted. As funções de filtro/projeção/ordenação/paginação foram mantidas. Não há transação de escrita nova, alteração de contrato ou mudança de schema.

SnapshotReadFailure ganhou apenas acesso à SqlException conhecida já identificada pela mesma política 1205/-2/3952. Nos contratos de páginas de contas/categorias, que não possuem resultado de erro no store, ExceptionDispatchInfo propaga a causa SQL original ao handler DbException/503, inclusive sob wrappers conhecidos do EF. Transações retornam TransactionFailure.Unavailable, como os outros resultados financeiros. Cancelamentos e falhas inesperadas não entram nesse filtro. SnapshotReadSession e sua limpeza não foram alterados.

Os novos testes posicionam a escrita real **depois da contagem e antes da página**, não apenas antes de iniciar a consulta. Exigem count=1 e um item na primeira resposta, count=2 e dois itens na seguinte. A baseline sem Snapshot permite o interleaving count=1/itens=2; essa é a evidência estática do defeito. A reprodução SQL automatizada está preparada, mas não foi executada no sandbox por bloqueio Docker; não se afirma aprovação real dessa regressão antes da validação externa.

| Consulta após a correção | Limite de SELECTs financeiros no teste | Comando de restauração da sessão |
|---|---:|---:|
| accounts | 2 | 1 |
| categories | 2 | 1 |
| transactions com conta/categoria | 4 | 1 |
| transfers com conta | 3 | 1 |
| balance | 1 | 0 |
| statement | 3 | 1 |
| summary com conta | 2 | 1 |
| by-category com conta | 2 | 1 |

São limites superiores, não exigência artificial de um comando único ou proibição de futura consolidação. JWT é contado separadamente; begin/commit não são confundidos com SELECT; SET READ COMMITTED é manutenção da sessão. O teste detecta N+1 e consultas adicionais sem contar leituras de autenticação como consultas financeiras.

## Inspeção offline e catálogo

Executado com sucesso, sem conexão SQL:

```powershell
dotnet run --project tools/FinanceFlow.QueryAnalysis/FinanceFlow.QueryAnalysis.csproj --configuration Release --no-build --no-restore -- --offline .local/7c-performance-results/offline
```

Foram gerados accounts.sql, categories.sql, transactions.sql, transfers.sql, balance.sql, statement.sql, summary.sql e by-category.sql, mais model-indexes.json e model-keys.json. As projeções de listas reutilizam as expressões reais dos stores; os LINQs diagnósticos reproduzem formas representativas dos filtros e agregados. **Não são execução dos stores, catálogo SQL real ou planos.** O modo de medição captura os comandos reais, inclusive propriedade, agregados e página do extrato.

Conferidos na tradução: UserId parametrizado, tipos date/uniqueidentifier/decimal compatíveis, OFFSET/FETCH e ORDER BY no servidor, SUM/CASE, JOIN composto da categoria e UNION ALL do extrato. Não há função sobre OccurredOn nos predicados de intervalo, SQL com campo arbitrário do cliente, lazy loading ou materialização de histórico individual para agrupar. OR de origem/destino e sorts por nome/valor continuam pontos para examinar no plano. Precisão e escala não foram alteradas; somas monetárias permanecem separadas onde isso evita redução de escala no SQL.

## Procedimento externo de medição

```powershell
& C:\FinanceFlow\scripts\Test-QueryPerformance.ps1
```

O script cria uma rede e dois containers **exclusivos**, com nomes financeflow-7c-... aleatórios: SQL Server 2022 na mesma imagem fixada pela fixture e SDK .NET 10. Não utiliza Compose, containers existentes, portas/volumes persistentes ou banco local. O código fonte é montado somente para leitura e copiado para /work. Senhas efêmeras são geradas criptograficamente, passadas por ambiente sem aparecer nos argumentos/logs e restauradas no host ao terminar.

O projeto diagnóstico é independente da solução da API e referencia somente Infrastructure; **não adiciona pacotes**. Provisionamento/migrations são explícitos em bases FinanceFlowPerf_GUID desse SQL descartável. As consultas dos stores usam a identidade SQL restrita da API; a identidade administrativa é usada somente na preparação, catálogo e replay diagnóstico de SHOWPLAN. Não são concedidos SHOWPLAN ou privilégios extras à API.

Escalas preparadas, ainda não medidas:

| Escala | FinancialTransactions | Transfers | Proprietários | Contas | Categorias |
|---|---:|---:|---:|---:|---:|
| Pequena | 200 | 50 | 2 | 8 | 24 |
| Maior | 40000 | 10000 | 2 | 8 | 24 |

Distribuição determinística em 01/10–26/11/2026, centavos exatos, múltiplas contas/categorias/naturezas, linhas excluídas/canceladas e histórico inativo. Bulk copy é apenas preparação sintética administrativa, com CHECKs/FKs habilitados; não altera a política HTTP de novos vínculos inativos. As contagens e distribuição **efetivamente inseridas** são consultadas no SQL e registradas em distribution.json. environment.json exige Snapshot ON/RCSI OFF e registra versões/condições; indexes.json registra catálogo real, PK/AK, chaves e colunas incluídas.

Cada consulta possui dois cenários: sem filtro adicional e com filtro explícito. Em contas/categorias são atividade/tipo; transações adicionam conta/kind/min/max e ordenação por valor; transferências filtram origem OU destino. Saldo compara cortes e extrato compara intervalos (não existem consultas globais desses recursos). Summary/by-category usam conta e, quando previsto, kind. Parâmetros concretos ficam nos artefatos.

São dois aquecimentos e cinco repetições por cenário/escala, sem limpeza de cache global. JSONs registram tempos end-to-end do store, número de SELECTs financeiros, resets e SQL/parametrização reais; não incluem HTTP/JWT. O replay de cada comando com os mesmos parâmetros/isola­mento coleta STATISTICS IO/TIME e XML de plano real, quando retornado, em arquivos separados. O replay administrativo de plano não é apresentado como latência HTTP ou benchmark da credencial restrita. Não existe limiar temporal como asserção funcional.

Tudo fica em **.local/7c-performance-results/EXECUCAO**: logs, exit codes, distribuições, catálogo, comandos, métricas e .sqlplan. O SDK é inspecionado para obter seu exit code real; limpeza no finally remove **somente os containers/rede criados com sucesso pelo procedimento**, sem volumes ou enumeração de ambientes existentes, e registra falhas de limpeza. Nenhuma limpeza indiscriminada de pools/caches é feita.

**Medições reais, planos, leituras lógicas, CPU e conclusões de desempenho estão pendentes da execução externa.** O Docker permaneceu inacessível ao sandbox. A análise de desempenho não está concluída; os índices ficam mantidos e nenhuma proposta estrutural é implementada. Os artefatos externos deverão ser examinados antes do fechamento oficial da 7C.

## Medição externa completa e decisão final — 27/11/2026

Os registros anteriores descrevem a preparação e suas pendências naquele momento. A execução **20261127-012104-056-56f46de6**, realizada pelo usuário, concluiu as duas escalas. A análise abaixo usa seus artefatos reais, não a tradução SQL offline ou a tentativa parcial anterior.

### Integridade e origem das métricas

Foram lidos **190 arquivos**: **83 por escala**, mais 24 de controle/logs. Há **32 resultados de cenário**, **160 amostras temporais**, **64 arquivos de estatísticas não vazios** e **64 planos XML válidos com RunTimeCountersPerThread**. completion.json exige success=true, escalas 200/40000, 32 cenários, dois aquecimentos e cinco repetições. Os 12 códigos de saída registrados (cópia, extração, restore, build, medição, attach, inspect, container, procedimento e três limpezas) são **zero**; SDK terminou exited/0, sem OOM. Os logs confirmam os 16 cenários de cada escala, sem etapas silenciosamente omitidas.

As distribuições SQL efetivas somam **200 transações + 50 transferências = 250 movimentos** e **40000 + 10000 = 50000**. Cada base contém dois proprietários, oito contas e 24 categorias. Foram registrados **20/4000 transações excluídas** e **7/1250 transferências canceladas**, respectivamente. Dados financeiros cobrem 01/10–26/11/2026. SQL Server **16.00.4230**, runtime **10.0.15**, EF assembly **10.0.13.0**, SqlClient assembly **6.0.0.0**; esta última é versão de assembly, não uma alegação de versão de pacote. Snapshot está ON e RCSI OFF em ambas as bases. Hardware/quotas de CPU/RAM do Docker não foram registrados.

Convenções da tabela:

- **Store min/med/max (ms):** mínimo, mediana e máximo das cinco amostras por cenário, obtidas com Stopwatch ao executar os stores usando o login restrito. Incluem coordenação EF/conexão/transação/limpeza, mas **não HTTP/JWT**. A mediana é a terceira amostra ordenada; não se estimaram percentis. Apenas a apresentação foi arredondada a três casas; JSONs preservam as amostras originais.
- **Leituras lógicas:** soma das linhas de STATISTICS IO dos comandos do cenário. São acessos a páginas, podendo contar releituras da mesma página; não páginas distintas ou linhas de transações. **CPU/decorrido SQL (ms):** soma dos eventos SQL Server Execution Times desses comandos em **um replay diagnóstico por comando**, excluindo parse/compile. Não são medianas das cinco amostras nem soma de CPU inclusiva de todos os operadores do plano. Eventos SET com zero não alteram os totais.
- O replay usa login administrativo exclusivamente para SHOWPLAN/STATISTICS, SQL/parâmetros capturados e o isolamento registrado. Seus tempos/planos não são apresentados como medição da credencial da API nem latência HTTP. Não se subtraem esses tempos do tempo do store para inferir overhead.
- **SELECTs** inclui verificações de propriedade, contagem/agregados e página; o total foi constante nas cinco amostras e nas duas escalas. Há um SET READ COMMITTED por leitura Snapshot; balance tem zero. Begin/commit e autenticação não entram nesse número.
- **Linhas raiz 250/50000:** soma observada no nó raiz de cada SELECT, na ordem dos comandos. **ND** significa que o nó raiz não publica contador de linhas; não foi substituído por estimativa ou pelo valor de COUNT. Não representa quantidade de movimentos que satisfazem o filtro.

| Cenário | Store min / med / max ms, 250 | Store min / med / max ms, 50000 | Leituras lógicas 250 / 50000 | CPU SQL ms 250 / 50000 | Decorrido SQL ms 250 / 50000 | SELECTs | Linhas raiz 250 / 50000 |
|---|---|---|---|---|---|---:|---|
| accounts-filtered | 4.056 / 4.542 / 4.823 | 2.721 / 2.977 / 3.385 | 4 / 4 | 1 / 0 | 0 / 0 | 2 | ND+2 / ND+2 |
| accounts-unfiltered | 3.015 / 3.147 / 3.624 | 2.508 / 2.669 / 3.335 | 4 / 4 | 1 / 0 | 0 / 0 | 2 | ND+4 / ND+4 |
| balance-filtered | 2.059 / 2.447 / 3.565 | 8.399 / 8.722 / 10.322 | 20 / 1846 | 0 / 6 | 2 / 8 | 1 | ND / ND |
| balance-unfiltered | 2.452 / 2.677 / 3.121 | 9.252 / 9.709 / 10.620 | 20 / 1846 | 3 / 8 | 2 / 9 | 1 | ND / ND |
| by-category-filtered | 4.021 / 4.741 / 4.938 | 7.218 / 7.354 / 7.631 | 18 / 761 | 0 / 4 | 0 / 5 | 2 | ND+4 / ND+6 |
| by-category-unfiltered | 2.526 / 3.132 / 3.873 | 8.179 / 8.427 / 16.318 | 104 / 771 | 0 / 8 | 1 / 6 | 1 | 10 / 12 |
| categories-filtered | 3.472 / 4.295 / 5.673 | 2.962 / 3.195 / 3.465 | 4 / 4 | 1 / 1 | 0 / 0 | 2 | ND+5 / ND+5 |
| categories-unfiltered | 6.029 / 6.216 / 7.280 | 2.336 / 2.610 / 3.385 | 4 / 4 | 0 / 0 | 0 / 0 | 2 | ND+12 / ND+12 |
| statement-filtered | 6.288 / 6.770 / 7.028 | 20.584 / 21.516 / 21.671 | 20 / 1846 | 1 / 17 | 2 / 18 | 3 | 1+ND+11 / 1+ND+20 |
| statement-unfiltered | 5.736 / 6.524 / 7.184 | 22.580 / 23.211 / 24.834 | 20 / 1846 | 6 / 22 | 2 / 20 | 3 | 1+ND+20 / 1+ND+20 |
| summary-filtered | 3.787 / 4.630 / 7.112 | 7.227 / 7.487 / 7.593 | 8 / 749 | 0 / 3 | 0 / 5 | 2 | ND+ND / ND+ND |
| summary-unfiltered | 2.619 / 3.086 / 3.216 | 7.342 / 7.389 / 7.688 | 6 / 747 | 0 / 6 | 0 / 6 | 1 | ND / ND |
| transactions-filtered | 5.141 / 5.487 / 6.474 | 11.308 / 11.921 / 13.698 | 14 / 1496 | 0 / 7 | 0 / 9 | 3 | ND+ND+3 / ND+ND+20 |
| transactions-unfiltered | 4.937 / 5.518 / 7.315 | 14.997 / 16.986 / 19.209 | 12 / 1494 | 1 / 9 | 0 / 12 | 2 | ND+20 / ND+20 |
| transfers-filtered | 4.323 / 4.760 / 5.129 | 5.632 / 5.870 / 6.150 | 8 / 352 | 0 / 2 | 1 / 3 | 3 | ND+ND+2 / ND+ND+20 |
| transfers-unfiltered | 3.860 / 5.361 / 6.260 | 4.682 / 5.162 / 5.297 | 6 / 350 | 1 / 0 | 1 / 3 | 2 | ND+6 / ND+20 |

**Leituras físicas: zero em todos os replays.** O ensaio é de dados aquecidos/cache, não de I/O frio. CPU/decorrido SQL têm resolução inteira em ms; zero não significa ausência de trabalho. Os arquivos brutos continuam ignorados em .local, sem serem versionados.

### Interpretação dos cenários

As maiores medianas na escala de 50000 foram **statement-unfiltered 23,211 ms**, **statement-filtered 21,516 ms** e **transactions-unfiltered 16,986 ms**. Os maiores valores das cinco amostras foram, respectivamente, **24,834**, **21,671** e **19,210 ms**. O by-category sem filtro teve máximo **16,318 ms**, mas mediana **8,427 ms**; essa dispersão não foi ocultada por uma única média.

O extrato usa três SELECTs e soma **1846 leituras lógicas** no replay grande, tanto com intervalo amplo quanto restrito. Saldo usa um comando, mas seu plano diagnóstico contém **duas varreduras de FinancialTransactions e duas de Transfers**, também somando **1846 leituras** com a consulta da conta. É amplificação interna ao plano de agregados correlacionados, **não N+1 ou quatro requisições SQL independentes**. O resultado e as regras do saldo foram preservados.

Transações filtradas leem **1496 páginas**, próximas das **1494 sem filtro**, embora retornem subconjuntos distintos: a verificação de conta acrescenta duas leituras e os dois comandos financeiros ainda varrem o clustered. A mediana cai de 16,986 para 11,921 ms, mas os filtros e a ordenação também diferem; isso **não é ganho de índice medido**. Transferências permanecem em 350/352 leituras; summary em 747/749 e by-category em 771/761 no ensaio grande.

O nome filtered tem significado específico: contas usa atividade/tipo; categorias usa atividade/kind; transações usa conta, kind=expense, intervalo, minAmount=1/maxAmount=50 e amount asc; transferências usa conta como origem OU destino. Summary filtra conta, by-category conta/kind. Saldo compara cortes 01/11 e 26/11 da mesma conta; extrato compara 01/11–26/11 com 01/10–26/11. Não se chama balance-unfiltered de saldo global. **CategoryId não foi filtro do benchmark de transações**; a combinação conta/categoria é coberta funcionalmente, sem alegação de medição dessa variante.

Contas/categorias têm o mesmo número de linhas nas duas escalas: quatro/12 por proprietário. Suas pequenas diferenças de tempo não demonstram ganho com aumento de movimentos. O resultado financeiro não é calculado a partir de tempos, contagens físicas ou respostas HTTP independentes durante escrita concorrente.

### Planos reais e catálogo físico

O catálogo de cada base contém **17 definições**: quatro PKs, duas chaves alternativas e os 11 outros índices financeiros previstos pelo modelo/migrations, incluindo unicidade de categoria e índices de FKs. Não há INCLUDE explícito nos registros coletados. Os índices UserId/AccountId/OccurredOn/Id, UserId/CategoryId/OccurredOn e UserId/OccurredOn/Id, os dois de direção de transferência e os cadastros estão presentes. Não se presume ausência de custo ou redundância só pela ordem semelhante das chaves.

Nos **planos reais do replay administrativo**, observaram-se:

- Index Seek/Clustered Index Seek de contas/categorias e **Clustered Index Scan** de movimentos, além de Sort, Stream Aggregate, Nested Loops e Concatenation. Não foram observados scans de heap/Table Scan. By-category grande usa Hash Match para agrupar.
- **Um Key Lookup** no by-category sem filtro da escala 200, associado a IX_FinancialTransactions_UserId_CategoryId_OccurredOn e PK_FinancialTransactions. Na escala maior esse cenário muda para clustered scan/hash. Isso demonstra por que seek não equivale automaticamente a menos trabalho, nem scan a defeito.
- Predicados residuais nos scans de movimento para proprietário, exclusão/cancelamento, conta, natureza, datas e valores conforme a consulta. Os parâmetros financeiros são uniqueidentifier/date/varchar/decimal, não concatenação arbitrária do cliente.
- Conversões implícitas para bigint em OFFSET, int em contagens, varchar em parâmetros e varchar/nvarchar nas naturezas projetadas do UNION. **Nenhum PlanAffectingConvert registrado.** Não se identificou conversão de data de coluna impedindo seu predicado de intervalo; o gargalo de cobertura não é atribuído genericamente a toda conversão.
- **Nenhum nó Warnings ou de spill encontrado nos 64 XMLs**. Essa ausência é limitada aos planos/execuções coletados; não prova ausência de spills em cargas futuras. Não se inferiu uso de tempdb ou falta de pressão de memória fora do ensaio.
- Há estimativas diferentes dos retornos reais: no scan de transactions-filtered/page, estimativa **2590,41**, retorno **447**, linhas lidas **40000**; summary-unfiltered estima **12561**, retorna **7296**, lê **40000**. O agregado do extrato estima **3443,62** transações e retorna **4000**, com leitura de 40000; Transfers estima **1241,35** e retorna **1250**, lendo 10000. São contadores dos operadores, não número de itens da página. Não se afirma cardinalidade perfeita ou estabilidade de planos.

Os planos grandes contêm sugestões de índices para UserId/IsDeleted/OccurredOn, variantes com AccountId/Kind/Amount e INCLUDE de valores/categoria/payload; Transfers sugere UserId/IsCanceled/OccurredOn com direções incluídas. Por exemplo, o plano de summary filtrado mostra impacto estimado **94,4372**, e transactions-filtered/page **47,4123**. Esses valores são **estimativas do otimizador, não percentuais de ganho observado**. Nenhum DDL foi executado a partir delas.

### Decisão sobre otimizações

| Tema | Evidência observada | Classificação e decisão |
|---|---|---|
| Contas/categorias | Quatro/12 linhas por proprietário, quatro leituras lógicas por cenário; scans/sorts pequenos | **Nenhuma alteração necessária** nos cenários avaliados |
| Cobertura de transações/agregados | Scans de 40000 linhas, 747 páginas por scan e sugestões de INCLUDE | **Melhoria futura não bloqueante**; manter os índices na 7C, sem comparação A/B ou SLO definido que justifique mudança imediata |
| Ordenação por amount e paginação | Sort e scans em listagem filtrada; apenas primeira página foi medida | **Melhoria futura não bloqueante**; medir distribuição/páginas profundas antes de escolher ordem de chaves |
| OR de origem/destino | Scans de Transfers, 350/352 leituras por cenário grande | **Melhoria futura não bloqueante**; não reescrever UNION/OR ou duplicar índices sem experimento |
| Saldo e extrato | 1846 leituras, agregados repetidos no plano de saldo; extrato é o maior tempo observado | **Melhoria futura não bloqueante** de cobertura/formato da consulta; não é defeito financeiro demonstrado |
| Hash/Sort/memória | Sem warnings/spills reportados no ensaio | **Nenhuma intervenção imediata demonstrada**; observar outras distribuições/concorrência |

**Recomendação para esta entrega: manter os índices atuais.** Não foi demonstrado gargalo bloqueante nas escalas coletadas. Isso não declara a estrutura ótima para toda carga nem dispensa medições futuras.

Se for autorizado um experimento posterior, um candidato **não aprovado para implantação** seria avaliar cobertura do índice atual de transações **(UserId,AccountId,OccurredOn,Id)** com **INCLUDE(IsDeleted,Kind,Amount)** para agregados por conta. O benefício esperado é reduzir leituras/lookups de agregação, **a medir**, não um percentual prometido. É insuficiente para todos os filtros globais, por categoria e ordenação amount; não se recomenda adicionar em paralelo sem avaliar a sobreposição com o índice atual.

Custos: cópia adicional de valores, armazenamento/cache e manutenção em INSERT/UPDATE; incluir Description/RowVersion indiscriminadamente aumentaria amplificação de escrita. O experimento exigiria workload representativo, métricas comparativas de leitura **e escrita**, reconciliação/concorrência preservadas e autorização adicional antes de DDL. Qualquer adoção usaria migration incremental, sem editar históricas; rollback restauraria a definição anterior (ou removeria só o experimental mantendo o atual). **Nenhuma mudança estrutural é necessária ou autorizada por este parecer.**

### Limitações e fechamento da matriz

São duas bases sintéticas, dois usuários, baixa cardinalidade de contas/categorias, 57 datas, descrições nulas e exclusões/cancelamentos correlacionados ao proprietário. O benchmark consulta o primeiro proprietário; o segundo fornece dados alheios no conjunto, sem simular tráfego concorrente de dois usuários. Não foram medidos tráfego concorrente, I/O frio, payload longo, páginas profundas, filtro CategoryId do benchmark ou todas as combinações de filtros. CPU/RAM do host não foram coletados. Cinco amostras aquecidas não sustentam p95/p99, capacidade máxima, requests/s ou extrapolação para produção. Os planos são **replays administrativos reais**, não captura garantida do plano em cache de cada execução restrita da API.

A matriz final está **atendida no escopo aprovado**: os cinco endpoints já consistentes foram preservados; accounts/categories/transactions tiveram a lacuna de count/página resolvida e seus três testes concorrentes passaram. Filtros publicados, ordenação com desempate, páginas vazias/finais/extremas, totais, intervalos/366 datas onde aplicável, histórico inativo, exclusões/cancelamentos e proprietário permanecem cobertos pelos **858 anteriores + 29 novos**, todos aprovados externamente. Relatórios não incluem patrimônio/transferências; reconciliação financeira é independente e em dados estáveis. Não há N+1 conhecido nas consultas medidas e os oito limites de comandos passaram na suíte.

**Análise técnica de desempenho concluída para estes artefatos/cenários, sem achado bloqueante.** As limitações e oportunidades futuras acima permanecem explícitas. A 7C está **aprovada para commit**; encerramento oficial da Etapa 7 depende de publicação autorizada e CI correspondente. Etapa 8 não iniciada. Não foram alterados código, testes, scripts, índices, migrations ou permissões nesta auditoria.
