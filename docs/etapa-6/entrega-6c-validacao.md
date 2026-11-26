# Entrega 6C — Listagem, extratos e reconciliação

## Estado atual e baseline — 26/11/2026

**APROVADA PARA COMMIT.** Validação externa final **741/741** e auditoria do P2 concluídas, conforme evidências abaixo. Implementação ainda local, sem publicação ou CI própria aprovada. A Etapa 6 só poderá ser integralmente encerrada após publicação/CI autorizadas; a auditoria NuGet continua inconclusiva.

Baseline conferida antes das alterações: **main@1cfb556 — feat: implement logical transfer cancellation**, working tree e staging vazios. O usuário confirmou a publicação da 6B com **613/613 aprovados** (116 Domain, 115 Application, 382 Integration) e **CI #11, execução 44870149140 — success**. Essas evidências anteriores pertencem à 6B, não à 6C.

Histórico de **25/11/2026**: a primeira execução externa da 6C apresentou **714/715 aprovados**, com uma falha na preparação do teste de Snapshot ausente. O setup foi corrigido pontualmente, conforme o registro abaixo. Naquele momento, a nova aprovação completa estava pendente; as validações posteriores e o encerramento do P2 estão registrados em suas respectivas seções.

Posteriormente, o usuário comprovou **715/715** em `20261125-222720-071-d1a9d5`. A auditoria de encerramento não aprovou o commit por uma pendência P2 sobre isolamento residual no pool. Essa pendência recebeu proteção localizada e **26 testes SQL adicionais**, total atual **741**. **P2 PRONTO PARA VALIDAÇÃO EXTERNA**, sem afirmar reprodução SQL ou aprovação da correção no sandbox. O histórico e as evidências anteriores continuam preservados; a 6C ainda não está aprovada para commit/publicação.

Foram consultados AGENTS.md, roadmap, modelo, contratos, critérios, ADRs 003/004/005/006/007 e os históricos de 5/6A/6B. Domain e entidades financeiras foram preservados. Não houve dependência, migration ou índice novo.

## Endpoints e validações

### GET /api/v1/transfers

JWT obrigatório. Filtros opcionais accountId/from/to; page=1, pageSize=20 (máximo 100), sort=occurredOn e direction=desc por padrão. Sort permite occurredOn/amount/id; direction permite asc/desc. Desempate por Id ascendente, exceto quando o próprio sort é id desc. Conta de filtro inexistente/alheia retorna o mesmo 404; Guid vazio é inválido. A conta pode estar inativa.

Datas são yyyy-MM-dd inclusivas, sem futuro em America/Sao_Paulo; from <= to quando ambos presentes. **Não há limite de 366 dias na listagem.** Parâmetros desconhecidos, duplicados, vazios ou malformados retornam 400, conforme o padrão de transactions. Offset que exceda int.MaxValue também é rejeitado antes do SQL.

Retorno `{ items, page, pageSize, totalCount, totalPages }`, com TransferView/versão opaca existente. Cada transferência própria não cancelada aparece uma vez; accountId corresponde à origem OU ao destino. Conta inativa não elimina histórico. Filtros, count, sort e paginação executam no SQL. Página além do fim retorna 200, items vazio e totais corretos. Referência, count e página compartilham uma transação Snapshot.

### GET /api/v1/accounts/{id}/statement

JWT obrigatório; from/to obrigatórios, exatos yyyy-MM-dd, inclusivos e sem futuro. Máximo de **366 datas incluídas**; diferença de DayNumber >= 366 é rejeitada, sem AddDays(-1) e sem overflow em DateOnly.MinValue/MaxValue. Page/pageSize seguem os mesmos limites. Não aceita sort ou outros parâmetros adicionais.

Conta própria ativa ou inativa é permitida; alheia/inexistente retorna 404 genérico. Não exige If-Match nem Idempotency-Key. Ambas as rotas preservam HTTPS, revogação JWT, rate limiting/Retry-After, no-store e Problem Details. Respostas: 200, 400, 401, 404, 429, 503 conhecido ou 500 seguro para erro inesperado.

## Contrato do extrato

AccountStatement expõe:

- accountId, currency, from, to;
- accountOpeningDate e accountOpeningBalance cadastrados;
- openingBalance do período e accountOpeningContribution;
- income, expense, result, transferIn, transferOut e closingBalance;
- items, page, pageSize, totalCount e totalPages.

Os totais income/expense/transferIn/transferOut são **magnitudes positivas**. Result = income - expense, sem saldo cadastrado ou transferências. Nos movimentos, amount é **assinado**:

| Nature | OperationType | Amount | Categoria | Contraparte |
|---|---|---|---|---|
| income | transaction | positivo | CategoryId | null |
| expense | transaction | negativo | CategoryId | null |
| transferIn | transfer | positivo | null | SourceAccountId |
| transferOut | transfer | negativo | null | DestinationAccountId |

Cada StatementMovement contém operationId original, operationType, nature, occurredOn, amount, description, categoryId e counterpartyAccountId. **OperationType discrimina FinancialTransaction.Id de Transfer.Id**, inclusive se os GUIDs coincidirem entre tabelas. Não existe novo identificador persistido, categoria fictícia, linha de abertura nem saldo acumulado por item. UserId e entidades EF não são expostos.

Ordenação fixa: occurredOn desc, operationId asc, operationType asc. O último critério desempata referências iguais de fontes diferentes. SQL faz a paginação após UNION ALL das duas fontes originais, sem N+1 ou histórico inteiro em memória. Transações excluídas e transferências canceladas são filtradas antes de somar/paginar. Contas/categorias inativas não removem movimentos válidos.

## Abertura, fechamento e precisão

OpeningBalance do extrato é o saldo cadastrado mais movimentos válidos estritamente anteriores a from, somente quando accountOpeningDate <= from. Movimentos em from entram no período.

Se a conta abre depois de from e até to, openingBalance=0 e **accountOpeningContribution=accountOpeningBalance**. Antes ou exatamente em from, essa contribuição é zero porque a abertura já integra openingBalance. Se abre após to, ambas as posições são zero e não há movimentos válidos.

```text
closingBalance = openingBalance + accountOpeningContribution
               + income - expense + transferIn - transferOut
```

