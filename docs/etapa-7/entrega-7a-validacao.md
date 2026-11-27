# Entrega 7A — Resumo financeiro por período

## Estado e preparação

**APROVADA PARA COMMIT.** Validação externa **798/798** e auditoria técnica final concluídas conforme registro abaixo. Implementação ainda local, sem publicação ou CI própria aprovada. O sandbox não executou com sucesso os testes SQL; seus bloqueios permanecem separados das evidências externas do usuário. Auditoria NuGet inconclusiva; 7B/7C/Etapa 8 não iniciadas.

Baseline conferida: **main@d303fc220c3ea02df86c71355c80112101bb4407**, mensagem `feat: implement transfer listing and reconciled account statements`. Working tree e staging estavam vazios antes da implementação. A Etapa 6 está concluída, com 6C publicada, **CI #12 / 45751367056 — Success** e **741/741** na execução externa do usuário `20261126-002527-480-4b4fce` (116 Domain, 149 Application, 476 Integration). O P2 de pooling foi resolvido e auditado. Os resultados da 6C não são atribuídos à 7A.

O usuário confirmou posteriormente imagem da API atualizada, migrations/provisionamento explícitos no ambiente persistente, SQL/API Healthy, Swagger em https://localhost:5443/swagger/index.html e validação manual de identidade e finanças até extrato. Esses dados fictícios e containers foram preservados; o Codex não executou Compose, provisionamento, migrations ou consultas contra o banco persistente.

Foram revisados AGENTS.md, README, roadmap, contratos, critérios, ADRs 001 a 006, documentos da 6C e os componentes HTTP, serviços, stores, DbContext/mapeamentos, autenticação, erros, rate limiting, Snapshot, fixtures SQL e script Docker. Escopo exclusivo: resumo; 7B/7C/Etapa 8 permanecem pendentes.

## Contrato HTTP

**GET /api/v1/reports/summary**, JWT válido e não revogado obrigatório.

| Query | Regra |
|---|---|
| from | Obrigatória, yyyy-MM-dd exato |
| to | Obrigatória, yyyy-MM-dd exato |
| accountId | Opcional; GUID não vazio de conta própria, inclusive inativa |

Período inclusivo, from <= to, máximo **366 datas incluídas**, sem futuro em America/Sao_Paulo. FinancialCalendar usa o instante do TimeProvider existente. Subtração de DayNumber evita overflow em extremos de DateOnly. Propriedade vem exclusivamente de ICurrentUser, nunca de query/body.

Parâmetros desconhecidos, duplicados, vazios ou compostos apenas por espaços são rejeitados; datas não aceitam timestamp ou formato aproximado. Casing dos nomes segue o padrão existente, sem distinção de maiúsculas/minúsculas; duplicações com casing diferente também falham. Não há paginação, ordenação, agrupamento, If-Match ou Idempotency-Key no resumo.

Exemplo fictício:

```http
GET /api/v1/reports/summary?from=2026-11-01&to=2026-11-26
Authorization: Bearer <token>
```

```json
{
  "currency": "BRL",
  "from": "2026-11-01",
  "to": "2026-11-26",
  "income": 4500.00,
  "expense": 500.00,
  "result": 4000.00
}
```

Para uma conta, acrescentar `&accountId=<id-proprio>`; ausente considera todas as contas próprias. Conta válida sem movimentos e usuário sem contas retornam 200, mesmos seis campos, totais zero. Nenhuma referência ao proprietário, saldo, patrimônio, paginação ou entidade EF é exposta.

Respostas: **200**, **400** entrada inválida, **401** JWT ausente/inválido/expirado/revogado, **404** conta alheia/inexistente indistinguível, **429** com Retry-After, **503** indisponibilidade conhecida ou falha SQL conforme pipeline existente, **500** falha inesperada segura. Erros usam Problem Details; 401 preserva WWW-Authenticate. O middleware aplica **Cache-Control: no-store**, inclusive antes do controller. Não há ETag em resposta agregada. Swagger/OpenAPI continuam somente em Development; documentam parâmetros, limites, exemplo e agregados sem o limite individual de operação.

