# Entrega 7B — Relatórios financeiros por categoria

## Baseline e escopo

Implementação local sobre **main@c6369fcf6afcd88b5edb50272a30cc92e7c9c93d**, `feat: implement financial summary reports`. Branch/HEAD e working tree foram conferidos antes das alterações: nenhum arquivo local anterior e staging vazio.

A 7A foi publicada com **GitHub Actions #13 / 45919727200 — Success** e **798/798** aprovados externamente: 116 Domain, 166 Application e 516 Integration. Evidência anterior: `.local/7a-docker-results/20261126-214221-889-68195a`. Publicação/CI e execução Docker são evidências fornecidas pelo usuário, não testes SQL executados nesta implementação. O histórico da 7A foi preservado em [entrega-7a-validacao.md](entrega-7a-validacao.md).

Esta entrega acrescenta exclusivamente **GET /api/v1/reports/by-category**. Não implementa 7C, Etapa 8, frontend, exportações, percentuais, orçamentos, paginação de grupos ou novos relatórios. A 7B **não está publicada nem aprovada pela CI**.

## Contrato HTTP

```http
GET /api/v1/reports/by-category?from=2026-11-01&to=2026-11-26&kind=expense
Authorization: Bearer <token>
```

`from` e `to` são obrigatórios, em `yyyy-MM-dd`, inclusivos, ordenados e limitados a **366 datas incluídas**, sem futuro no calendário America/Sao_Paulo. Usa FinancialCalendar/TimeProvider; subtração de DayNumber evita overflow nos extremos de DateOnly. `accountId` opcional deve ser um UUID não vazio de conta própria, inclusive inativa. `kind` opcional aceita somente **income** ou **expense**, sem normalização textual; ausente inclui ambos.

Query string mantém nomes case-insensitive conforme a 7A, mas rejeita chaves desconhecidas, duplicadas (inclusive casing diferente), valores explicitamente vazios, whitespace e formatos inválidos. Não aceita userId, sort, direction ou page. **Summary continua aceitando apenas from, to e accountId; kind permanece desconhecido naquela rota.**

Sucesso é HTTP **200**, `application/json`, `Cache-Control: no-store`, sem ETag ou envelope. Exemplo fictício:

```json
[
  { "categoryId": "22222222-2222-2222-2222-222222222222", "name": "Alimentação", "kind": "expense", "total": 350.00 },
  { "categoryId": "33333333-3333-3333-3333-333333333333", "name": "Academia", "kind": "expense", "total": 150.00 },
  { "categoryId": "11111111-1111-1111-1111-111111111111", "name": "Salário", "kind": "income", "total": 4500.00 }
]
```

Há exatamente quatro campos por item: **categoryId, name, kind, total**. Sem movimentos válidos retorna **[]**, inclusive usuário sem contas, conta sem movimentos ou filtro sem resultados. Não inclui categorias sem transações válidas no intervalo.

Ordem fixa no **SQL Server**: **kind asc, total desc, categoryId asc**. O desempate utiliza a ordenação de uniqueidentifier do SQL Server; os testes usam SqlGuid como referência independente.

| HTTP | Condição |
|---|---|
| 200 | Coleção completa, inclusive vazia |
| 400 | Período, conta, kind ou query inválidos |
| 401 | JWT ausente, inválido ou revogado, com WWW-Authenticate Bearer |
| 404 | Conta de filtro alheia ou inexistente, indistinguíveis |
| 429 | Rate limiting existente, preservando Retry-After |
| 503 | Indisponibilidade conhecida, conforme infraestrutura financeira existente |
| 500 | Falha inesperada pelo handler global seguro |

Problem Details e no-store seguem o pipeline existente, sem SQL, stack trace ou dados de terceiros. OpenAPI em Development documenta parâmetros, exemplos de grupos e vazio, ordenação, exclusões e respostas. Totais são decimais monetários positivos, sem o limite máximo de uma operação individual.

## Regras financeiras e reconciliação