Assim, conta aberta durante o período com 800, receita 200 e despesa 50 retorna openingBalance=0, contribuição=800, income=200 e closingBalance=950. Abertura nunca é receita. O histórico representa o estado atualmente válido dos registros, conforme ADR-004; editar/excluir/cancelar retroativamente altera os cortes afetados, sem estorno fictício na data técnica.

SUM e count executam no SQL Server, com operandos em decimal e agregados SQL decimal(38,2). As parcelas separadas são combinadas em C# com **checked decimal**, preservando escala e evitando redução de escala pela soma de expressões SQL decimal(38,2). Nenhum float/double ou arredondamento silencioso foi introduzido. Falhas de conversão/overflow não retornam extrato parcial. O endpoint /balance e sua implementação permaneceram intactos; testes independentes com dados controlados comparam closingBalance ao corte to.

## Consistência e requisito operacional SQL

Cada extrato usa **uma transação IsolationLevel.Snapshot** para a leitura da conta, os agregados completos e a página. A leitura da conta estabelece a visão antes das consultas seguintes; alterações concorrentes confirmadas depois não misturam versões na resposta. Somente após concluir as leituras e o commit é retornado sucesso. Falhas/cancelamento descartam a transação e liberam a conexão; não há retry ou fallback para ReadCommitted.

Isso também vale para count/página da listagem. **Requisições de páginas diferentes não compartilham Snapshot**; alterações entre requisições podem mudar a paginação. Não se afirma que duas respostas HTTP independentes de /balance e /statement sejam do mesmo instante durante escritas.

O provisionamento administrativo explícito `DatabaseTasks.ProvisionAsync` agora habilita **ALLOW_SNAPSHOT_ISOLATION ON** no banco FinanceFlow validado. Usa a conexão administrativa e QUOTENAME, preserva credenciais existentes e não habilita READ_COMMITTED_SNAPSHOT. Essa opção não é alteração do modelo EF: **nenhuma migration/snapshot histórico foi editado ou criado**. Índices existentes de usuário/conta/data atendem aos padrões da consulta; não foram acrescentados índices sem medição.

**Nada foi aplicado ao banco persistente nesta execução.** Para um banco existente da 6B, o operador precisará autorizar e executar o provisionamento explícito com a imagem atualizada e as configurações externas já previstas:

```powershell
# Somente após autorização específica para o banco persistente e reconstrução da imagem.
docker compose run --rm provision
```

Não é necessário executar migrations para 6C. Em ambiente novo, permanece a sequência documentada de provision/migrations/provision. A API normal não configura opções nem aplica migrations no startup. A credencial restrita continua com os mesmos SELECTs e grants por coluna da 6B; **nenhum privilégio novo** foi concedido para leitura ou configuração.

Readiness consulta snapshot_isolation_state e exige valor 1, com conversão explícita de tinyint para int. Configuração ausente retorna 503/notReady, sem afetar liveness. Listagem/extrato não oferecem fallback: SQL **3952** (Snapshot não permitido), **1205** e **-2**, diretos ou nos wrappers conhecidos, retornam indisponibilidade segura. Erros inesperados e OperationCanceledException não são absorvidos por esse filtro.