## Regras financeiras e SQL

ReportStore consulta **somente FinancialTransactions**, filtrando UserId, IsDeleted=false, OccurredOn >= from e <= to, e AccountId quando informado. Não filtra atividade de conta/categoria e não consulta Transfers ou saldo inicial. Income soma Amount de income; expense soma Amount de expense como magnitude positiva. **Result = income - expense**, podendo ser negativo ou zero.

A verificação de conta consulta Id + UserId, sem distinguir inexistência e outro proprietário. Somatórios condicionais de receita e despesa compartilham **um comando SQL agregado**; GroupBy constante retorna ausência de linha em conjunto vazio, convertida explicitamente em zeros. Nenhum histórico é materializado, não há N+1 ou paginação em memória. Consultas EF são parametrizadas e AsNoTracking.

Dinheiro permanece decimal; SUM preserva a precisão SQL **decimal(38,2)** e as duas parcelas são lidas separadamente. A subtração final usa **checked decimal** no C#, evitando redução de escala ao subtrair somas decimal(38,2) no SQL. Agregados podem superar o máximo de uma transação. Falha de soma/conversão/overflow não retorna resultado parcial ou arredondado: segue o tratamento global seguro existente. SQL 8115 é observado somente na instrumentação interna dos testes, nunca acrescentado à resposta pública.

## Consistência e proteção do pool

Reutiliza **SnapshotReadSession** sem modificá-lo. Quando existe filtro, a leitura de propriedade e a agregação participam da mesma transação Snapshot; sem filtro, os dois totais estão no mesmo comando dentro dessa sessão. Não se presume snapshot pontual de um SELECT sob ReadCommitted e não se somam respostas HTTP de saldo/extrato.

O componente compartilhado mantém a mesma conexão física aberta, confirma/descarta a transação e restaura ReadCommitted antes do retorno ao pool. Limpeza usa CancellationToken.None, timeout limitado e preservação da exceção original; quando não pode garantir restauração, invalida somente a geração do pool correspondente. Sem ClearAllPools, pooling global desabilitado, interceptor global, retry ou fallback inconsistente. Os 26 testes P2 da baseline permanecem intactos.

SnapshotReadFailure também é reutilizado, sem ampliar sua captura: reconhece SQL **1205/-2/3952** diretos ou nos wrappers conhecidos. Cancelamentos e erros inesperados não são convertidos indiscriminadamente em SummaryFailure.Unavailable. O pipeline global permanece intacto, inclusive sua resposta segura para outros erros DbException. Nenhum resultado é confirmado antes do commit e da limpeza.

Requisito operacional **já existente na 6C**: ALLOW_SNAPSHOT_ISOLATION ON, provisionado administrativamente de modo explícito/autorizado e detectado pela readiness. Não habilitar RCSI ou configurar o banco no startup. A 7A não acrescenta requisito de provisionamento, grants ou configuração de banco ao ambiente já preparado pelo usuário.

## Schema, índices e privilégio mínimo

Não há tabela/coluna, entidade de relatório persistida, migration ou alteração de snapshot. Migrations históricas foram preservadas. SELECT em FinancialAccounts e FinancialTransactions já é autorizado à identidade restrita da API; não há mudança em DatabaseTasks ou grants, nem uso administrativo no caminho da API.

Índices existentes analisados em FinancialTransactionConfiguration: **(UserId, OccurredOn, Id)** para o período global e **(UserId, AccountId, OccurredOn, Id)** para o filtro de conta. A verificação da conta utiliza as chaves existentes. Não se alegam medições/benchmarks ou cobertura de índice por INCLUDE; nenhuma adição foi proposta sem evidência. Análise consolidada pertence à **7C**, não iniciada.

## Cobertura acrescentada

Baseline **741** preservada sem editar testes anteriores. **57 casos novos**, total descoberto **798**:

| Arquivo | Casos | Cobertura |
|---|---:|---|
| ReportsServiceTests | 17 | Proprietário, ausência de identidade, filtro, store/falhas sem retry, mesmo dia, 366/367 datas, extremos, ordem/futuro e virada UTC/São Paulo |
| ReportContractTests | 4 | JWT ausente/malformado/expirado, no-store/Problem Details, OpenAPI/Bearer, seis campos e limites corretos dos agregados |
| ReportsSqlTests | 28 | Cenário financeiro de referência, filtros/isolamento, inatividade, vazio/abertura, edição/retroativos/exclusão, centavos/limites agregados, query estrita, 366 datas, revogação e 429 |
| ReportConsistencySqlTests | 4 | Criação/exclusão concorrente determinística e sessão reutilizada com filtro/sem filtro, identidade real e revogação |
| ReportFailureSqlTests | 4 | Timeout SQL -2, overflow SQL 8115, erro inesperado seguro e Snapshot ausente/3952, sem relatório parcial |

ReportTestRequests contém apenas helpers e host de teste, sem casos. Financial clocks são controlados; relógio JWT, assinatura, autenticação e revogação permanecem reais. Todo teste SQL usa IdentitySqlFixture/Testcontainers SQL Server 2022 e as credenciais restritas da API. Credenciais administrativas/migration são usadas apenas pela preparação/diagnóstico dos bancos descartáveis. Não há SQLite, InMemory, skip ou consulta automatizada ao ambiente persistente.

### Reconciliação e concorrência

O cenário de referência usa duas contas novas com abertura em 01/11/2026 e posições 5000/2000; receita 4500 em 05/11, despesas 150/350 em 10/11 e 15/11, transferência 1000 em 20/11. Os valores esperados são constantes independentes da produção: resumo global/primeira conta **4500/500/4000**, segunda conta **0/0/0**; extratos e saldos **8000/3000**, patrimônio conjunto **11000**, distinto do resultado **4000**. Cancelar a transferência não muda o resumo. Outro conjunto distribui receitas/despesas e centavos nas duas contas e em outro proprietário, incluindo histórico inativo; comprova filtros, resultado negativo e limites inclusivos. Edição troca valor/tipo/conta/data; exclusão remove o efeito. Abertura nunca aparece como receita.

No teste concorrente, a leitura Snapshot da conta já ocorreu quando uma barreira pausa o agregado. Uma requisição independente confirma receita ou exclusão de despesa, antes de liberar a leitura. A primeira resposta exige os totais anteriores; uma nova resposta exige o estado confirmado. Não há sleeps arbitrários ou comparação de respostas concorrentes como se fossem do mesmo snapshot.

Os dois testes de pooling criam banco e connection string exclusivos, Pooling=true/Max Pool Size=1, e exigem o mesmo **SPID + ClientConnectionId** durante a consulta e na reutilização, com isolamento **2** e @@TRANCOUNT=0 depois. Também verificam transação nula/conexão fechada antes de descartar o DbContext, nova consulta válida e JWT/proprietário/logout/revogação. Os testes compartilhados da 6C continuam provando os demais caminhos de limpeza.

Timeout usa WAITFOR/CommandTimeout para provocar erro real. Overflow altera somente operandos no comando interceptado, exigindo 8115 em CommandFailed/QueryIterationFailed e resposta sanitizada. Snapshot ausente altera somente uma base exclusiva do contêiner, com Pooling=false nesse cenário de configuração, autenticação real preservada e exigência de SQL 3952/HTTP 503, readiness 503/liveness 200. Não há configuração automática ou fallback.

## Arquivos

Criados: Api/Contracts/Reports/SummaryRequest.cs; Api/Controllers/ReportsController.cs; Application/Reports/ReportContracts.cs e ReportsService.cs; Infrastructure/Persistence/Reports/ReportStore.cs; os cinco arquivos de testes listados acima e ReportTestRequests.cs; docs/etapa-7/README.md e este documento.

Modificados: Api/Program.cs (no-store), Api/Configuration/ApiDocumentationConfiguration.cs (somente documentação do resumo), Infrastructure/DependencyInjection.cs (serviço/store), scripts/Test-DockerSuite.ps1 (caminhos 7A e guarda da contagem real) e README.md (publicação da 6C/estado da 7A).