Somente FinancialTransactions do proprietário autenticado, não excluídas, no intervalo inclusivo, respeitando accountId/kind quando informados. Cada grupo soma Amount como magnitude positiva. A relação com Categories preserva **CategoryId + UserId + Kind**, além do filtro explícito de proprietário em ambas as fontes.

Agrupamento por identidade da categoria, nome atual e natureza compatível: categorias de mesmo nome com naturezas diferentes continuam separadas. Renomear apresenta o nome atual também para transações históricas; não há histórico temporal de nomes. Contas/categorias inativas continuam incluídas; não há IsActive implícito.

Não consulta Transfers nem inclui saldo inicial, contribuições de abertura, transferências válidas/canceladas, transações excluídas ou outros usuários. Alterações, exclusões e retroativos refletem o estado atual dos registros, preservando a definição histórica do FinanceFlow.

O cenário de referência em banco descartável utiliza aberturas **5000/2000**, receita de salário **4500**, despesas **350/150** e transferência **1000**. Grupos esperados: Alimentação 350, Academia 150, Salário 4500; Itaú retorna vazio. Resumo: **4500 - 500 = 4000**, distinto do patrimônio **11000**; saldos **8000/3000** e extrato reconciliados. Cancelar a transferência não altera os grupos. Outro cenário distribui receitas **8,08/4,04** e despesas **3,03/3,03** entre duas contas, verificando filtros isolados e combinados, nome atual e histórico inativo.

Em dados estáveis, soma income dos grupos = summary.income; soma expense = summary.expense; diferença = summary.result. Os testes comparam valores decimais independentes e reconciliam com extrato/saldo. **Chamadas HTTP independentes não compartilham Snapshot durante escritas concorrentes.**

## Arquitetura, SQL e consistência

Estende ReportsController, ReportsService, IReportStore e ReportStore existentes, com ByCategoryRequest e contratos explícitos CategoryReportQuery/Group/Result/Failure. Controller não acessa DbContext; Application não conhece EF/SQL/HTTP; Domain permanece intacto. Validação comum de período foi extraída no mesmo serviço, mantendo o comportamento do summary.

O store executa JOIN, WHERE, GROUP BY, SUM e ORDER BY no SQL, com parâmetros, filtros de proprietário e AsNoTracking. Sem filtro de conta há um comando de agrupamento; com filtro há consulta de propriedade seguida do agrupamento, ambas na mesma Snapshot. Não há N+1, consulta por categoria, agrupamento em memória ou materialização de transações individuais. ToListAsync conclui a coleção inteira antes de retornar sucesso: falha não retorna grupos parciais.

A geração **offline** com EF Core confirmou esta forma de consulta, sem abrir conexão (diagnóstico ignorado em `.local/7b-generated-sql.log`):

```sql
SELECT [c0].[Id], [c0].[Name], [c0].[Kind], COALESCE(SUM([f].[Amount]), 0.0)
FROM [dbo].[FinancialTransactions] AS [f]
INNER JOIN (
    SELECT [c].[Id], [c].[Kind], [c].[Name], [c].[UserId]
    FROM [dbo].[Categories] AS [c]
    WHERE [c].[UserId] = @userId
) AS [c0] ON [f].[CategoryId] = [c0].[Id]
    AND [f].[UserId] = [c0].[UserId] AND [f].[Kind] = [c0].[Kind]
WHERE [f].[UserId] = @userId AND [f].[IsDeleted] = CAST(0 AS bit)
    AND [f].[OccurredOn] >= @from AND [f].[OccurredOn] <= @to
    -- Optional accountId and kind are parameterized predicates.
GROUP BY [c0].[Id], [c0].[Name], [c0].[Kind]
ORDER BY [c0].[Kind], COALESCE(SUM([f].[Amount]), 0.0) DESC, [c0].[Id];
```

Os nomes dos parâmetros acima foram abreviados para documentação. Não há conversão para double/float, arredondamento ou redução de escala para combinar somas. SUM mantém decimal exato; testa agregados maiores que o máximo individual e erro 8115 sem resposta parcial.

