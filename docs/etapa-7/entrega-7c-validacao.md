# Entrega 7C — Consolidação de consultas, filtros e desempenho

## Baseline e limites

Baseline confirmada antes das alterações: **main@9db921e996d364654bd1ba3aee17af54e1229c06**, `feat: implement financial reports by category`, working tree limpo e staging vazio. A 7B foi publicada, com **CI #14 / 45925546392 Success**, conforme confirmação do usuário. Sua primeira execução externa `.local/7b-docker-results/20261126-222947-267-8d877f` aprovou **858/858**: Domain 116, Application 189, Integration 553. São evidências externas anteriores, não resultados executados pela 7C no sandbox. O histórico das 7A/7B permanece preservado.

Escopo: revisão dos oito GETs financeiros existentes, filtros/ordenação/paginação/totais, reconciliação conjunta, consistência, SQL e índices. Não há endpoint, relatório, funcionalidade da Etapa 8, dependency, migration, índice ou privilégio novo. Nenhuma alteração estrutural está sendo solicitada ou implementada sem autorização.

## Diagnóstico antes de alterar código

A [matriz de conformidade e análise de índices](analise-de-consultas-e-indices.md) foi escrita antes das alterações de produção. Relaciona filtros, ordenação/desempate, paginação/totais, validação, proprietário, consistência, testes e índices, com classificação por endpoint.

Foram identificadas **três lacunas do mesmo defeito de consistência**: accounts, categories e transactions faziam count e página como comandos ReadCommitted independentes. Uma criação confirmada entre eles poderia produzir totalCount=1 e dois itens. Transferências, extratos e relatórios já usavam Snapshot; saldo já consolidava conta/agregados em um SELECT. A proteção em long do deslocamento já existia nos cinco serviços paginados e foi preservada.

Filtros, campos/direções de ordenação, desempates por ID, exclusões, limites monetários e de datas estavam implementados. Contas/categorias incluem inativas. Relatórios têm até 366 datas inclusivas; listas de movimentos têm datas opcionais sem esse limite. A política legada de query de accounts difere da whitelist dos endpoints posteriores; não foi introduzida uma breaking change por uniformização estética.

Lacunas de cobertura delimitadas: interleaving count/página nas três listas, deslocamento extremo **válido**, limite de comandos das oito consultas e reconciliação de todos os modelos de leitura após uma sequência de operações históricas. Os testes anteriores por recurso foram reutilizados, sem repetir o conjunto P2 de pooling.

## Correções localizadas

| Problema | Evidência/causa | Correção | Regressão |
|---|---|---|---|
| Count e página podem refletir estados distintos | SELECTs separados sem transação/visão pontual nas três listas | SnapshotReadSession envolve referências/count/página, mantendo projeção/filtros/ordem | Três casos com escrita independente confirmada entre count e página |
| Wrappers EF de falhas SQL conhecidas devem manter 503 nas novas leituras Snapshot | Stores de contas/categorias retornam Page sem enum de falha; handler global entende DbException | Extrai somente a mesma causa SQL 1205/-2/3952 já reconhecida, propaga via ExceptionDispatchInfo; transactions retorna Unavailable | Timeout SQL real direto e encapsulado, erro inesperado e releitura válida |

SnapshotReadFailure apenas expõe a causa SQL já classificada; não amplia o conjunto reconhecido nem percorre wrappers arbitrários. Cancelamentos/erros inesperados não são convertidos em indisponibilidade. SnapshotReadSession **não foi alterado**: mesma conexão física, Snapshot, commit/dispose, restauração ReadCommitted antes do pool, limpeza sem token cancelado, timeout limitado, preservação da primeira falha e invalidação restrita quando necessária. Sem RCSI, ClearAllPools, retries ou fallback inconsistente.

Não foi feita otimização de índice ou de tempo de resposta sem medição. A correção responde ao requisito de consistência da resposta, não a um benchmark. Até a validação externa, a evidência do interleaving é estática e os testes SQL estão preparados/compilados, não aprovados pelo sandbox.

## Filtros, ordenação e reconciliação