Total **18 arquivos: cinco modificados e 13 novos**. Domain, contratos publicados, infraestrutura Snapshot, DatabaseTasks, schema, dependências, workflow e todos os testes anteriores permanecem intactos.

## Resultados executados no sandbox

| Verificação | Resultado |
|---|---|
| Restore com NuGet.Config | Saída 0; cinco NU1900 |
| Build Release | Saída 0; sete projetos, zero erros e cinco NU1900; sem warnings C#/analisadores |
| dotnet test Release --no-build --no-restore | 798 reportados; **404 aprovados, 394 falhas de fixture Docker, zero ignorados**, saída 2; aproximadamente 33,9s |
| Domain | 116 aprovados |
| Application | 166 aprovados, incluindo os 17 novos |
| Integration | 122 sem SQL aprovados, incluindo quatro novos; 394 falhas na fixture Docker, incluindo os 36 novos casos SQL |
| Docker | Acesso negado a config.json e pipe docker_engine; nenhuma proteção alterada |
| NuGet | Inconclusivo: NU1900, índice de vulnerabilidades inacessível; não houve supressão |

Os 394 casos SQL falharam antes das asserções por DockerUnavailableException; não são falhas financeiras demonstradas nem aprovação SQL. Os novos testes de Application e os contratos HTTP/OpenAPI disponíveis passaram. Não houve bloqueio adicional de carregamento identificado nesta execução.

Logs ignorados: `.local/7a-restore.log`, `7a-build-initial.log`, `7a-build.log` e `7a-tests.log`. A compilação inicial apontou duas referências anuláveis no código novo de OpenAPI; foram corrigidas, e o build final teve somente NU1900. Nenhum warning foi suprimido.

Verificações estáticas: **git diff --check aprovado**, script PowerShell sem erros de parsing e guarda 798/798. Os arquivos de testes da baseline não têm alterações; SnapshotReadSession/SnapshotReadFailure, autenticação, DbContext, DatabaseTasks, migrations, dependências e workflow permanecem intactos. Sem artefatos temporários ou assinaturas de segredos identificados nos arquivos novos/modificados. Estado final: **main@d303fc2**, 18 arquivos locais (cinco modificados, 13 novos), staging vazio; nenhum stage, commit ou push.

## Primeira validação externa e correção pontual — 26/11/2026

O **usuário**, externamente no Docker Linux/SQL Server/Testcontainers, executou a suíte em **`.local/7a-docker-results/20261126-023548-931-084230`**. O Codex conferiu os três XMLs completos, os erros correspondentes no console.log e os arquivos de códigos de saída:

| Projeto | Executados | Aprovados | Falhas | Ignorados / erros do runner |
|---|---:|---:|---:|---|
| Domain | 116 | 116 | 0 | 0 / 0 |
| Application | 166 | 166 | 0 | 0 / 0 |
| Integration | 516 | 513 | 3 | 0 / 0 |
| Total | **798** | **795** | **3** | **0 / 0** |

Restore/build/Domain/Application retornaram **0**; Integration e Docker, **1**. Os 741 casos anteriores e 54 dos 57 novos passaram. Essa execução não foi realizada no sandbox e não aprova a correção posterior.

### Overflow: substituição ampla invalidava a consulta derivada

Falha em **ReportFailureSqlTests.A_failed_aggregate_never_returns_partial_totals_and_a_later_read_succeeds**, `stage: overflow`, status ServiceUnavailable, sqlNumber 8115, antiga linha **88**. A asserção esperava **8115**, mas observou **8155**. O console registra, a partir da linha 10079: **No column name was specified for column 1 of 'f0'**, Number=8155, State=2, Class=16.

A regex original substituía **todas** as referências qualificadas a Amount, inclusive a projeção da subconsulta que o EF cria para GroupBy constante. A geração offline por **ToQueryString com EF Core SQL Server 10.0.13**, sem abrir conexão, reproduziu a estrutura e demonstrou o problema. Trecho incorreto induzido pelo teste:

```sql
SELECT COALESCE(SUM(CASE
    WHEN [f0].[Kind] = 'income'
        THEN CAST(999999999999999999999999999999999999.99 AS decimal(38,2))
    ELSE 0.0 END), 0.0) AS [Income]
FROM (
    SELECT CAST(999999999999999999999999999999999999.99 AS decimal(38,2)),
        [f].[Kind], 1 AS [Key]
    FROM [dbo].[FinancialTransactions] AS [f]
    -- Filtros parametrizados de proprietário, conta, datas e exclusão preservados.
) AS [f0]
GROUP BY [f0].[Key];
```

A primeira expressão da tabela derivada deixou de possuir o nome Amount; o SQL falha ao compilar a consulta, antes de calcular SUM. Não é um defeito no agregado de produção.

**Correção:** limitar a regex aos operandos após THEN (`\bTHEN\s+\[[^\]]+\]\.\[Amount\]`), preservando os nomes/projeções internos. Trecho corrigido:

```sql
SELECT COALESCE(SUM(CASE
    WHEN [f0].[Kind] = 'income'
        THEN CAST(999999999999999999999999999999999999.99 AS decimal(38,2))
    ELSE 0.0 END), 0.0) AS [Income]
FROM (
    SELECT [f].[Amount], [f].[Kind], 1 AS [Key]
    FROM [dbo].[FinancialTransactions] AS [f]
    -- Os mesmos filtros parametrizados permanecem intactos.
) AS [f0]
GROUP BY [f0].[Key];
```

Os trechos mostram Income; o comando completo também contém Expense, seus aliases e os mesmos filtros da consulta original. A verificação offline confirmou **dois operandos CASE substituídos** e projeção interna intacta. Os dois lançamentos income de 1,00 preparados pelo teste passam a contribuir, somente nessa consulta interceptada, com o máximo decimal(38,2); a soma deve exceder sua capacidade. **Ainda falta comprovar o novo comando e o erro 8115 em SQL Server real.** Não se aceita 8155 ou qualquer erro genérico como equivalente.

Permanecem intactas as asserções de injeção efetiva, número exato **8115**, HTTP **503**, no-store, Problem Details sem totais parciais/detalhes SQL e consulta posterior com totais **2/0/2**. SnapshotReadSession e a classificação de falhas de produção não foram alterados.

### Sessão: @@SPID retornava smallint, não int

Os dois casos de **ReportConsistencySqlTests.Summary_restores_the_same_pooled_session_before_real_JWT_revocation_and_a_later_query**, `filter: False` e `filter: True`, falharam na antiga linha **126**, chamada pela linha **85**. Isso ocorreu na sondagem **before**, antes de consultar o resumo: SqlDataReader.GetInt32(0) recebeu **System.Int16**.

| Expressão | Tipo SQL nativo | Tipo CLR na consulta original | Correção |
|---|---|---|---|
| @@SPID | smallint | Int16, incompatível com GetInt32(0) | CAST(@@SPID AS int), retornando Int32 |
| transaction_isolation_level | smallint | Int32, pois já havia CAST AS int | Preservado |
| @@TRANCOUNT | int | Int32 | Preservado |
| SqlConnection.ClientConnectionId | Propriedade do provider, não coluna SQL | Guid | Preservada |