**SnapshotReadSession e SnapshotReadFailure não foram alterados.** Reutiliza a transação Snapshot, mesma sessão física, encerramento/descarte, restauração ReadCommitted antes do pool, limpeza independente do token cancelado, timeout limitado e preservação da exceção original. Mantém invalidação somente do pool correspondente se necessário, sem ClearAllPools, pooling global desabilitado, retries, RCSI ou fallback ReadCommitted. Classificação restrita de falhas SQL e distinção de cancelamentos/erros inesperados permanecem as publicadas.

Testes concorrentes estabelecem Snapshot na leitura da conta e pausam o GROUP BY por interceptor/barreira; outra conexão confirma criação de receita, exclusão de despesa ou renomeação de categoria. Primeira resposta exige nome/totais anteriores, a seguinte exige o novo estado. Sem sleeps como prova de ordenação.

O teste de pooling usa banco/connection string exclusivos, Pooling=true, MaxPoolSize=1, credencial restrita e verifica **SPID + ClientConnectionId** antes/durante/depois. Sonda CAST(@@SPID AS int), isolamento 5 e transação aberta durante o agrupamento; depois exige mesma sessão em isolamento 2 e @@TRANCOUNT=0. Verifica ainda store sem filtro, conexão fechada/transação nula antes do descarte do DbContext, perfil do proprietário com JWT real, logout e rejeição do token revogado. Esses são testes preparados; sua aprovação SQL depende da execução externa.

## Schema, índices, segurança e operação

Nenhuma tabela, coluna, migration, snapshot de modelo, índice, dependência ou grant novo. Índices existentes de FinancialTransactions por **UserId/OccurredOn/Id**, **UserId/AccountId/OccurredOn/Id** e **UserId/CategoryId/OccurredOn**, mais as chaves de Categories, foram inspecionados. São compatíveis com filtros e relacionamento; não foram medidos planos/benchmarks ou alegados ganhos de desempenho. Análise estrutural de índices permanece na 7C.

Usa SELECT já autorizado na credencial restrita, sem privilégios administrativos. JWT/revogação, rate limiting, identidade autenticada e no-store são os existentes. O filtro de conta não permite inferir existência alheia. Fixtures usam usuários reais, contas de terceiros no mesmo intervalo e verificam ausência de vazamento em grupos/totais.

O requisito **ALLOW_SNAPSHOT_ISOLATION ON** e readiness são os publicados na 6C: provisionamento administrativo explícito, nunca configuração automática no startup ou ampliação dos privilégios da API. A 7B não executa provisionamento/migration em banco persistente. Nenhum acesso, inicialização/reconstrução/reinício de containers persistentes, alteração de dados/volumes/credenciais ou proteção do Windows foi realizado.

## Arquivos e cobertura

**19 arquivos: dez modificados e nove novos.**

Modificados:

- `src/FinanceFlow.Api/Controllers/ReportsController.cs`: rota e validação estrita compartilhada com listas de parâmetros separadas.
- `src/FinanceFlow.Api/Configuration/ApiDocumentationConfiguration.cs`: contrato e exemplos da nova rota, preservando summary.
- `src/FinanceFlow.Application/Reports/ReportContracts.cs`: contratos da consulta agrupada e extensão de IReportStore.
- `src/FinanceFlow.Application/Reports/ReportsService.cs`: validação/identidade e encaminhamento.
- `src/FinanceFlow.Infrastructure/Persistence/Reports/ReportStore.cs`: agrupamento SQL sob Snapshot.
- `tests/FinanceFlow.Application.Tests/ReportsServiceTests.cs`: double existente recebe o novo membro, lançando NotSupportedException se houver dispatch incorreto; nenhuma asserção anterior alterada.
- `tests/FinanceFlow.IntegrationTests/ReportContractTests.cs`: guarda das rotas passa a exigir exatamente summary e by-category. Nome histórico do teste e todas as asserções do summary foram preservados para rastreabilidade; somente a disponibilidade da nova rota evoluiu.
- `scripts/Test-DockerSuite.ps1`: caminhos 7b e guarda de 858 testes; execução integral, XMLs, logs e exit codes preservados.
- `README.md` e `docs/etapa-7/README.md`: publicação da 7A, escopo local da 7B e resultados distintos.