Os contratos HTTP permanecem intactos. Contas/categorias ordenam name asc/id como padrão e aceitam somente os campos já publicados. Transações/transferências mantêm occurredOn desc, alternativas occurredOn/amount/id e desempate por id. By-category mantém kind asc/total desc/categoryId asc, sem paginação ou sort livre. Página além do fim continua 200 com metadados/totais corretos; deslocamento maior que int.MaxValue continua 400, antes do SQL.

O teste integrado usa contas 5000/2000, receita 4500, despesas 150/350 e transferência 1000, com relógio financeiro controlado. Um segundo proprietário possui dados no mesmo intervalo. Valores esperados são declarados independentemente dos endpoints:

| Estado | Income | Expense | Result | Nubank | Itaú | Soma das posições |
|---|---:|---:|---:|---:|---:|---:|
| Inicial | 4500 | 500 | 4000 | 8000 | 3000 | 11000 |
| Academia alterada para 200 | 4500 | 550 | 3950 | 7950 | 3000 | 10950 |
| Receita retroativa 250 no Itaú | 4750 | 550 | 4200 | 7950 | 3250 | 11200 |
| Exclusão lógica de Alimentação 350 | 4750 | 200 | 4550 | 8300 | 3250 | 11550 |
| Cancelamento da transferência | 4750 | 200 | 4550 | 9300 | 2250 | 11550 |
| Conta/categoria inativas; Salário renomeado | 4750 | 200 | 4550 | 9300 | 2250 | 11550 |

Em cada estado são conferidos summary, by-category, extratos das duas contas, saldos individuais, listas de transações/transferências e contagens. A página 99 do extrato fica vazia sem alterar totais/fechamento. Transferências têm efeitos opostos, não entram no resultado e seu cancelamento preserva a soma. Histórico inativo e nome atual permanecem corretos. A comparação entre requisições é feita **sem escrita concorrente** nessa reconciliação; não se afirma Snapshot compartilhado entre HTTPs independentes.

## SQL, índices e medição

Consultas/projeções, UserId, parametrização, AsNoTracking, GROUP BY/SUM, paginação/ordenação no SQL e ausência de lazy loading/N+1 foram revisados nos stores. Soma decimal e cálculo final verificado continuam os mesmos; não há arredondamento ou dinheiro em double/float. O saldo consolidado de duas contas é calculado somente no teste, sem endpoint novo.

A ferramenta `tools/FinanceFlow.QueryAnalysis` referencia Infrastructure, **sem PackageReference adicional**, e fica fora da solução funcional. Seu modo offline gerou SQL representativo para os oito recursos e metadados de índices/chaves, sem abrir conexão. Projeções de listas vêm das expressões reais dos stores; o modo medido captura os comandos reais da aplicação. Não se confunde tradução LINQ com execução/planos reais.

Os índices existentes foram mantidos. Predicados por usuário/conta/data têm índices compatíveis; OR de transferências, ordenação por nome/amount, lookups, agregações amplas e histórico anterior do extrato precisam de avaliação do plano/escala. Scan não é classificado automaticamente como defeito, nem seek como garantia de desempenho. **Nenhum gargalo ou ganho percentual foi inventado.**

O procedimento externo isolado está documentado na análise: duas escalas preparadas (**200/40000 transações e 50/10000 transferências**), dois proprietários, oito contas, 24 categorias, estados excluídos/cancelados, dois aquecimentos e cinco repetições. Captura distribuição e catálogo reais, SQL/parâmetros/contagem de comandos, tempos de store com credencial restrita e replay diagnóstico administrativo para STATISTICS IO/TIME/planos XML. Não limpa caches globais, não amplia grants e não mede autenticação/HTTP como se fossem SQL financeiro. **Medições e conclusões de desempenho continuam pendentes.**

## Testes

Preservados todos os **858 casos publicados**, sem alteração, remoção, skip ou enfraquecimento dos arquivos anteriores. Foram acrescentados **29 casos de Integration**, em dois arquivos:

- `QueryConsolidationSqlTests.cs`: três casos concorrentes de count/página; 12 de falhas (timeout -2 real direto, wrappers conhecidos EF e falha inesperada, para cada lista); cinco de paginação extrema válida/inválida; oito de limite de consultas, separando JWT e manutenção da sessão.
- `ConsolidatedReconciliationSqlTests.cs`: um cenário integrado com os seis estados financeiros acima e outro proprietário.

Os testes de comandos usam limites superiores razoáveis, permitindo futura consolidação sem autorizar N+1. Não exigem um SELECT único quando propriedade e consistência requerem mais leituras. Os testes concorrentes usam barreiras/interceptores, não sleeps. WAITFOR é usado exclusivamente para timeout SQL real, sem simular números de erro. Os wrappers são aplicados sobre essa SqlException real; asserções exigem SQL -2/HTTP 503, nenhuma página parcial e releitura válida. Falha inesperada exige 500. Testes SQL continuam em Testcontainers com a identidade restrita.

| Projeto | Baseline | Novos | Total descoberto |
|---|---:|---:|---:|
| Domain | 116 | 0 | 116 |
| Application | 189 | 0 | 189 |
| Integration | 553 | 29 | 582 |
| **Total** | **858** | **29** | **887** |

## Verificações efetivamente realizadas no sandbox

| Verificação | Resultado |
|---|---|
| Docker version | Acesso negado a config.json/pipe docker_engine; nenhum container persistente iniciado |
| Restore solução com NuGet.Config | Exit 0; cinco NU1900 |
| Build Release solução --no-restore | Exit 0, zero erros, cinco NU1900, sem warnings C#/analisadores |
| Restore/build ferramenta | Exit 0; dois NU1900; zero erros/warnings C#/analisadores no build |
| Ferramenta --offline | Exit 0; oito SQLs e metadados em .local/7c-performance-results/offline; nenhuma conexão SQL |
| Suíte completa --no-build --no-restore | **887 descobertos, 430 aprovados, 457 falhas de fixture Docker, zero ignorados; exit 2** |
| Domain / Application | 116 / 189 aprovados |
| Integration | 125 sem SQL aprovados; 457 casos bloqueados na construção de IdentitySqlFixture |
| Scripts PowerShell | Ambos analisados pelo parser, zero erros de sintaxe |
| Git | main@9db921e, staging vazio, git diff --check aprovado |

Todos os blocos de falha do runner contêm DockerUnavailableException na construção da fixture. Não são falhas funcionais comprovadas, e **nenhum teste SQL novo foi declarado aprovado**. Essa execução via dotnet test não produziu os XMLs externos de fechamento; a execução externa exigirá também zero erros do runner. Nenhuma proteção do Windows foi desabilitada e nenhum banco alternativo substituiu SQL Server.

NU1900 permanece registrado; auditoria de vulnerabilidades **inconclusiva**. A ausência de métricas SQL no sandbox não é apresentada como desempenho aprovado. Logs locais de restore/build/testes permanecem em .local e os históricos anteriores foram preservados.

## Arquivos e procedimentos de reprodução

Inventário final: **16 arquivos, sete modificados e nove novos**. As alterações versionáveis ficam restritas aos componentes abaixo; bin/obj e artefatos .local estão ignorados pelo Git.

Produção alterada somente em **FinancialAccountStore.cs, CategoryStore.cs, FinancialTransactionStore.cs e SnapshotReadFailure.cs**, pelas razões acima. Nenhum controller, serviço Application, contrato DTO, mapeamento, migration, DatabaseTasks, autenticação, idempotência, RowVersion, dependência ou workflow foi alterado.

Novos: os dois arquivos de testes; `tools/FinanceFlow.QueryAnalysis/{FinanceFlow.QueryAnalysis.csproj,Program.cs,SeedData.cs,OfflineSql.cs}`; `scripts/Test-QueryPerformance.ps1`; este documento e `analise-de-consultas-e-indices.md`. Atualizados também README principal, README da Etapa 7 e o script funcional, somente para caminhos/contagem 7c.

No PowerShell, executar separadamente:

```powershell
# Regressão funcional completa: três XMLs, 887/887, zero falhas/ignorados/erros do runner.
& C:\FinanceFlow\scripts\Test-DockerSuite.ps1

# Diagnóstico de desempenho: containers/rede próprios e bases descartáveis.
& C:\FinanceFlow\scripts\Test-QueryPerformance.ps1
```

Regressão: `.local/7c-docker-tests.log` e `.local/7c-docker-results/EXECUCAO`, com códigos de restore/build/módulos/Docker preservados. Desempenho: `.local/7c-performance-results/EXECUCAO`, com logs, amostras, catálogo, planos e códigos de saída/limpeza. O segundo procedimento não recebe connection string do usuário, não usa Compose e remove exclusivamente recursos que ele criou, sem remover volumes existentes. Migrations/provisionamento só são aplicados às bases desse container descartável.

## Pendências e parecer local

Faltam **887/887 externos**, revisão dos XMLs, execução/análise das medições reais, auditoria técnica final, publicação autorizada e CI da 7C. Se métricas indicarem necessidade estrutural, apresentar proposta separada com custo/benefício e rollback, sem executar alteração sem autorização. A auditoria NuGet conclusiva também permanece pendente.

Não houve stage, commit, push, reset, clean, acesso ao banco persistente, inicialização de containers persistentes, remoção de volumes, alteração de segredos ou proteção do Windows. **A 7C é implementação local, não está publicada nem aprovada pela CI; a Etapa 7 ainda não está oficialmente encerrada. Etapa 8 permanece pendente.**

**ENTREGA 7C IMPLEMENTADA — PRONTA PARA VALIDAÇÃO EXTERNA**, incluindo os dois procedimentos acima. A análise de desempenho não está concluída enquanto as medições externas não forem executadas e avaliadas.

## Validação funcional externa e correção da ferramenta — 27/11/2026

### Regressão funcional aprovada pelo usuário

A execução externa **20261127-004756-385-23c9ec** aprovou **887/887**. Os três XMLs e seis códigos de saída foram lidos nesta intervenção:

| Projeto | Executados/aprovados | Falhas | Ignorados | Erros do runner |
|---|---:|---:|---:|---:|
| Domain | 116/116 | 0 | 0 | 0 |
| Application | 189/189 | 0 | 0 | 0 |
| Integration | 582/582 | 0 | 0 | 0 |
| **Total** | **887/887** | **0** | **0** | **0** |

Restore, build, três módulos e Docker têm **saída zero**. É execução do usuário em Docker Linux/SQL Server, não teste SQL executado no sandbox. Resolve a pendência funcional da seção anterior; não conclui medição, auditoria final ou publicação/CI da 7C.

### Falha de medição investigada

Última tentativa real: **.local/7c-performance-results/20261127-010559-574-e85708f8**. O console original registra **`Measurement failed: SqlException; SQL 18456`**. `attach.exitcode` contém **1**. A mensagem de attach/inspection da linha 46 era secundária: o script tratava qualquer attach não zero como falha de transporte antes de registrar o estado/exit code do container.

O SDK iniciou, restaurou e executou a ferramenta, inclusive seu build implícito via dotnet run. Os antigos comandos eram encadeados por &&; não havia arquivos de saída separados de restore/build/inspect. Portanto, não se inventam exit codes observados para etapas que não foram registradas. O processo da ferramenta percorreu o catch que retorna 1, e o attach registrou esse resultado. O antigo script não gravou docker.exitcode porque lançou a mensagem genérica antes disso.

Evidência da escala **200**: todos os **16 cenários** concluídos, cinco amostras por cenário; **83 arquivos**, incluindo **32 planos XML válidos**, **32 arquivos de estatísticas não vazios**, catálogo, distribuição efetiva de **200 transações/50 transferências** e ambiente SQL Server **16.00.4230**, Snapshot ON/RCSI OFF. Isso comprova que SQL estava disponível e que provisionamento, migrations, dados e consultas da primeira escala foram alcançados. Os artefatos foram lidos e preservados; não são uma aprovação completa das duas escalas.