Referências oficiais utilizadas: [Snapshot/ALLOW_SNAPSHOT_ISOLATION](https://learn.microsoft.com/en-us/sql/t-sql/statements/alter-database-transact-sql-set-options?view=sql-server-ver17), [erro 3952](https://learn.microsoft.com/en-us/sql/relational-databases/errors-events/database-engine-events-and-errors-3000-to-3999?view=sql-server-ver17), [SqlQuery com tipos não persistidos](https://learn.microsoft.com/en-us/ef/core/querying/raw-sql).

## Cobertura acrescentada

Baseline preservada: **613 casos**. Novos testes: **102**, total descoberto **715**.

| Arquivo novo de testes | Casos | Cobertura principal |
|---|---:|---|
| AccountStatementsServiceTests | 17 | Proprietário, ausência de identidade, paginação, 366/367 datas, extremos e virada UTC/São Paulo |
| TransferListingServiceTests | 17 | Filtros, datas opcionais sem limite implícito, sort/direction, paginação, proprietário e encaminhamento |
| FinancialReadContractTests | 5 | JWT obrigatório/inválido, no-store e OpenAPI dos novos contratos |
| TransferListingSqlTests | 6 | Seis ordenações, empates SQL de GUID, origem/destino, páginas, canceladas/inativas, isolamento e conta alheia |
| AccountStatementSqlTests | 11 | Aberturas, quatro naturezas, reconciliação independente, contraparte, 152 movimentos/centavos, empates entre fontes, retroativos/edição/exclusão/cancelamento |
| FinancialReadValidationSqlTests | 36 | Query estrita, datas, filtros, erros seguros, 366 dias, conta inativa, isolamento e revogação |
| StatementConsistencySqlTests | 7 | Quatro escritores concorrentes, recursos após falha/cancelamento, configuração/ausência de Snapshot, readiness, RCSI desligado e modelo sem divergência |
| FinancialReadFailureSqlTests | 3 | Timeout SQL real nas duas rotas, overflow 8115 interno e ausência de resposta financeira parcial |

FinancialReadRequests contém apenas auxiliares/host de testes, sem casos adicionais. Os dois doubles existentes de ITransferStore ganharam o método novo sem alterar expectativas. Dois testes OpenAPI anteriores foram atualizados somente para reconhecer GET na coleção, preservando POST e DELETE; nenhum caso anterior foi removido/renomeado/ignorado.

Testes concorrentes usam DbContexts/conexões independentes, barreiras e interceptores. O escritor confirma antes da liberação do agregado, após a leitura Snapshot da conta; a primeira resposta deve manter o estado anterior e uma nova resposta deve refletir o estado confirmado. Não há sleeps como prova de ordenação. Timeout usa WAITFOR/CommandTimeout apenas para provocar erro real. O teste de overflow amplia operandos somente no comando interceptado do teste, exigindo SQL 8115 em QueryIterationFailed/CommandFailed e erro HTTP sanitizado; não altera dados ou cálculo de produção.

Os testes de configuração e permissões usam a conta restrita real e bancos descartáveis. O teste de configuração desliga Snapshot **somente no banco isolado que ele próprio criou**, comprova SQL 3952, readiness 503/liveness 200 e erro seguro nas duas rotas. Os grants restritos existentes, RowVersion, rollback, idempotência e SQL 1205/-2 anteriores continuam na regressão obrigatória.

## Arquivos da entrega

Inventário da implementação original, antes do P2 de pooling: **33 arquivos, 16 modificados e 17 novos**. Não havia alterações preexistentes no início daquela implementação. A intervenção P2 abaixo preserva esse trabalho e acrescenta dois arquivos, resultando em **35 arquivos locais: 16 modificados e 19 novos**.

| Grupo | Modificados | Novos |
|---|---|---|
| Api | Program.cs; Configuration/ApiDocumentationConfiguration.cs; Controllers/TransfersController.cs e HealthController.cs | Controllers/AccountStatementsController.cs; Contracts/Statements/StatementRequest.cs; Contracts/Transfers/TransferListRequest.cs |
| Application | Transfers/TransferContracts.cs e TransfersService.cs | Statements/StatementContracts.cs e AccountStatementsService.cs |
| Infrastructure | DependencyInjection.cs; Persistence/DatabaseTasks.cs; Persistence/Transfers/TransferStore.cs | Persistence/SnapshotReadFailure.cs; Persistence/Statements/AccountStatementStore.cs |
| Application.Tests | TransfersServiceTests.cs; TransferCancellationServiceTests.cs | AccountStatementsServiceTests.cs; TransferListingServiceTests.cs |
| IntegrationTests | TransferContractTests.cs; TransferCancellationContractTests.cs | AccountStatementSqlTests.cs; FinancialReadContractTests.cs; FinancialReadFailureSqlTests.cs; FinancialReadRequests.cs; FinancialReadValidationSqlTests.cs; StatementConsistencySqlTests.cs; TransferListingSqlTests.cs |
| Execução/documentação | scripts/Test-DockerSuite.ps1; README.md; docs/etapa-6/README.md | docs/etapa-6/entrega-6c-validacao.md |

Domain, migrations históricas, snapshot cumulativo, contas/categorias/transações e os fluxos de escrita/idempotência permaneceram intactos. As responsabilidades seguem ADR-001: Api valida HTTP, Application identifica proprietário/valida consultas, Infrastructure consulta SQL; nenhuma entidade derivada no Domain.

## Verificações executadas pelo Codex

| Verificação | Resultado real |
|---|---|
| Restore com NuGet.Config | Saída 0; cinco NU1900 |
| Build Release | Sete projetos, saída 0, zero erros e cinco NU1900; sem warnings C#/analisadores |
| Testes | 715 descobertos/executados pelo runner; 383 aprovados, 332 falhas de fixture Docker, zero ignorados, saída 2 |
| Domain | 116 aprovados |
| Application | 149 aprovados, incluindo os 34 novos |
| Integration | 118 aprovados sem SQL; 332 falhas antes das asserções SQL por DockerUnavailableException |
| Runner | Nenhuma falha adicional de handshake/carregamento identificada; saída 2 decorre das 332 falhas de fixture; última execução em aproximadamente 29,4s |
| Docker | config.json negado; pipe docker_engine inacessível no sandbox; nenhuma proteção alterada |
| Auditoria NuGet | Inconclusiva: comando package --vulnerable --include-transitive saiu 1; índice api.nuget.org:443 bloqueado e NU1900 |
| Script | Sintaxe PowerShell válida, CRLF preservado; três módulos integrais, nova guarda de 715/715 e logs 6c separados |
| Git | git diff --check aprovado; staging vazio; sem stage/commit/push |

Logs ignorados em `.local`: `6c-restore.log`, `6c-build.log`, `6c-build-final.log`, `6c-tests.log`, `6c-tests-final.log`, `6c-nuget-audit.log`. A execução inicial e a final são registros do sandbox; não comprovam os novos cenários SQL. Não houve erro funcional nos casos executados sem SQL. As falhas de fixture não foram tratadas como testes aprovados nem mascaradas com skips/InMemory/SQLite.

## Primeira validação externa e correção pontual — 25/11/2026

O usuário executou `scripts/Test-DockerSuite.ps1` no PowerShell com Docker Linux e SQL Server/Testcontainers. O Codex conferiu os três XMLs e os arquivos de exit code em **`.local/6c-docker-results/20261125-215344-669-a1e15c`**; esta não foi uma execução SQL pelo sandbox.

| Projeto | Executados | Aprovados | Falhas | Ignorados | Erros do runner |
|---|---:|---:|---:|---:|---:|
| Domain | 116 | 116 | 0 | 0 | 0 |
| Application | 149 | 149 | 0 | 0 | 0 |
| Integration | 450 | 449 | 1 | 0 | 0 |
| Total | 715 | 714 | 1 | 0 | 0 |

Restore, build, Domain e Application tiveram saída 0; Integration e Docker, saída 1. A única falha foi `FinanceFlow.IntegrationTests.StatementConsistencySqlTests.Explicit_snapshot_configuration_is_required_detected_by_readiness_and_does_not_enable_RCSI_or_change_model`, na **antiga linha 165**: esperado **503 ServiceUnavailable**, recebido **401 Unauthorized**. A requisição era o primeiro item do loop, **GET /api/v1/transfers**; a consulta de statement no segundo item ainda não havia sido alcançada.

### Diagnóstico e causa

O helper registra um usuário fictício, realiza login real e associa o Bearer a DefaultRequestHeaders. O mesmo cliente já havia criado a conta e obtido o statement com sucesso. O caso durou **3,033 segundos**, enquanto o emissor configura expiração de 15 minutos. Não há logout, desativação ou alteração de TokenVersion nesse caso, e o banco é exclusivo do teste.

Depois de desligar ALLOW_SNAPSHOT_ISOLATION, o `console.log` da execução registra **SQL 3952** repetidamente, a partir da linha 10239, com o mesmo ClientConnectionId. A configuração usava a mesma connection string com pooling para o host HTTP, a leitura prévia de statement e a sondagem explícita em Snapshot. O nível de isolamento da sessão reutilizada pode continuar em Snapshot: esse comportamento de pooling é [documentado pela Microsoft](https://learn.microsoft.com/en-us/dotnet/framework/data/adonet/sql/snapshot-isolation-in-sql-server).

A análise conjunta do log e do fluxo identifica o problema de setup: uma sessão com isolamento Snapshot era reutilizada depois de a opção ser desabilitada. `AuthenticationConfiguration.OnTokenValidated` consulta `UserTokenState.IsCurrentAsync` para verificar IsActive/TokenVersion; quando essa leitura falha com SQL 3952, chama `context.Fail("Authentication unavailable.")`. O challenge responde **401 antes do controller/store financeiro**. Esse bloqueio seguro não é defeito a contornar na autenticação. O filtro do store financeiro já reconhece SQL 3952 como indisponibilidade, mas o teste não chegava à consulta protegida que pretendia exercitar.

Essa identificação usa os resultados externos existentes e a inspeção do código; não houve reprodução SQL adicional no sandbox. As novas pré-condições HTTP tornam explícita a disponibilidade da autenticação durante a próxima execução.

### Correção mínima

Somente o setup desse método foi alterado para **Pooling=false na connection string da aplicação do banco descartável criado pelo próprio caso**. Assim, as chamadas HTTP e a sondagem SQL não compartilham uma sessão física com isolamento residual. O pooling da aplicação normal e das demais fixtures não foi modificado; não há ClearPool global, reset de sessão em produção ou bypass de JWT.

O teste agora exige **GET /api/v1/users/me = 200**, com o mesmo Id do proprietário autenticado, antes e depois de desligar Snapshot. Isso comprova no pipeline real a validação de assinatura/lifetime e a consulta de estado/revogação antes das leituras financeiras. Os dois GETs financeiros continuam exigindo **exatamente 503**, code service_unavailable, ausência de WWW-Authenticate e de detalhes SQL/saldo parcial.

Permanecem intactas as asserções de modelo sem divergência, Snapshot inicialmente habilitado, RCSI desligado, privilégios restritos, erro SQL **3952 real**, readiness 503 e liveness 200. A opção é desligada apenas em `FinanceFlow6C_<Guid>` criado pelo teste; o banco compartilhado da fixture e bancos persistentes não são alterados. O banco isolado pertence ao contêiner descartável. Não há fallback para ReadCommitted na consulta financeira.

Nenhum código de produção, middleware, contrato HTTP, migration, privilégio, cálculo financeiro ou script foi alterado nesta correção. Nenhum teste foi removido, renomeado ou ignorado. O total permanece **715**, e os outros 714 casos aprovados externamente foram preservados.

### Verificações desta correção pelo Codex

- Restore com NuGet.Config: saída **0**, cinco NU1900.
- Build Release: saída **0**, sete projetos, zero erros e cinco NU1900, sem warnings C#/analisadores.
- Suíte disponível: **715 reportados, 383 aprovados, 332 falhas na fixture Docker, zero ignorados**, saída **2**, aproximadamente 33,5 segundos. Domain: 116 aprovados; Application: 149; Integration: 118 sem SQL aprovados e 332 falhas antes das asserções SQL, todas com DockerUnavailableException.
- Docker continua bloqueado no sandbox por acesso negado a config.json e ao pipe docker_engine. Nenhuma proteção foi alterada. **O caso corrigido e a suíte SQL ainda não foram aprovados após o ajuste.**
- Logs ignorados: `.local/6c-snapshot-fix-restore.log`, `6c-snapshot-fix-build.log` e `6c-snapshot-fix-tests.log`. Evidências externas anteriores preservadas.
- `git diff --check` aprovado e staging vazio. Somente este documento e `StatementConsistencySqlTests.cs` foram alterados nesta intervenção; os outros 31 arquivos locais da 6C foram preservados por comparação de hashes. Nenhum stage, commit ou push.

## P2 — Sessões físicas reutilizadas pelo pool

### Baseline externa e parecer da auditoria

A segunda execução externa do usuário, **`.local/6c-docker-results/20261125-222720-071-d1a9d5`**, comprovou **715/715 aprovados**, com três XMLs válidos: **116 Domain, 149 Application e 450 Integration**, zero falhas, ignorados e erros do runner. Restore/build/módulos/Docker retornaram 0. O caso de configuração Snapshot anteriormente falho passou, alcançando os perfis autenticados antes/depois, mesma identidade, SQL 3952, readiness 503, liveness 200, duas respostas financeiras 503, RCSI desligado e modelo sem divergência. Essa é evidência externa anterior à correção de pooling, não validação da mudança descrita a seguir.

A auditoria posterior emitiu **NÃO APROVADA PARA COMMIT**: AccountStatementStore.GetAsync e TransferStore.ListAsync encerravam a transação sem garantir o isolamento da sessão devolvida ao pool. O teste com Pooling=false isola adequadamente a ausência da opção SQL, mas não prova o ciclo de vida da aplicação normal com pooling. Não houve corrupção financeira ou SQL 3960 demonstrado nos logs existentes.

### Fatos, documentação e limites da investigação

Versões preservadas: **Microsoft.Data.SqlClient 6.1.8**, **EF Core SQL Server 10.0.13**, SQL Server 2022, imagem da fixture `2022-CU22-GDR1-ubuntu-22.04`.

- **Documentado:** o isolamento é estado de sessão e pode permanecer numa conexão física reutilizada pelo pool. Commit/rollback/Dispose da transação não devem ser tratados como garantia de retorno a ReadCommitted. [Documentação ADO.NET](https://learn.microsoft.com/en-us/dotnet/framework/data/adonet/sql/snapshot-isolation-in-sql-server).
- **Inspecionado na versão usada:** [SqlClient 6.1.8 — ResetConnection](https://raw.githubusercontent.com/dotnet/SqlClient/v6.1.8/src/Microsoft.Data.SqlClient/netcore/src/Microsoft/Data/SqlClient/SqlInternalConnectionTds.cs) prepara o reset do provider; o código da aplicação não pode inferir disso uma restauração explícita do isolamento exigido por todas as operações.
- **Inspecionado no EF:** [RelationalTransaction 10.0.13](https://raw.githubusercontent.com/dotnet/efcore/v10.0.13/src/EFCore.Relational/Storage/RelationalTransaction.cs) limpa a transação/fecha a referência de conexão após commit, rollback e descarte. [BatchExecutor 10.0.13](https://raw.githubusercontent.com/dotnet/efcore/v10.0.13/src/EFCore.Relational/Update/Internal/BatchExecutor.cs) pode executar um único comando sem iniciar outra transação; SaveChangesAsync não é uma garantia geral de redefinição do isolamento.
- **Observado anteriormente:** SQL 3952 após desligar Snapshot, com reutilização de conexão e 401 antes da consulta financeira. A autenticação permanece corretamente fechada quando não consegue verificar IsActive/TokenVersion.
- **Ainda não observado pelo Codex:** valores reais de SPID/isolation durante os novos testes, retenção após cada caminho do provider e eventual SQL 3960 em uma disputa específica. Docker continua inacessível. Não se afirma que toda versão/caminho do pool necessariamente retenha nível 5 ou que já tenha havido regressão financeira.

O controle SQL novo registra commit e rollback manuais, sem apresentá-los como prova dos stores. Ele exige a mesma sessão física e registra se o provider devolveu nível 2 ou 5. As verificações dos fluxos reais são mais fortes: **após os stores, a sessão reutilizável deve estar em nível 2**, ou deve haver descarte comprovado da sessão comprometida. Esses resultados precisam ser lidos nos XMLs externos para concluir empiricamente o diagnóstico.

### Correção localizada e ciclo de vida

Novo componente interno **SnapshotReadSession**, utilizado somente pelos dois stores de leitura da 6C. Não há interceptor global, Repository, mudança de porta, alteração em Domain/Application ou biblioteca nova. As expressões financeiras, projeções, filtros, ordenação, contagem e paginação foram preservadas em métodos privados de consulta.

1. Obter a conexão SqlConnection do mesmo FinanceFlowDbContext e identificar sua propriedade. Se já houver transação EF, rejeitar o uso sem tomar posse dela.
2. Abrir explicitamente a conexão física quando ela pertence à operação. O EF respeita uma conexão aberta externamente e não a devolve ao pool ao encerrar sua própria transação.
3. Iniciar a transação **Snapshot** pelo EF, executar as consultas e confirmar. Nenhum resultado de sucesso é devolvido antes do commit.
4. Descartar a transação; em falha/cancelamento, o descarte reverte a unidade. A conexão física permanece sob posse do componente para limpeza.
5. Na mesma sessão, sem transação ativa, executar **SET TRANSACTION ISOLATION LEVEL READ COMMITTED**, com **CancellationToken.None** e timeout de comando limitado a **cinco segundos**. Restaurar o timeout configurado no DbContext depois dessa instrução.
6. Só depois fechar a conexão que a operação abriu, permitindo retorno ao pool. Se a conexão já pertencia ao chamador, preservá-la aberta com o isolamento normalizado.
7. Preservar a primeira exceção e sua stack por ExceptionDispatchInfo. Uma falha de limpeza não substitui a falha de consulta ou OperationCanceledException. Quando a limpeza é a única falha, ela é propagada; não se devolve sucesso ou resposta financeira parcial nesse caso.

Se Dispose/reset falhar ou a conexão estiver quebrada/fechada inesperadamente, o componente não presume restauração: usa **SqlConnection.ClearPool(connection)** para aposentar a geração do **pool específico dessa conexão**, antes do retorno da sessão. **Nunca ClearAllPools**, nem limpeza de pools não relacionados. O provider oferece invalidação por chave de pool, não expulsão pública de uma única sessão; portanto, outras sessões da mesma geração também serão aposentadas quando retornarem, sem interrupção dos empréstimos em curso. Esse custo está restrito a falhas de limpeza, não às leituras normais. Evento de log **6001/SnapshotPoolRetired** não contém credenciais, tokens, connection string ou dados financeiros.

Os filtros existentes de SQL 1205/-2/3952 continuam restritos. Não há retry financeiro ou conversão de falhas de autenticação para 503. O reset ocorre **depois** das leituras e do término da transação, nunca substituindo Snapshot durante a formação do extrato ou da listagem. Snapshot/RCSI/grants/migrations e configuração de startup não foram alterados pelo P2.

### Reprodução determinística e cobertura nova

Arquivo **SnapshotConnectionPoolingSqlTests.cs**, **26 casos SQL reais**, sem skips, SQLite, InMemory, delays de ordenação ou SQL 3960 fabricado. Cada caso cria banco descartável exclusivo, com as credenciais restritas da API, Snapshot habilitado, ApplicationName único, **Pooling=true/Max Pool Size=1**. O escritor concorrente usa conexão independente de migration, sem disputar o slot da API. A observação de bloqueios usa a credencial administrativa existente apenas no banco/contêiner de teste; nenhum privilégio adicional é concedido à API.

O registro usa **@@SPID + SqlConnection.ClientConnectionId**, além de transaction_isolation_level da própria linha em sys.dm_exec_sessions e @@TRANCOUNT. Ambos os identificadores devem coincidir para afirmar reutilização; SPID reciclado numa conexão nova não é aceito como restauração da sessão antiga. São registrados: baseline antes, nível **5** durante o store, transação encerrada antes do reset, nível **2** após reset e antes do fechamento, e nível **2** no novo empréstimo. As observações são escritas no output do teste/XML, sem tokens ou senhas. O helper de output é capturado no teste e passado à instrumentação, sem depender do ExecutionContext do TestServer.

| Grupo | Casos | Garantias exigidas |
|---|---:|---|
| Controle do provider | 2 | Commit/rollback, mesma sessão antes/durante/depois, observação do reset/retorno ao pool sem pressupor retenção obrigatória |
| Sucesso dos dois stores | 2 | Snapshot nível 5, mesma sessão restaurada para 2, listagem/contagem e saldo 105/result 7 do conjunto fictício |
| JWT e revogação após reutilização | 2 | Perfil 200 do mesmo proprietário, logout 204 e token revogado 401 no pool real |
| Conta/categoria — alteração válida | 4 | Reutilização após cada store, UPDATE em nível 2, 204/novo ETag, versão antiga 412, saldo preservado; output indica se EF criou transação |
| Conta/categoria — disputa real | 4 | Escritor independente segura lock e avança RowVersion; DMV confirma UPDATE da API bloqueado antes do commit; API exige 412, sem sobrescrita; SQL 3960 não é fabricado nem aceito no lugar do contrato |
| Timeout, falha inesperada e cancelamento | 6 | Timeout SQL -2 real/503, erro inesperado/500 seguro, cancelamento de SQL aguardando WAITFOR observado na DMV; OCE ou SqlException de atenção validada, limpeza sem token cancelado e sem resposta parcial |
| Falha de restauração | 6 | IOException de reset ou timeout SQL -2 real, preservação da IOException original quando ambas falham, invalidação registrada, conexão substituta em nível 2 e pool não relacionado preservado |

O cancelamento usa um CancellationTokenSource real; só cancela após observar WAITFOR no SQL Server, sem sleep como prova. A disputa de UPDATE também é liberada apenas após observar LCK_M_% com o bloqueador esperado. Conexões/DbContexts não são compartilhados entre operações concorrentes.

As chamadas diretas aos stores conferem transação nula e conexão fechada **antes de descartar o escopo de serviços**, evitando que o Dispose do DbContext esconda um vazamento. Esse caminho complementa os endpoints HTTP e é exercitado em sucesso, cancelamento e preservação da falha original.

Em falha, o teste distingue **restauração da mesma sessão** de **substituição após descarte**. Se houver descarte, exige evento 6001, ClientConnectionId diferente e nível 2 da substituta; isso não é reportado como restauração da sessão antiga. A comparação com um pool saudável de outra chave impede que ClearAllPools passe despercebido.

O teste anterior **Explicit_snapshot_configuration_is_required_detected_by_readiness_and_does_not_enable_RCSI_or_change_model** permanece integralmente preservado, inclusive Pooling=false, autenticação real antes/depois e ambas as respostas 503. Os 715 casos anteriores não foram removidos, renomeados ou ignorados.

### Resultados efetivamente obtidos nesta intervenção

- Restore: saída **0**, cinco NU1900; auditoria NuGet continua inconclusiva.
- Build Release: sete projetos, saída **0**, zero erros e cinco NU1900, sem warnings C#/analisadores.
- Testes disponíveis: **741 reportados, 383 aprovados, 358 falhas na fixture Docker, zero ignorados**, saída **2**. Domain 116 aprovados, Application 149 aprovados; Integration 118 sem SQL aprovados e 358 falhas de fixture, incluindo os 26 novos. A tentativa inicial levou aproximadamente 31,2s; a final, após revisar a instrumentação, **28,3s**. Nenhuma falha adicional de handshake/módulo.
- **Nenhum novo teste SQL ou controle de retenção foi executado com sucesso no sandbox.** As falhas são DockerUnavailableException antes das asserções. A correção ainda não tem prova empírica no SQL Server; resultados de SPID/isolamento, cancelamento e disputa devem ser conferidos externamente.
- Logs ignorados: `.local/6c-pooling-restore.log`, `6c-pooling-build.log`, `6c-pooling-tests.log`, `6c-pooling-build-final.log` e `6c-pooling-tests-final.log`. Evidências externas 6A/6B/6C e o histórico 714/715 e 715/715 foram preservados.
- Escopo desta intervenção: criados SnapshotReadSession.cs e SnapshotConnectionPoolingSqlTests.cs; modificados AccountStatementStore.cs, TransferStore.cs, Test-DockerSuite.ps1 e este documento. Os outros **29 arquivos locais** foram preservados por hashes. Total no Git: **35 arquivos, 16 modificados e 19 novos**, staging vazio. Sem stage/commit/push ou alterações em banco persistente.

**Parecer naquele momento: P2 PRONTO PARA VALIDAÇÃO EXTERNA.** Faltava comprovar os novos casos e a regressão completa, analisar os outputs de sessão e fechar a auditoria. Ainda não havia aprovação final da 6C para commit.

### Execução externa do P2: 739/741 e correção dos dois cancelamentos

Execução do **usuário**, não do sandbox: **`.local/6c-docker-results/20261125-234414-181-bbd8c2`**. Os três XMLs foram conferidos: Domain **116/116**, Application **149/149**, Integration **474/476**; total **739 aprovados, duas falhas, zero ignorados e zero erros do runner**. Restore/build/Domain/Application tiveram saída 0; Integration e Docker, saída 1. Os 715 casos anteriores e 24 dos 26 casos novos passaram.

As duas falhas ocorreram exclusivamente em **SnapshotConnectionPoolingSqlTests.Failed_or_canceled_store_reads_leave_only_safe_pool_sessions**, casos `statement: False, stage: "cancel"` e `statement: True, stage: "cancel"`, na antiga linha **229**. A asserção exigia OperationCanceledException, mas o SqlClient entregou diretamente **SqlException**, com as mensagens de erro severo e cancelamento pelo usuário. A stack aponta para ExecuteReaderAsync e a primeira leitura de cada store; SnapshotReadSession linha 125 relança a primeira exceção preservada por ExceptionDispatchInfo, sem convertê-la.

Os outputs dos dois XMLs já demonstram que a limpeza ocorreu **antes** dessa asserção: mesma sessão/SPID e ClientConnectionId, isolamento **5** e transação ativa durante a leitura, transação encerrada antes do reset, isolamento **2** após o reset. Para a listagem, SPID **107**; para o extrato, SPID **109**. Isso não comprova as verificações seguintes de reutilização e nova consulta: elas não foram alcançadas nessa execução devido à asserção do tipo da exceção.

**Causa e decisão:** a expectativa exclusiva do teste era incorreta para cancelamento de um comando já em execução. No [SqlCommand do SqlClient 6.1.8](https://raw.githubusercontent.com/dotnet/SqlClient/v6.1.8/src/Microsoft.Data.SqlClient/netcore/src/Microsoft/Data/SqlClient/SqlCommand.cs), o token é registrado para cancelar o comando; CleanupExecuteReaderAsync preserva a exceção de uma tarefa com falha. O [TdsParser 6.1.8](https://raw.githubusercontent.com/dotnet/SqlClient/v6.1.8/src/Microsoft.Data.SqlClient/netcore/src/Microsoft/Data/SqlClient/TdsParser.cs), após receber o acknowledgment de atenção, pode acrescentar SqlError de cancelamento, sem transformá-lo em OCE. O [detector do EF Core SQL Server 10.0.13](https://raw.githubusercontent.com/dotnet/efcore/v10.0.13/src/EFCore.SqlServer/Storage/Internal/SqlServerExceptionDetector.cs) reconhece o token cancelado para registrar CommandCanceled; RelationalCommand relança a exceção recebida. Nenhum contrato financeiro exige normalizar esse cancelamento em outro tipo CLR. Foi escolhida a **correção somente do teste**, preservando a exceção original e toda a produção.

Os XMLs/logs existentes **não registraram os números, estados e classes da coleção SqlErrors nesses dois casos**; esses valores não são apresentados como observados. O código-fonte do TdsParser define o erro de atenção com **Number=0, State=0 e Class=11** ([MIN_ERROR_CLASS](https://raw.githubusercontent.com/dotnet/SqlClient/v6.1.8/src/Microsoft.Data.SqlClient/src/Microsoft/Data/SqlClient/TdsEnums.cs)), também usados para o aviso severo não fatal. Essa é a assinatura exigida pela nova asserção; os valores efetivamente recebidos serão escritos no output/XML da próxima execução. A mensagem de cancelamento é uma verificação complementar, nunca a única evidência.

O ajuste em **SnapshotConnectionPoolingSqlTests.cs** exige:

- WAITFOR efetivamente observado na DMV da sessão correta, tarefa ainda em execução e token ainda não cancelado antes do cancelamento controlado.
- Token da leitura igual ao token cancelado pelo teste, sem cancelamento do token geral do runner; evento CommandCanceled vinculado ao CommandId da leitura original, não ao comando de restauração.
- OCE ou **SqlException direta**, esta obrigatoriamente da mesma ClientConnectionId, com coleção não vazia, todos os erros com a assinatura acima e presença da mensagem de atenção. Deadlock, timeout, erros independentes ou wrappers não demonstrados não são aceitos genericamente.
- Nenhum retorno de sucesso para a leitura cancelada; verificações originais de transação nula/conexão fechada antes do Dispose do DbContext, reutilização segura ou descarte registrado, SPID/ClientConnectionId/isolamento e nova consulta financeira com saldo **105** permanecem obrigatórias após a captura.

Não houve alteração em SnapshotReadSession, SnapshotReadFailure, stores, autenticação, revogação, privilégios, migrations, fórmulas financeiras ou no script Docker. A quantidade permanece **741 testes**. Somente este documento e o arquivo de testes foram alterados nesta correção; os outros arquivos locais foram preservados. Os logs e XMLs externos anteriores permanecem intactos. Uma nova execução externa completa é necessária para comprovar os dois casos e alcançar todas as asserções posteriores.

**Verificações desta correção pelo Codex (26/11/2026):**

- Restore: saída **0**, cinco NU1900.
- Build Release: saída **0**, zero erros e cinco NU1900; sem warnings C#/analisadores.
- Suíte completa disponível: **741 reportados, 383 aprovados, 358 falhas na fixture Docker e zero ignorados**, saída **2**, duração aproximada **30,1 segundos**. Domain 116 aprovados, Application 149 aprovados; Integration 118 sem SQL aprovados e 358 falhas de fixture por DockerUnavailableException. **Os dois casos corrigidos não chegaram às asserções SQL no sandbox.**
- Docker continua inacessível: acesso negado ao config.json e ao pipe docker_engine. Nenhuma proteção foi alterada. A execução externa 739/741 não é atribuída ao Codex nem substituída por esta tentativa local.
- Logs locais ignorados: `.local/6c-cancellation-restore.log`, `6c-cancellation-build.log` e `6c-cancellation-tests.log`.
- `git diff --check` aprovado; main@1cfb556, staging vazio. Os outros **33 arquivos locais** foram preservados por comparação de hashes. Permanecem **35 arquivos locais: 16 modificados e 19 novos**. Nenhum stage, commit ou push.
- O script permanece intacto, exigindo **741/741**, três XMLs, zero ignorados/erros do runner e Docker exit code 0. Auditoria NuGet inconclusiva por NU1900. Nenhuma aprovação final da 6C para commit.

**Parecer antes da execução final: CORREÇÃO DOS CANCELAMENTOS P2 PRONTA PARA VALIDAÇÃO EXTERNA.** Faltava executar novamente o script e comprovar a assinatura real dos erros e todas as asserções posteriores dos dois casos, além da regressão completa.

### Auditoria final de encerramento do P2 — 26/11/2026

**Execução externa do usuário:** `.local/6c-docker-results/20261126-002527-480-4b4fce`. O Codex leu os três XMLs e os arquivos de códigos de saída; não executou essa suíte SQL no sandbox.

| Projeto | Casos nos XMLs | Aprovados | Falhas / ignorados / erros do runner |
|---|---:|---:|---|
| Domain | 116 | 116 | 0 / 0 / 0 |
| Application | 149 | 149 | 0 / 0 / 0 |
| Integration | 476 | 476 | 0 / 0 / 0 |
| Total | **741** | **741** | **0 / 0 / 0** |

Todos os códigos de saída são **0**: restore, build, Domain, Application, Integration e Docker. As quantidades de elementos test coincidem com os totais declarados nos três XMLs completos. A comparação de nomes **e multiplicidades** com a execução 715/715 anterior não encontrou remoção ou substituição de casos: exatamente **26 adições**, todas em SnapshotConnectionPoolingSqlTests. Os nomes da execução intermediária 739/741 também foram integralmente preservados. O teste de Snapshot indisponível com Pooling=false continua aprovado, sem alteração de setup/asserções nesta auditoria.

**Conclusão sobre o risco e a correção:** o controle sem reset comprovou isolamento residual real neste ambiente. Após commit e também após rollback/dispose, a mesma sessão retornou do pool ainda em nível **5**, com @@TRANCOUNT=0 (SPIDs **74** e **117**, respectivamente, mantendo seus ClientConnectionIds). Portanto, encerrar a transação e fechar logicamente a conexão não assegurava ReadCommitted. Isso não demonstra corrupção financeira ou SQL 3960; demonstra a necessidade da restauração explícita implementada.

Nos dois stores reais corrigidos, as sessões mantiveram a identidade antes/durante/depois: nível **2** antes, **5/TranCount=1** durante Snapshot, transação encerrada antes do reset, **2/TranCount=0** após o reset e na reutilização. Os casos de sucesso usaram SPID **105** para listagem e **108** para extrato, sem troca de ClientConnectionId. A consulta continua inteiramente Snapshot; ReadCommitted é restaurado somente depois de encerrar/descartar a transação, antes de devolver a conexão ao pool.

Foram revisados SnapshotReadSession, os dois stores, SnapshotReadFailure e os 26 testes. A conexão é mantida aberta explicitamente para que o EF não a devolva ao pool antes da limpeza; o reset usa CancellationToken.None e timeout de cinco segundos. ExceptionDispatchInfo preserva a primeira exceção. Falha de reset/dispose não provoca sucesso financeiro e aposenta somente a geração do pool correspondente. As provas de falha de restauração exigem o evento de descarte e ClientConnectionId substituto; o pool não relacionado manteve SPID **66** e ClientConnectionId **c0e750aa-bbb6-45da-bdab-bbc10108f010** em todos esses casos. Um SPID pode ser reciclado pela substituta: os testes não confundem isso com preservação da sessão anterior. Não há ClearAllPools, pooling global desabilitado, retry ou fallback de isolamento.

Os **26/26** casos passaram: dois controles do provider, dois fluxos de sucesso dos stores, dois de JWT/proprietário/revogação, quatro de atualização válida de conta/categoria, quatro de RowVersion concorrente, seis de timeout/falha inesperada/cancelamento e seis de falha de restauração. JWT/revogação exercitaram os endpoints reais na sessão reutilizada. As atualizações geradas pelo EF foram observadas em isolamento **2**, com o output **Legacy UPDATE has EF transaction: False**, e mantiveram 204/ETag e 412 nas disputas. As barreiras e DMVs posicionam a escrita bloqueadora/WAITFOR antes da liberação ou cancelamento, sem sleeps de ordenação. Timeout exige SQL **-2**; falha inesperada permanece 500 seguro, sem resultado financeiro parcial; falha primária simultânea à limpeza exige a mesma instância original de IOException.

#### Dois cancelamentos anteriormente falhos

Ambos os casos de **Failed_or_canceled_store_reads_leave_only_safe_pool_sessions** passaram. Os outputs registraram:

| Caso | SPID | ClientConnectionId mantido | SqlErrors efetivamente observados |
|---|---:|---|---|
| statement: False, stage: cancel | 113 | c383aae9-8a9f-4fc1-a867-6ea8ecd64200 | Dois erros, ambos Number=0, State=0, Class=11 |
| statement: True, stage: cancel | 115 | db8004b6-32e2-481b-b592-4bcf10ddf9e7 | Dois erros, ambos Number=0, State=0, Class=11 |

Esses metadados foram escritos exclusivamente pelo ramo que recebeu **SqlException**, confirmando a representação real do cancelamento nas duas leituras. Não foi aceita uma exceção SQL arbitrária: a asserção também exige a mensagem de atenção, o token correspondente cancelado, CommandCanceled vinculado ao CommandId original e a ClientConnectionId da sessão da leitura. O valor numérico/GUID do CommandId não é impresso no XML; sua correspondência é verificada pelo interceptor. O teste só solicita cancelamento após observar WAITFOR na DMV, com a tarefa ainda em execução e o token ainda não cancelado, e exclui cancelamento do token do runner.

Todas as asserções posteriores foram alcançadas: ReadStoreAsync verifica transação nula e conexão fechada **antes** do descarte do escopo/DbContext; a comparação dos identificadores e fases exige a mesma sessão em nível **2**, @@TRANCOUNT=0 após reutilização; por fim, a nova consulta HTTP do extrato exige closingBalance **105**. Não houve descarte nesses dois casos, pois AssertRestored também exige Discarded=false. A aprovação não se limita à mudança da expectativa do tipo da exceção.

**Parecer final: APROVADA PARA COMMIT. P2 encerrado**, sem novo defeito bloqueante identificado. O impacto foi restrito ao ciclo de vida das leituras Snapshot: expressões financeiras, reconciliação, filtros/paginação, idempotência, RowVersion, autenticação/revogação e permissões permanecem preservados. Não houve nova migration, edição de migrations históricas, dependência ou workflow alterado, banco persistente acessado ou funcionalidade da Etapa 7. A auditoria anterior dessas funcionalidades permanece válida, complementada pela regressão 741/741.

Nesta auditoria foram atualizados **somente README.md, docs/etapa-6/README.md e este documento**. Os demais **32 arquivos locais** foram preservados por comparação de hashes; working tree com **35 arquivos, 16 modificados e 19 novos**, main@1cfb556 e staging vazio. Git diff --check aprovado; nenhum stage, commit ou push. Os relatórios e logs históricos foram preservados. A indisponibilidade Docker/NU1900 no sandbox permanece registrada separadamente; não foi repetida a suíte nesta auditoria.

Pendências não bloqueantes para commit: consulta NuGet conclusiva quando o índice estiver acessível e monitoramento operacional do version store/custo das consultas. Antes do uso em banco persistente, é necessário provisionamento administrativo explícito e autorizado de **ALLOW_SNAPSHOT_ISOLATION ON**; readiness apenas verifica, sem modificar o banco. Sem RCSI ou ampliação de privilégios da API. Publicação autorizada e aprovação da CI própria da 6C continuam necessárias para encerrar integralmente a Etapa 6.

## Reprodução externa e pendências

Com Docker Desktop/Engine Linux, imagens/NuGet acessíveis e recursos para o SQL Server 2022/Testcontainers, executar no PowerShell:

```powershell
& C:\FinanceFlow\scripts\Test-DockerSuite.ps1
```

O script monta o workspace somente para leitura, copia para /work, executa restore/build e os três módulos completos no SDK Linux. Gera `.local/6c-docker-tests.log` e `.local/6c-docker-results/<execucao>`, com stdout/stderr, três XMLs e códigos de saída. Histórico 6a/6b permanece preservado. Após o P2, exige **741 executados/aprovados** (116 Domain, 149 Application, 476 Integration), zero falhas, ignorados ou erros do runner, Docker exit code 0. A guarda anterior de 715 pertence às execuções históricas.

Esse comando não usa o banco persistente: as opções Snapshot, migrations e grants serão exercitados somente nos bancos descartáveis da fixture. Não exige mudança manual em código nem desativação do Smart App Control.

Validação SQL externa **741/741**, dois cancelamentos e auditoria final concluídos conforme a seção acima; não resta pendência técnica bloqueante do P2. Permanecem consulta NuGet conclusiva, requisitos operacionais de provisionamento autorizado e publicação/CI próprias da 6C. Os históricos 715/715 e 739/741 mantêm seu significado anterior; a execução final 741/741 comprova a correção atual. A implementação não foi publicada nem aprovada na CI própria. A Etapa 6 permanece aberta até publicação/CI e a Etapa 7 não foi iniciada.

Risco operacional: Snapshot utiliza o version store do SQL Server; uso de espaço e custo das consultas deverão ser observados no ambiente de execução. Não foram executados benchmarks nem se presumiu a necessidade de novos índices ou configurações globais.