Novos:

- `src/FinanceFlow.Api/Contracts/Reports/ByCategoryRequest.cs`.
- `tests/FinanceFlow.Application.Tests/CategoryReportsServiceTests.cs` — **23 casos**: calendário, extremos/366/367 datas, identidade, filtros/kind, passagem do token e falhas do store.
- `tests/FinanceFlow.IntegrationTests/CategoryReportContractTests.cs` — **3 casos**: JWT ausente/inválido e OpenAPI completo, preservando summary.
- `tests/FinanceFlow.IntegrationTests/CategoryReportValidationSqlTests.cs` — **22 casos**: query estrita, formatos, casing/duplicação, 366 datas, kind desconhecido em summary, conta alheia/inexistente e revogação real.
- `tests/FinanceFlow.IntegrationTests/CategoryReportsSqlTests.cs` — **5 casos** financeiros compostos: referência/reconciliação, identidade/nome atual/inativos/terceiros/filtros, vazios, edições/retroativos/exclusões, centavos/grandes agregados/desempate/permissões reais.
- `tests/FinanceFlow.IntegrationTests/CategoryReportConsistencySqlTests.cs` — **4 casos**: três escritas concorrentes coordenadas e reutilização física com JWT/revogação.
- `tests/FinanceFlow.IntegrationTests/CategoryReportFailureSqlTests.cs` — **3 casos**: overflow 8115 real, timeout -2 real e falha inesperada 500, sem grupos parciais, seguidos de nova consulta válida.
- `tests/FinanceFlow.IntegrationTests/CategoryReportRequests.cs`: auxiliares sem novos casos, assertivas de array exato/no-store/sem ETag.
- Este documento.

A indução de overflow substitui somente o **primeiro SUM da projeção** por SUM(CAST(valor máximo decimal(38,2))), sobre duas transações da mesma categoria. O SQL gerado repete SUM no ORDER BY; essa ocorrência fica intacta. Aliases e projeções internas são preservados. Exige **8115**, não aceita 8155 e não modifica a classificação de produção. Session probe converte explicitamente @@SPID smallint para int. Asserções HTTP, financeiras e de limpeza não foram substituídas por aceitação genérica de erros.

Os **798 casos anteriores foram mantidos**, sem remoção, rename, skips ou enfraquecimento; apenas as duas adaptações de contrato/double descritas acima. Novos: **60** (23 Application, 37 Integration).

| Projeto | Baseline publicada | Novos | Total esperado |
|---|---:|---:|---:|
| Domain | 116 | 0 | 116 |
| Application | 166 | 23 | 189 |
| Integration | 516 | 37 | 553 |
| **Total** | **798** | **60** | **858** |

## Verificações executadas no sandbox

| Verificação | Resultado real |
|---|---|
| Restore com NuGet.Config | Saída 0; cinco NU1900 |
| Build Release --no-restore | Saída 0; zero erros, cinco NU1900; sem warnings C#/analisadores |
| Geração SQL offline via ToQueryString | Saída 0; agregação/ordenação parametrizadas e alteração de overflow inspecionadas; sem conexão ou execução SQL |
| Suíte completa Release --no-build --no-restore | **858 descobertos: 430 aprovados, 428 falhas de fixture Docker, zero ignorados**, saída 2 |
| Domain / Application | **116 / 189 aprovados**, incluindo todos os novos casos Application |
| Integration | **125 sem SQL aprovados; 428 falhas na construção da fixture**, antes das asserções SQL |
| Docker | Acesso negado ao config.json/pipe docker_engine; SQL Server/Testcontainers indisponível no sandbox |
| Script PowerShell | Análise sintática sem erros; três módulos e guardas de relatórios preservados |
| Git | main@c6369fc, staging vazio e git diff --check aprovado |