A pasta **40000** foi criada, mas contém **zero arquivos**. O código mostra a causa determinística: a cada escala eram geradas novas senhas para os mesmos logins **FinanceFlowPerfMigrator/FinanceFlowPerfApi**, que são do servidor. DatabaseTasks.ProvisionAsync usa `IF SUSER_ID(@username) IS NULL` e, corretamente, **não altera senha de login existente**. Na segunda escala, o provisionamento administrativo reutiliza os logins já existentes; o primeiro acesso com a nova senha do migrador acontece em **MigrateAsync**, antes de seed/catálogo/consultas. Essa combinação explica o **18456 de autenticação** após os 16 cenários pequenos, sem atribuir falha a uma consulta financeira. O log original não contém State/Class da SqlException; nenhum desses valores é inferido.

Os três registros de limpeza da tentativa — SDK, SQL e rede exclusivos — têm **exit code 0**, com seus nomes gerados registrados nos logs. São evidências externas de remoção dos recursos próprios. O Docker continuou inacessível no sandbox (config.json/pipe com acesso negado), impedindo uma nova inspeção direta do engine; nenhum recurso persistente foi acessado ou iniciado.

### Correção pontual e diagnóstico

Alterações restritas a **tools/FinanceFlow.QueryAnalysis/Program.cs**, **scripts/Test-QueryPerformance.ps1** e **este documento**:

1. Senhas de migrador e API passam a ser geradas **uma vez por execução**, antes do loop das escalas, mantendo uma credencial por login durante a vida desse SQL descartável. Bases, ApplicationName/pools e dados continuam separados por escala. Não se altera login, política de provisionamento, permissões ou segredo de ambiente existente.
2. A ferramenta registra fase segura em failure.json e stderr: disponibilidade, escala/provisionamento/migration/seed/catálogo, cenário/aquecimento/amostra e replay de estatísticas. Só registra tipo de exceção e metadados SQL Number/State/Class quando efetivamente disponíveis, **sem mensagens do provider, senhas ou connection strings**.
3. O SDK grava códigos separados de cópia, extração, restore, build e medição. O host grava attach.exitcode, inspect.exitcode, sdk-state.json e docker.exitcode antes de classificar o resultado. Container encerrado com saída não zero é reportado como falha da etapa/ferramenta, com código e caminho de console.log/failure.json; não é confundido com defeito no inspect.
4. Falhas reais de attach/inspect permanecem não zero. O host também grava procedure.exitcode, incluindo falhas de limpeza. Sucesso exige todas as etapas zero e **completion.json**, emitido somente após **ambas as escalas e os 32 cenários**, com dois aquecimentos e cinco repetições. Medição parcial nunca vira sucesso.

Permanecem intactos seed/distribuição, escalas, aquecimento, repetições, comandos reais dos stores, parâmetros, planos, STATISTICS IO/TIME, catálogo de índices, identidades restritas e isolamento/limpeza Docker. Nenhum índice, schema, migration, dependência ou grant foi modificado.

### Validações disponíveis e preservação

| Verificação nesta intervenção | Resultado real |
|---|---|
| Parser PowerShell do script final | Zero erros |
| Restore da ferramenta com NuGet.Config | Exit 0; dois NU1900 |
| Build Release --no-restore da ferramenta | Exit 0; zero erros, dois NU1900, sem warnings C#/analisadores |
| Inspeção offline do SQL | Exit 0; nova saída isolada em .local/7c-performance-results/offline-fix-20261127; nenhuma conexão SQL |
| Verificações isoladas do controle do script | **Oito cenários aprovados**, usando comando Docker substituto local, sem Docker/SQL: sucesso, falha da ferramenta, restore, inspect, attach, conclusão ausente/parcial e falha de limpeza |
| Git | main@9db921e, staging vazio; git diff --check aprovado |

As oito verificações do script ficam ignoradas em .local/7c-procedure-check e logs .local/7c-perf-check-*.log, em workspaces simulados separados dos resultados reais. Verificam saídas esperadas (inclusive não zero), preservação da falha original e os três alvos de limpeza gerados. **Não são testes funcionais novos nem reprodução SQL da correção**; a contagem funcional permanece **887**. Nenhuma nova execução da suíte funcional foi necessária: produção e testes não foram alterados.