Tipos conferidos na documentação oficial: [@@SPID](https://learn.microsoft.com/en-us/sql/t-sql/functions/spid-transact-sql?view=sql-server-ver16), [sys.dm_exec_sessions](https://learn.microsoft.com/en-us/sql/relational-databases/system-dynamic-management-objects/sys-dm-exec-sessions-transact-sql?view=sql-server-ver17) e [@@TRANCOUNT](https://learn.microsoft.com/en-us/sql/t-sql/functions/trancount-transact-sql?view=sql-server-ver16). A consulta agora coincide com a sondagem dos 26 testes P2 já validados na 6C:

```sql
SELECT CAST(@@SPID AS int), CAST(transaction_isolation_level AS int), @@TRANCOUNT
FROM sys.dm_exec_sessions WHERE session_id=@@SPID;
```

Nenhuma asserção foi removida: SPID/ClientConnectionId devem identificar a mesma sessão física antes/durante/depois; a consulta deve usar Snapshot e a sessão reutilizada voltar a isolamento **2**, com @@TRANCOUNT=0. Transação nula e conexão fechada antes de descartar o DbContext, resumo posterior **1,01/0/1,01**, identidade real, logout e token revogado **401** continuam obrigatórios com e sem filtro. Essas verificações posteriores **não foram alcançadas na execução 795/798** devido à leitura inicial incorreta e ainda precisam da nova execução externa.

### Escopo e verificações desta correção

Somente **ReportFailureSqlTests.cs**, **ReportConsistencySqlTests.cs** e este documento foram alterados entre os arquivos versionáveis da entrega. Os outros **15 arquivos locais** foram preservados por comparação de hashes SHA-256, incluindo toda a produção, README e script Docker. Nenhuma nova dependência, migration, alteração financeira, grant, autenticação, infraestrutura Snapshot ou funcionalidade 7B/7C. Contagem mantida em **798 testes**.

| Verificação no sandbox | Resultado efetivamente obtido |
|---|---|
| Geração SQL offline | Saída 0; estrutura EF reproduzida e projeção interna preservada após o ajuste; não executa SQL nem abre conexão |
| Restore com NuGet.Config | Saída 0; cinco NU1900 |
| Build Release | Saída 0, zero erros, cinco NU1900; sem warnings C#/analisadores |
| Suíte completa Release --no-build --no-restore | **798 reportados, 404 aprovados, 394 falhas de fixture Docker, zero ignorados**, saída 2, aproximadamente **29,2s** |
| Domain / Application | 116 / 166 aprovados |
| Integration | 122 sem SQL aprovados; 394 falhas por DockerUnavailableException antes das asserções SQL |
| Docker | Configuração/pipe docker_engine inacessíveis no sandbox; nenhuma proteção alterada |
| Git | main@d303fc2, 18 arquivos locais (cinco modificados/13 novos), staging vazio; git diff --check aprovado; sem stage/commit/push |

Não houve erro adicional de asserção ou carregamento identificado na execução local; os três casos SQL corrigidos continuam **sem aprovação no sandbox**. A auditoria NuGet permanece inconclusiva por NU1900, sem supressão de avisos. Logs ignorados: `.local/7a-sql-fix-restore.log`, `7a-sql-fix-build.log`, `7a-sql-fix-tests.log` e `7a-query-investigation.log`; o diagnóstico em `.local/7a-query-investigation` usa as referências existentes e dados/parametrização fictícios apenas para gerar SQL. Os XMLs/logs externos anteriores foram preservados.

**Parecer antes da segunda execução: CORREÇÃO DA ENTREGA 7A PRONTA PARA VALIDAÇÃO EXTERNA.** Faltava executar novamente o script integral e confirmar **798/798**, incluindo 8115 real e todas as asserções posteriores dos dois casos de sessão. Ainda não havia aprovação final, publicação ou CI da 7A.

## Segunda validação externa e auditoria final — 26/11/2026

Execução do **usuário** no Docker Linux/SQL Server/Testcontainers: **`.local/7a-docker-results/20261126-214221-889-68195a`**, com log consolidado em `.local/7a-docker-tests.log`. O Codex inspecionou os três XMLs completos, console.log e arquivos de exit code. Não executou novamente a suíte durante esta auditoria nem atribui a si a execução SQL externa.

| Projeto | Elementos test / total declarado | Aprovados | Falhas / ignorados / erros do runner |
|---|---:|---:|---|
| Domain | 116 / 116 | 116 | 0 / 0 / 0 |
| Application | 166 / 166 | 166 | 0 / 0 / 0 |
| Integration | 516 / 516 | 516 | 0 / 0 / 0 |
| Total | **798 / 798** | **798** | **0 / 0 / 0** |

Os códigos de saída de **restore, build, Domain, Application, Integration e Docker são todos 0**. A comparação com os XMLs 6C de `20261126-002527-480-4b4fce` confirmou **741 nomes e multiplicidades preservados**, sem remoção ou substituição, e exatamente **57 novos casos**. A comparação com a primeira execução 7A de 795/798 não encontrou mudança em nenhum nome/multiplicidade. Os novos grupos passaram integralmente: ReportsServiceTests 17, ReportContractTests 4, ReportsSqlTests 28, ReportConsistencySqlTests 4 e ReportFailureSqlTests 4.

### Encerramento das três falhas de instrumentação

**Overflow:** o caso `A_failed_aggregate_never_returns_partial_totals_and_a_later_read_succeeds(stage: overflow, status: ServiceUnavailable, sqlNumber: 8115)` consta como **Pass** no XML. A revisão do interceptor confirma que a alteração está limitada aos operandos THEN dos CASEs, preservando a projeção nomeada Amount da tabela derivada. A aprovação da asserção **Assert.Equal(8115, fault.SqlNumber)** comprova o erro SQL aritmético efetivamente observado na consulta interceptada; não se está inferindo o número apenas dos parâmetros/nome do caso. O console contém ocorrências reais de 8115, também provocadas por testes negativos anteriores; elas não são confundidas com falhas do runner nem usadas isoladamente para atribuir o erro ao novo teste.

As asserções seguintes, sem retorno antecipado, também foram alcançadas: HTTP **503**, no-store, application/problem+json, ausência de income/expense/result parciais e detalhes internos, seguida de nova consulta bem-sucedida com **income=2, expense=0, result=2**. Não houve aceitação de 8155, remoção de verificações, alteração de dados persistidos ou ampliação da classificação de erros de produção.

**Pooling com e sem conta:** os dois casos `Summary_restores_the_same_pooled_session_before_real_JWT_revocation_and_a_later_query` constam como **Pass**. Os outputs efetivamente registrados são:

| Filtro | SPID mantido | ClientConnectionId mantido | Isolamento antes / depois / final | @@TRANCOUNT |
|---|---:|---|---|---|
| False | 188 | a8f2a8ef-4dd5-46c7-9380-c5126b3e8c95 | 3 / 2 / 2 | 0 nas três sondagens |
| True | 189 | 2533a5a6-91d5-47d9-94a7-9ebabcb34f43 | 3 / 2 / 2 | 0 nas três sondagens |

O nível inicial **3** é RepeatableRead, observado após a preparação financeira; não se afirma que o log inicial era ReadCommitted. Os registros finais comprovam a mesma sessão física reutilizada em **2/ReadCommitted**, sem transação aberta, sem substituir a conexão por outra. A identidade combina SPID e ClientConnectionId, não somente um SPID potencialmente reciclado.

**Comprovado pelas asserções, não por uma sondagem DMV impressa durante a leitura:** o interceptor registra a sessão somente quando a transação SQL é Snapshot, e as igualdades de SPID/ClientConnectionId exigem esse registro da própria consulta. O log não imprime um nível DMV 5 nesse instante. O teste também alcança o resumo HTTP **1,01/0/1,01**, nova leitura direta do store com resultado **1,01**, transação EF nula e conexão fechada **antes do descarte do DbContext**, perfil HTTP 200 com a identidade do proprietário, logout 204 e relatório com token revogado **401**. A sondagem final igual à posterior ao resumo demonstra que a autenticação/revogação não mudou a sessão ou seu isolamento. A correção do CAST removeu apenas o impedimento de leitura Int16/Int32; todas essas verificações foram preservadas e executadas.

### Parecer técnico consolidado

**Sem achado técnico bloqueante ou correção funcional pendente.** Os 18 arquivos foram revisados contra d303fc2, incluindo os 13 novos. A separação Api/Application/Infrastructure permanece correta; Domain não recebeu dependências. O endpoint implementado é exclusivamente GET /api/v1/reports/summary e expõe os seis campos aprovados. Validação de período/data/GUID/query, identidade autenticada, histórico inativo, exclusões e 404 genérico permanecem alinhados aos contratos.

A agregação condicional ocorre no SQL, parametrizada, com UserId e intervalo inclusivo obrigatórios, conta opcional e exclusão de IsDeleted. Não há leitura de Transfers, saldo inicial, histórico em memória, N+1, saldo persistido ou agrupamento de categoria. Os testes comprovam zeros, sinais/centavos e agregados acima do limite individual, retroativos/edições/exclusões e a reconciliação independente **4500 - 500 = 4000**, distinta do patrimônio **11000**. As contas 8000/3000 e o cancelamento de transferência reconciliam com extratos/saldos em dados estáveis.

Propriedade e agregação compartilham SnapshotReadSession; os dois casos concorrentes confirmam receita ou exclusão de despesa em conexão independente após a primeira leitura Snapshot e antes da liberação do agregado. A resposta original mantém 10/3 e a seguinte reflete 17/3 ou 10/0. Não há sleeps como prova. A infraestrutura aprovada na 6C permanece intacta: limpeza sem token cancelado, timeout limitado, preservação da primeira exceção e invalidação restrita do pool, sem ClearAllPools, RCSI ou pooling global desabilitado. SnapshotReadFailure mantém a captura restrita 1205/-2/3952, sem conversão genérica de cancelamentos/exceções.

JWT/revogação, no-store, Problem Details, 429/Retry-After e OpenAPI/Bearer foram conferidos. O contrato OpenAPI documenta datas, filtro e magnitudes; os agregados não têm o limite máximo de uma operação individual. Swagger continua somente em Development. Os índices existentes por usuário/data e usuário/conta/data atendem à forma de acesso; não houve benchmark ou proposta de índice novo. Nenhuma migration, grant, dependência, workflow, idempotência, RowVersion, DbContext, autenticação ou regra financeira anterior foi alterada. Os testes anteriores permanecem sem edição e passaram na regressão externa.

### Integridade da auditoria e pendências

Nesta auditoria foram atualizados **exclusivamente README.md, docs/etapa-7/README.md e este documento**. Código, testes e script, abrangendo os demais **15 arquivos locais**, foram preservados por hashes SHA-256. Main@d303fc2, **18 arquivos locais: cinco modificados e 13 novos**, staging vazio; git diff --check aprovado. Nenhum segredo ou artefato indevido identificado nos arquivos versionáveis. Logs/diagnóstico em .local permanecem ignorados e preservados. Não houve stage, commit, push, mudança de proteções ou acesso/inicialização/modificação do ambiente persistente.

A ausência de NU1900 no console da execução externa **não constitui evidência de uma nova auditoria de vulnerabilidades conclusiva**; a pendência NuGet permanece. Os bloqueios Docker/NU1900 do sandbox continuam no histórico e não invalidam nem são substituídos por uma alegação de execução própria. Publicação autorizada e aprovação da CI própria da 7A ainda não ocorreram. 7B/7C e Etapa 8 permanecem não iniciadas; a Etapa 7 não está concluída.

**Parecer final: APROVADA PARA COMMIT.** As três falhas anteriores foram encerradas tecnicamente e a validação externa integral foi comprovada, sem impedimento bloqueante identificado. A aprovação não executa nem autoriza automaticamente stage, commit, push ou publicação nesta sessão.

## Reprodução externa e pendências

Executar no PowerShell com Docker Desktop Linux e recursos para SQL Server/Testcontainers:

```powershell
& C:\FinanceFlow\scripts\Test-DockerSuite.ps1
```

O script preserva montagem somente leitura do workspace, cópia para /work, restore/build no SDK Linux, três módulos completos, logs, XMLs e exit codes por etapa. Usa **`.local/7a-docker-tests.log` e `.local/7a-docker-results/<execucao>`**, preservando os históricos da 6C. Exige **798/798** (116 Domain, 166 Application, 516 Integration), três XMLs válidos, zero falhas/ignorados/erros do runner e Docker exit code 0.

Validação externa **798/798**, as três correções e auditoria final concluídas conforme a seção acima. Permanecem consulta NuGet conclusiva e eventual publicação/CI autorizadas. A 7A está **aprovada para commit**, ainda não publicada ou validada na CI própria. 7B, 7C e Etapa 8 não foram iniciadas. Nenhum stage, commit, push, mudança de segredo, banco/volume persistente ou segurança do Windows foi executado.