Os 428 blocos de falha do runner foram conferidos: todos contêm falha de construção de IdentitySqlFixture com DockerUnavailableException. Não são 428 defeitos funcionais comprovados e **não foram registrados como testes SQL aprovados**. Exceções intencionais dos logs de testes negativos não são tratadas como falhas adicionais do runner. A suíte não foi filtrada, desabilitada ou substituída por InMemory/SQLite. Logs locais de restore/build/testes/SQL offline ficam em `.local/7b-*`, ignorados pelo Git.

NU1900 permanece visível: índice de vulnerabilidades NuGet inacessível, auditoria **inconclusiva**. Não houve supressão de warnings nem instalação de dependências novas. O bloqueio Docker não foi contornado por mudança de segurança do Windows.

## Reprodução externa e pendências

No PowerShell, com Docker Desktop Linux e recursos para SQL Server/Testcontainers:

```powershell
& C:\FinanceFlow\scripts\Test-DockerSuite.ps1
```

O script executa restore/build e os três módulos completos em SDK Linux, com source mount somente leitura e bancos descartáveis. Preserva logs integrais em `.local/7b-docker-tests.log`, relatórios/console/exit codes por execução em `.local/7b-docker-results/<execucao>` e os históricos 7A/6C. Exige **três XMLs, 858 executados/aprovados, zero falhas/ignorados/erros do runner e Docker exit code 0**.

Ao concluir a implementação, permaneciam pendentes a validação SQL externa dos 858 casos, revisão dos XMLs/códigos de saída e auditoria final. Naquele momento, concorrência, overflow e pooling estavam compilados, mas não comprovados pelo SQL Server no sandbox. Essas pendências funcionais foram resolvidas pela execução externa e auditoria registradas abaixo; o histórico local não foi reclassificado como aprovação SQL própria. Não há alteração estrutural proposta para a 7C.

**ENTREGA 7B IMPLEMENTADA — PRONTA PARA VALIDAÇÃO EXTERNA.** Sem stage, commit ou push; ambiente persistente preservado. 7C e Etapa 8 continuam pendentes.

## Validação externa e auditoria final — 26/11/2026

### Relatórios conferidos

A primeira execução externa do usuário, **20261126-222947-267-8d877f**, aprovou toda a suíte no Docker Linux com SQL Server/Testcontainers. Evidências lidas nesta auditoria: os três XMLs completos, console.log e os seis arquivos de exit code em `.local/7b-docker-results/20261126-222947-267-8d877f`. Não foi execução SQL do sandbox.

| Projeto | Executados/aprovados | Falhas | Ignorados | Erros do runner | Novos contra 7A |
|---|---:|---:|---:|---:|---:|
| Domain | 116/116 | 0 | 0 | 0 | 0 |
| Application | 189/189 | 0 | 0 | 0 | 23 |
| Integration | 553/553 | 0 | 0 | 0 | 37 |
| **Total** | **858/858** | **0** | **0** | **0** | **60** |

Restore, build, Domain, Application, Integration e Docker possuem **exit code 0**. O número de nós test em cada XML coincide com o total da respectiva assembly. A comparação **ordinal de nomes e multiplicidades** com `.local/7a-docker-results/20261126-214221-889-68195a` confirmou **798 casos preservados**, nenhuma perda e exatamente **23 + 37 = 60 adicionais**.

As duas adaptações anteriores são legítimas: o double de ReportsServiceTests apenas implementa o novo membro e falha com NotSupportedException caso seja chamado por engano; suas asserções antigas permanecem intactas. ReportContractTests passa a exigir exatamente as duas rotas disponíveis, em vez de somente summary; preserva nome do caso e todas as verificações do contrato/schema/segurança do summary. O novo teste também confirma HTTP 400 quando kind é enviado ao summary.

### Evidências críticas e limites da interpretação

**Concorrência:** os três casos `One_snapshot_keeps_current_names_and_totals_together_while_an_independent_writer_commits`, actions **create/delete/rename**, constam como Pass. O fluxo revisado estabelece Snapshot na consulta de propriedade, suspende o GROUP BY por interceptor, confirma HTTP de escrita em outra requisição/contexto/conexão e só então libera a leitura. A primeira resposta exige despesa **3** e receita **10**, com nome original. A seguinte exige receita **17**, exclusão da despesa ou nome renomeado, conforme o caso. A contagem de comandos confirma as duas leituras. Não há sleeps como evidência de coordenação; nomes e agregados pertencem à mesma visão.