Hashes SHA-256 antes/depois comprovam preservação de **99 arquivos em src, 85 em tests**, script Test-DockerSuite.ps1 e demais arquivos locais, exceto os três expressamente alterados. SeedData.cs, OfflineSql.cs e o csproj também permanecem idênticos. Os **96 arquivos** da tentativa real que falhou foram preservados por hashes. O inventário continua **16 arquivos locais, sete modificados e nove novos**, incluindo o trabalho anterior da 7C; não houve stage, commit ou push.

A auditoria NuGet continua **inconclusiva** pelos NU1900. Nenhuma proteção do Windows foi alterada. Não houve alteração do ambiente persistente, de credenciais do usuário ou início da Etapa 8.

### Próxima execução

```powershell
& C:\FinanceFlow\scripts\Test-QueryPerformance.ps1
```

É necessária uma nova execução externa completa, seguida de revisão dos códigos, completion.json, duas distribuições e métricas/planos de ambas as escalas. **A análise de desempenho ainda não está aprovada/concluída**, e a 7C não está publicada nem aprovada pela CI. A medição parcial anterior não autoriza conclusões sobre a escala maior ou alterações de índices.

**FERRAMENTA DE DESEMPENHO CORRIGIDA — PRONTA PARA NOVA VALIDAÇÃO EXTERNA.**

## Auditoria final e conclusão técnica — 27/11/2026

### Evidências funcionais e continuidade do código

Foram novamente conferidos os três XMLs completos e os códigos de saída da execução externa **20261127-004756-385-23c9ec**: **887/887**, Domain **116**, Application **189**, Integration **582**, zero falhas, ignorados e erros do runner. Restore/build/módulos/Docker possuem saída zero. Não foi necessário repetir a suíte no sandbox.

A comparação **ordinal de nomes e multiplicidades** com `.local/7b-docker-results/20261126-222947-267-8d877f` confirmou **858 casos anteriores preservados** e exatamente **29 novos Integration**, sem remoção. Os arquivos anteriores de testes continuam sem diff contra a baseline; não houve adaptação ou enfraquecimento. Os hashes registrados antes da correção da ferramenta ainda coincidem com os **99 arquivos de produção, 85 de testes e script funcional**, comprovando que o resultado funcional continua aplicável. A ferramenta/script corrigidos foram preservados antes/depois desta auditoria.

### Revisão de consistência e contratos

Os três casos `Count_and_page_share_the_snapshot_when_a_writer_commits_between_them` constam como **Pass**. A barreira é acionada no SELECT paginado, depois de a contagem ter terminado. O cliente escritor confirma uma criação em contexto/conexão independentes antes da liberação do leitor. A primeira resposta exige count=1/um item, dois SELECTs e IsolationLevel.Snapshot; a seguinte exige count=2/dois itens. Não há cache falso, sleeps ou serialização de requisições. Esses são comportamentos comprovados pelas asserções aprovadas, não valores de SPID inventados a partir de nomes de testes.

As três listas compartilham a transação/conexão de SnapshotReadSession. Em transactions, as verificações de conta/categoria também ficam nessa visão. Filtros, projeções, ordenação e DTOs não foram alterados. SnapshotReadFailure preserva exatamente 1205/-2/3952 e os wrappers conhecidos; somente passou a expor a causa já reconhecida. Contas/categorias propagam essa SqlException por ExceptionDispatchInfo para o handler seguro, sem mudar o contrato Page; transactions retorna Unavailable. OperationCanceledException não corresponde ao classificador. Falhas inesperadas sem causa SQL continuam no handler global.

Os **12 casos** de falhas passaram: timeout SQL real **-2**, diretamente e sob wrappers EF explicitamente construídos sobre essa causa real, exige **503**, nenhuma página parcial/detalhe interno e releitura bem-sucedida. Não se afirma que o provider naturalmente produziu todos esses wrappers durante uma leitura. InvalidOperationException sem causa SQL exige **500**. Os oito testes de limite de comandos e os cinco extremos de paginação passaram, incluindo deslocamento válido 2147483646 com página vazia/metadados corretos e rejeição do produto que excede int.MaxValue.

SnapshotReadSession não foi alterado: encerra/descar­ta transação, restaura ReadCommitted na mesma sessão, limpa com token independente/timeout e preserva a primeira exceção; retira somente o pool correspondente se a restauração falhar. A regressão inclui os casos anteriores de timeout, cancelamento, pooling e Snapshot indisponível, todos Pass. Não se repetiu integralmente a auditoria P2 nem se atribuiu aos testes novos uma sondagem física que não registraram. JWT/revogação, proprietário, no-store, Problem Details, RowVersion, idempotência, privilégios e OpenAPI permanecem os publicados.

### Reconciliação consolidada aprovada

`ConsolidatedReconciliationSqlTests.Independent_expected_positions_reconcile_all_read_models_after_each_historical_change` está **Pass**. Todas as etapas da tabela histórica deste documento foram alcançadas: início **4500/500/4000**, posições **8000/3000**, soma **11000**; edição **7950/3000**; retroativo **7950/3250**; exclusão **8300/3250**; cancelamento e inatividade **9300/2250**, soma **11550**, resultado **4550**. Relatórios, extratos das duas contas, saldos, transações/transferências e contagens reconciliam em cada etapa com valores declarados independentemente das respostas.

O grupo inicial **Alimentação 350 / Academia 150 / Salário 4500** é verificado especificamente pelo caso anterior `CategoryReportsSqlTests.Reference_categories_reconcile_with_summary_statements_and_balances_without_opening_or_transfers`, também Pass no XML atual. O novo cenário verifica as somas por natureza e, após renomeação/inatividade, os nomes Academia/Pagamento; não se atribui a ele uma asserção por ID/valor inicial que pertence ao teste anterior. O proprietário distinto com receita 999 não aparece nas projeções próprias. Patrimônio e resultado não são confundidos, e chamadas HTTP de reconciliação são feitas em dados estáveis, sem alegação de snapshot entre requisições.

### Medição externa concluída

A execução principal **20261127-012104-056-56f46de6** foi realizada pelo usuário e validada integralmente nesta auditoria: **32 cenários, dois aquecimentos, cinco repetições**, **250 e 50000 movimentos efetivos**, **160 amostras**, **64 estatísticas e 64 planos XML reais válidos**, todos os 12 códigos registrados zero e limpeza de SDK/SQL/rede próprios confirmada nos logs. completion.json e sdk-state.json confirmam conclusão e container encerrado/0, sem OOM. Não há failure.json nessa execução ou cenários omitidos. Os **190 arquivos** foram lidos e os artefatos parciais da primeira tentativa foram mantidos.

A causa SQL **18456** e a correção de credenciais descritas na seção anterior estão encerradas pela conclusão também da escala 40000. Senhas por login são geradas antes do loop; bancos/pools permanecem separados. O script distingue attach, inspect, exit do container e exit do procedimento, exige códigos e completion.json, e remove apenas os recursos cujos nomes gerou e cuja criação confirmou. Não altera volumes existentes, bancos persistentes ou permissões da API. Migrations/provisionamento ocorreram só nas bases descartáveis; stores usaram a identidade restrita, e SHOWPLAN/catálogo somente o replay/preparação administrativos.