**Overflow:** o caso `Failed_group_aggregation_never_streams_partial_groups_and_can_be_repeated(stage: overflow, status: ServiceUnavailable, sqlNumber: 8115)` consta como Pass. O resultado não é inferido do nome: a asserção **Assert.Equal(sqlNumber, fault.SqlNumber)** observa SqlException via CommandFailed/QueryIterationFailed da fábrica do próprio teste e exige **8115**. A instrumentação substitui apenas o SUM da projeção por uma soma decimal(38,2) que excede sua capacidade sobre duas linhas da mesma categoria. A projeção interna Categories, os aliases, as chaves do JOIN e o SUM de ORDER BY permanecem intactos. Não aceita erro 8155.

As asserções posteriores também foram alcançadas, sem retorno antecipado: HTTP **503**, Problem Details/no-store, ausência de categoryId/total parciais, SQL, código interno ou stack trace, seguida de nova consulta bem-sucedida com **total 2**. O caso **timeout** exige precisamente SQL **-2** e o mesmo contrato seguro/releitura válida; o caso **unexpected** exige **500**, não uma classificação genérica como indisponibilidade conhecida. Os números SQL são comprovados pelas asserções internas aprovadas, não atribuídos a partir de linhas soltas de logs de outros testes negativos.

**Pooling:** `Real_grouped_read_returns_the_same_physical_session_as_ReadCommitted_and_preserves_JWT_revocation` consta como Pass. O output deste próprio caso registra:

| Sondagem | SPID | ClientConnectionId | Isolamento | @@TRANCOUNT |
|---|---:|---|---:|---:|
| Antes, após preparação | 201 | dbe28884-85ef-48da-a226-67bea56b6d3b | 3 — RepeatableRead | 0 |
| Durante o agrupamento | 201 | dbe28884-85ef-48da-a226-67bea56b6d3b | 5 — Snapshot | 1 |
| Depois da consulta | 201 | dbe28884-85ef-48da-a226-67bea56b6d3b | 2 — ReadCommitted | 0 |
| Final, após JWT/revogação | 201 | dbe28884-85ef-48da-a226-67bea56b6d3b | 2 — ReadCommitted | 0 |

A sondagem durante o agrupamento usa a própria conexão e transação SQL do comando interceptado. SPID e ClientConnectionId são iguais; não se está concluindo restauração a partir de uma sessão física diferente. O nível inicial observado foi **3**, não ReadCommitted. As asserções seguintes comprovam total HTTP **1,01**, nova consulta do store sem filtro também **1,01**, transação EF nula e conexão fechada **antes** do descarte do DbContext, perfil do mesmo proprietário com JWT real, logout 204, token revogado 401/Bearer e sondagem final inalterada. O teste usa banco exclusivo, MaxPoolSize=1 e credencial restrita; pooling continua habilitado.

**Reconciliação:** o cenário de referência retorna os grupos **350/150/4500**, summary **4500/500/4000**, extrato com resultado **4000** e saldos **8000/3000**. Transferência de 1000 e seu cancelamento não alteram os grupos. O segundo cenário exige receitas/despesas em ambas as contas, seus filtros isolados e reconciliação com summary, inclusive histórico inativo. Os casos de edição/retroativo/exclusão, nome atual, mesmo nome com tipos distintos, coleção vazia, centavos, agregado **1999999999999,98** e desempate SQL GUID também passaram. São comparações em dados estáveis, sem alegar Snapshot compartilhado entre respostas HTTP independentes.

### Parecer técnico

Foram revisados todos os **19 arquivos** contra c6369fc, inclusive os nove novos. **Nenhum achado técnico bloqueante ou correção funcional necessária foi identificado.**

O endpoint retorna exatamente a coleção de quatro campos aprovada; o agrupamento inclui a identidade da categoria, seu nome atual e natureza compatível, sem fundir tipos de mesmo nome. Magnitudes são positivas e decimais, categorias sem movimento não aparecem, inativos preservam histórico e exclusões/retroativos são respeitados. Não há consulta de transferências ou saldo inicial. Filtros, JOIN, GROUP BY, SUM e ordenação fixa são SQL parametrizados, com UserId em ambas as fontes e relacionamento por Id/UserId/Kind. ToListAsync conclui a coleção antes do sucesso, sem N+1 ou agrupamento de transações individuais em memória.

A inspeção de `.local/7b-generated-sql.log` confirma a forma traduzida pelo EF e a substituição restrita de SUM; é evidência **offline**, separada da execução externa bem-sucedida dos testes. Nenhum benchmark/plano medido é alegado. Os índices existentes são compatíveis com os predicados e chaves; não foram acrescentados índices preventivos.

Propriedade da conta, agregados e nomes usam SnapshotReadSession na mesma conexão/transação. A infraestrutura compartilhada permanece idêntica à baseline, com limpeza independente do token cancelado, timeout limitado, preservação da primeira exceção e invalidação somente do pool correspondente. Não há RCSI, fallback inconsistente, ClearAllPools, desativação global de pooling ou retries indiscriminados. SnapshotReadFailure continua restrita aos erros conhecidos **1205/-2/3952** e não absorve cancelamentos arbitrários; overflow mantém o handler global seguro anterior.

JWT/revogação, proprietário autenticado, 404 indistinguível, formato exato, limite inclusivo de 366 datas, calendário financeiro, Guid.Empty, kind canônico e rejeição de query desconhecida/duplicada/vazia foram conferidos no código e nos casos aprovados. No-store, Problem Details, Bearer, rate limiting/Retry-After e OpenAPI permanecem coerentes. O summary preserva seus seis campos, validações e rejeição de kind. Swagger continua restrito a Development.

ReportApiFactory utiliza efetivamente ApplicationConnectionString da fixture na configuração SQL da API, não a conexão administrativa. O teste financeiro também executa VerifyApplicationPrivilegesAsync com essa identidade restrita. Não há mudança em grants, DatabaseTasks, DbContext, migrations, mapeamentos, dependências ou workflow. Autenticação, idempotência, RowVersion, contas/categorias, transações, saldos, transferências e extratos permanecem preservados na regressão de 798 casos.

### Integridade e pendências finais

Nesta auditoria foram atualizados **somente README.md, docs/etapa-7/README.md e este documento**. Os demais **16 arquivos locais**, incluindo produção/testes/script, foram preservados por hashes SHA-256. Branch **main**, HEAD **c6369fcf6afcd88b5edb50272a30cc92e7c9c93d**, **19 arquivos locais: dez modificados e nove novos**, staging vazio e git diff --check aprovado. Nenhum segredo ou artefato temporário indevido foi identificado no inventário versionável; diagnósticos e logs em .local permanecem ignorados e preservados.

O console externo registra **build com zero warnings** e nenhuma ocorrência de NU1900. Isso não constitui evidência de uma nova auditoria de vulnerabilidades conclusiva; a pendência NuGet permanece. O histórico de NU1900 e bloqueio Docker do sandbox foi mantido e não é apresentado como execução SQL própria. Não foi necessário repetir testes ou executar Docker nesta auditoria documental/técnica.

Permanecem **publicação autorizada, CI própria da 7B e auditoria NuGet conclusiva**, sem impedimento técnico identificado para commit. A 7B ainda não foi publicada. O ambiente persistente não foi acessado, iniciado ou modificado; nenhuma proteção, credencial ou volume foi alterado. **7C e Etapa 8 continuam pendentes; a Etapa 7 não está concluída.** Não houve stage, commit, push, reset ou clean.

**Parecer final: APROVADA PARA COMMIT.** A validação externa integral e a revisão técnica não identificaram impedimento bloqueante. A aprovação não executa stage, commit, push ou publicação.