A [análise de consultas e índices](analise-de-consultas-e-indices.md#medição-externa-completa-e-decisão-final--27112026) contém os **32 resultados** em tabela comparativa: mínimo/mediana/máximo dos stores, leituras lógicas, CPU/decorrido do replay SQL, número de comandos e linhas observáveis nos planos. As três origens não são confundidas com latência HTTP. Principais medianas grandes: extrato **21,516–23,211 ms**, transações **11,921–16,986 ms**, saldo **8,722–9,709 ms**. O maior máximo observado foi **24,834 ms** no extrato amplo. Esses são resultados deste ensaio, não SLA de produção.

Catálogo físico: **17 índices por base**, incluindo PKs, AKs, índices de FKs e os planejados. Os planos mostram seeks, scans clustered, sorts e agregações; há um Key Lookup no agrupamento pequeno e sugestões de cobertura na escala maior. Não foram encontrados warnings/spill nodes ou PlanAffectingConvert nos XMLs coletados. Todos os replays tiveram leituras físicas zero; CPU inteira pode aparecer zero por resolução, não por ausência de trabalho.

**Decisão: manter índices e consultas nesta entrega.** Há oportunidades futuras de cobertura e redução de leituras de agregados/OR/ordenação, sem gargalo bloqueante demonstrado ou ganho A/B medido. A análise registra um candidato somente para experimento futuro autorizado, seus custos de manutenção/sobreposição e necessidade de migration incremental/rollback se alguma mudança for posteriormente aprovada. Nenhuma alteração estrutural foi implementada ou é condição de liberação desta entrega.

Limites: cargas sequenciais sintéticas, contas/categorias fixas, descrições nulas, cache aquecido, cinco amostras, sem HTTP/JWT, sem capacidade máxima/percentis, sem hardware/quotas registrados. Páginas profundas e CategoryId da listagem não foram medidos, embora cobertos funcionalmente. Planos são replays administrativos reais, não captura garantida dos planos em cache de cada execução da API. Essas limitações não foram ocultadas nem extrapoladas.

### Critérios da Etapa 7 e parecer

| Critério do roadmap/contratos/aceitação | Evidência e resultado |
|---|---|
| Filtros e ordenação com desempate | Matriz dos oito endpoints; testes publicados preservados, by-category fixo; atendido |
| Página vazia/final/além do fim e totais | Regressão anterior e cinco novos extremos válidos/inválidos; atendido |
| Período inclusivo/366 datas apenas onde previsto | Services/contratos preservados e casos de calendário/limites da baseline Pass; atendido |
| Exclusões/cancelamentos/inativos | Seis estados da reconciliação e cobertura publicada; atendido |
| Proprietário e referências | Filtros SQL/relacionamentos, cenário com outro proprietário e regressão de 404/JWT; atendido |
| Consistência de uma resposta | Três listas corrigidas, demais consultas preservadas, testes concorrentes reais; atendido |
| SQL, precisão decimal, ausência de N+1 conhecido | Stores/projeções preservados, limites de comandos aprovados e comandos reais medidos; atendido |
| Reconciliação de resultado, grupos, extrato e saldo | Valores independentes e separação de patrimônio; atendido |
| Análise fundamentada de consultas/índices | Duas escalas completas e análise dos 64 planos, com limitações explícitas; atendido neste escopo |

**Nenhum achado P0/P1/P2 ou correção funcional bloqueante identificado.** As oportunidades de desempenho são **melhorias futuras não bloqueantes**, não motivo para DDL automático. Nenhum critério funcional da Etapa 7 ficou pendente no escopo aprovado.

Nesta auditoria foram alterados **exclusivamente README.md, docs/etapa-7/README.md, este documento e analise-de-consultas-e-indices.md**. Produção, testes, ferramenta e scripts foram preservados por hashes SHA-256 antes/depois, com manifestos ignorados em .local/7c-final-audit. Não houve execução de testes ou Docker, acesso ao ambiente persistente, alteração de schema/grants/dependências/workflow, stage, commit, push, reset ou clean. Branch **main@9db921e**, **16 arquivos locais (sete modificados/nove novos)**, staging vazio e git diff --check aprovado. Logs/planos brutos permanecem em .local, sem versionamento.

Os consoles externos não apresentaram NU1900, mas isso não substitui uma consulta de vulnerabilidades conclusiva; a **auditoria NuGet permanece inconclusiva**, com o histórico do sandbox preservado. A validação funcional e as medições são do usuário; o sandbox apenas analisou artefatos/código.

**APROVADA PARA COMMIT — ETAPA 7 TECNICAMENTE CONCLUÍDA.** A 7C ainda não está publicada nem aprovada pela CI; o encerramento oficial depende de publicação autorizada e CI correspondente. Etapa 8 continua pendente. Não foi executado stage, commit ou push.
