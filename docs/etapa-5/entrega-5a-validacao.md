# Entrega 5A — Transações financeiras e saldos

## Origem e estado

Workspace C:\FinanceFlow; branch main; baseline confirmada `76e909b`. A implementação começou com Git limpo. Os 311 testes e a CI da Etapa 4 são evidências fornecidas pelo usuário, anteriores a estas alterações. Documentação e código aprovados da Etapa 1 foram lidos, sem alteração de ADRs.

**Implementada e validada externamente pelo usuário: 386 testes aprovados, 0 falhas e 0 ignorados**, incluindo SQL Server/Testcontainers no PowerShell. A revisão técnica consolidada dos 32 arquivos contra `76e909b` foi aprovada sem problemas bloqueantes. Não há publicação ou CI de 5A aprovada confirmada. Os avisos NU1900 mantêm a auditoria de vulnerabilidades inconclusiva.

O histórico de execução do Codex no sandbox Windows, SDK 10.0.404, durante 17–18/11/2026 permanece abaixo, separado da confirmação externa posterior. Os resultados externos não são atribuídos ao Codex.

## Escopo HTTP

| Método | Rota | Retorno |
|---|---|---|
| POST | /api/v1/transactions | 201 com DTO, Location, ETag e snapshot preservado no replay |
| GET | /api/v1/transactions | 200: items, page, pageSize, totalCount, totalPages |
| GET | /api/v1/transactions/{id} | 200 com DTO e ETag, ou 404 genérico |
| GET | /api/v1/accounts/{id}/balance | 200: accountId, currency, asOf, balance |

Todas exigem JWT válido, validação de estado do usuário já existente, HTTPS e no-store. POST exige exatamente um Idempotency-Key válido. Campos JSON desconhecidos/controlados pelo servidor são rejeitados. Nenhum controlador acessa o DbContext para regras de transações/saldos. Os endpoints e o header estão no OpenAPI/Bearer; Swagger/OpenAPI permanecem apenas em Development. O saldo agregado não recebe ETag de conta, pois mudanças nos lançamentos não atualizam a versão da conta.

Entrada de POST: accountId/categoryId obrigatórios, kind income/expense, amount, occurredOn e description opcional até 500 unidades UTF-16. Descrição é preservada literalmente, sem normalização não prevista no contrato; null e vazio são comandos distintos. Saída não inclui UserId, IsDeleted, DeletedAtUtc ou entidades EF.

Erros: 400 para corpo/chave/filtros/valores/datas inválidos ou categoria incompatível; 401 para JWT ausente/inválido/revogado; 404 para referências e recursos alheios/inexistentes; 409 para vínculo inativo ou mesma chave com comando diferente; 429/Retry-After e indisponibilidade conhecida 503 preservados. Erros inesperados permanecem sanitizados, sem ampliação indiscriminada de captura SQL.

Listas: page=1, pageSize=20, máximo 100; filtros accountId/categoryId/kind/from/to/minAmount/maxAmount. Referências filtradas são verificadas por proprietário; inatividade não apaga histórico. Ordenação occurredOn/amount/id, asc/desc; padrão occurredOn desc, desempate Id. Contagem, filtros e paginação são executados no SQL. Datas YYYY-MM-DD, limites inclusivos sem futuro; sem limite artificial de 366 dias para listas. Filtros monetários admitem zero, máximo 999999999999.99 e centavos exatos. A API lê o token monetário original também na query, evitando arredondamento anterior à validação. Listas não prometem snapshot entre contagem/página ou requisições distintas.

## Domínio, datas e valores

FinancialTransaction contém todos os campos do modelo aprovado, com setters privados. Criação gera Guid/UTC no servidor, inicia IsDeleted=false, DeletedAtUtc/UpdatedAtUtc nulos e recebe RowVersion do SQL. Não há métodos de edição/exclusão em 5A.

Amount é decimal, maior que zero e até 999999999999.99, com rejeição de fração efetiva de centavo. O conversor já existente de Accounts foi reutilizado sem alteração: valida o número JSON original antes de qualquer conversão capaz de perder precisão. 10.000 é válido; 10.001 e números com frações ocultas por excesso de precisão são rejeitados. O limite monetário foi centralizado sem mudar o intervalo assinado de saldo inicial.

FinancialCalendar/TimeProvider existentes definem hoje em America/Sao_Paulo. OccurredOn é DateOnly, sem conversão para UTC; deve estar entre a abertura e hoje, inclusive. O domínio valida os vínculos carregados sob proteção transacional: proprietário, identificadores, categoria compatível, abertura e atividade. A aplicação valida o comando e coordena a porta específica; a Infrastructure garante a ordem e a proteção das leituras necessárias a essa validação.

## Idempotência e disputa com desativação

Usa a tabela existente IdempotencyRecords, namespace UserId + `transactions.create.v1` + Key, comparação BIN2 e mesmo índice único. A validação comum da chave foi compartilhada preservando a função pública de Accounts e seu fingerprint original. Chave: 1–128 ASCII visíveis, sem espaços, trim ou alteração de caixa.

O SHA-256 usa JSON canônico v1 de ordem fixa: operação, accountId/categoryId em Guid D, kind, amount F2 invariante, occurredOn yyyy-MM-dd e description literal. Id e timestamps gerados não integram o hash.

Fluxo: consulta replay; inicia RepeatableRead; lê conta e depois categoria por Id/UserId; reconfere replay após eventual espera em bloqueio; valida os vínculos no domínio; insere lançamento; captura RowVersion; insere snapshot de resposta; confirma a transação. Retornos antecipados e falhas descartam/rollback da transação. Sucesso só retorna depois do commit.

RepeatableRead mantém shared locks dos recursos lidos até o término da transação. A desativação usa o UPDATE existente, que disputa pelos mesmos locks. Se a desativação confirmar antes da leitura, a criação rejeita; se a criação adquirir os locks primeiro, a desativação aguarda seu commit. Não há consulta de atividade fora da transação como garantia, NOLOCK, retry automático ou Serializable global.

Na colisão, somente o erro 2601 do índice idempotente exato com entrada EF IdempotencyRecord é reconhecido. Faz rollback integral e consulta o vencedor em contexto novo. Demais índices não viram replay. Deadlock/timeout conhecido não retorna sucesso; repetir posteriormente com a mesma chave é seguro. O snapshot guarda 201, corpo JSON, Location, ETag e Content-Type originais; replay não reconstrói o recurso nem exige vínculos ainda ativos. O mecanismo de criação de Accounts permanece intacto.

## Saldo

Uma consulta SQL consolidada seleciona a conta própria e o agregado assinado de lançamentos não excluídos até asOf. O saldo final é OpeningBalance + receitas - despesas. Sem asOf, usa hoje financeiro; futuro retorna 400; antes da abertura retorna zero. Inativas continuam incluídas. Retroativos alteram o histórico calculado; não é uma posição temporal do que era conhecido naquele dia.

O SUM decimal fica separado do saldo inicial na projeção SQL. A soma final é checked decimal no C#: evita redução de escala na aritmética SQL de decimal(38,2) com decimal(18,2). Não há limite individual de Amount aplicado ao saldo agregado. Overflow/erro não retorna saldo parcial. Não se habilitou Snapshot/RCSI: esta resposta usa um único comando, conforme preferência do ADR-004. Transferências não existem nesta etapa.

## Migration e permissões

Migration nova: **20261117000100_TransactionsDelivery5A**; tabela dbo.FinancialTransactions, decimal(18,2), date, datetimeoffset, rowversion, descrição nvarchar(500), CHECKs de valor/tipo e consistência de exclusão. FKs NoAction: UserId; (AccountId,UserId); (CategoryId,UserId,Kind). As chaves alternativas dos pais já existiam.

Índices por usuário/conta/data/Id, usuário/categoria/data, usuário/data/Id para listas sem conta e os índices de FK gerados pelo modelo. Não há índices para cada combinação arbitrária de filtro.

O target congelado TransactionsDelivery5AModel parte do target anterior, sem chamar configurações runtime mutáveis. O snapshot cumulativo aponta ao novo target. Nenhuma migration/target histórico foi editado. HasPendingModelChanges false foi verificado sem acesso ao banco. O teste histórico que enumera migrations foi atualizado para incluir a quarta migration, sem remover verificações anteriores.

API recebe somente SELECT/INSERT em FinancialTransactions. O verificador foi ampliado com os mesmos dois privilégios, mantendo as regras e grants por coluna de Accounts/Categories. Não há UPDATE/DELETE para transações ou registros idempotentes, DDL ou conta administrativa na API. Readiness também verifica SELECT na nova tabela; liveness permanece independente.

Nada foi aplicado ao banco persistente. Para uso posterior em um ambiente persistente, a migration e o novo provisionamento precisam de execução explícita e autorizada, com as credenciais separadas já documentadas no README. Os comandos de teste abaixo não exigem essa atualização do ambiente persistente.

## Testes e resultados reais

Acrescentados **75 casos** aos 311 existentes: 19 Domain, 14 Application, 15 contratos/modelo sem SQL e 27 SQL Server. Nenhum teste foi removido ou ignorado.

| Verificação do Codex | Resultado |
|---|---|
| Restore com NuGet.Config | Saída 0; cinco NU1900, consulta de vulnerabilidades indisponível |
| Build Release | Sete projetos, zero erros, zero warnings C# e cinco NU1900 |
| Domain.Tests | 75 aprovados |
| Application.Tests | 78 aprovados |
| IntegrationTests sem SQL | 93 aprovados, incluindo contratos OpenAPI e modelo/snapshot |
| Suíte completa | 386 casos: 246 aprovados, 140 falhas na fixture Docker, zero ignorados; saída 2 |
| Auditoria com transitivos | Saída 1; índice api.nuget.org inacessível, socket api.nuget.org:443 proibido pelo ambiente; inconclusiva |
| Docker | config.json: acesso negado; pipe docker_engine indisponível/inacessível no sandbox |
| Revisão Git | 32 arquivos no escopo; git diff --check aprovado; sem alterações em migrations históricas, dependências, arquivos sensíveis/temporários visíveis ao Git ou conteúdo staged |

Os 140 casos SQL (113 anteriores + 27 novos) falharam antes de suas asserções com DockerUnavailableException. Isso não comprova migration aplicada, privilégios efetivos, rollback ou concorrência no SQL Server. Não houve substituição por SQLite/InMemory, skip ou enfraquecimento de segurança. Os avisos ambientais de Data Protection do perfil Windows continuam separados dos resultados da aplicação; nenhuma chave/configuração foi alterada.

Cobertura preparada: receitas/despesas com saldos iniciais zero/positivos/negativos; centavos e limites; agregados acima do limite individual; corte inclusivo, abertura e retroativos; estados excluídos preparados somente no banco descartável; inatividade; proprietário alheio; filtros/contagem/ordenação; replay após inatividade; corrida pela chave com barreira antes dos INSERTs; falha induzida entre lançamento e snapshot; criação versus desativação nas duas ordens. Os testes coordenados verificam isolamento RepeatableRead e shared locks reais via sys.dm_tran_locks, com requisições independentes e sem sleeps arbitrários.

O teste incremental inicia no schema de B e preserva usuários/contas/categorias/versões após migrar; a fixture geral testa banco vazio. Outros casos verificam tipos SQL, CHECKs/FKs, SELECT/INSERT efetivos, UPDATE/DELETE negados, grants excessivos rejeitados e readiness sem leitura na tabela. Essa cobertura estava pendente ao encerrar a execução no sandbox; sua aprovação externa foi posteriormente confirmada pelo usuário, conforme a seção seguinte.

Logs locais ignorados: `.local/5a-restore.log`, `.local/5a-build.log`, `.local/5a-tests.log`, `.local/5a-audit.log`; rodada intermediária `.local/5a-initial-tests.log`. Não foram publicados.

## Validação externa concluída e auditoria técnica consolidada

O usuário confirmou a execução externa da suíte completa no PowerShell com Docker/SQL Server/Testcontainers disponíveis:

```powershell
dotnet test FinanceFlow.slnx --configuration Release --no-restore
```

Resultado fornecido pelo usuário: **386 testes aprovados, 0 falhas e 0 ignorados**, incluindo a integração com SQL Server/Testcontainers. A regressão completa e os 140 casos SQL não estão mais pendentes de aprovação externa. A confirmação não substitui nem apaga as falhas anteriores do Codex na fixture Docker, que ocorreram antes das asserções.

O usuário também confirmou avisos **NU1900**: a auditoria de vulnerabilidades permanece **inconclusiva**. Não se declara ausência de vulnerabilidades nem consulta bem-sucedida ao índice; a aprovação funcional não é uma auditoria de segurança. Não foi informado novo build sem warnings.

A revisão estática consolidada do Codex comparou os **32 arquivos locais (11 modificados e 21 novos)** com `76e909b`, incluindo os arquivos ainda não rastreados. Resultado: **APROVADO, sem problemas bloqueantes encontrados**. Foram revisados segurança e isolamento, centavos e saldos históricos, idempotência/atomicidade/rollback, proteção contra desativação concorrente, FKs e permissões, HTTP/OpenAPI e testes. Nenhum código, teste ou migration foi alterado nesta auditoria; somente a documentação foi atualizada. Nenhum teste foi reexecutado nesta atualização.

As evidências externas dizem respeito aos bancos descartáveis dos testes. Não comprovam aplicação de migrations/provisionamento a banco persistente, publicação ou aprovação do GitHub Actions de 5A. As Entregas 5B/5C e a Etapa 6 permanecem fora do escopo.

## Procedimento de reprodução externa

Pré-requisitos: SDK .NET 10.0.4xx, Docker Desktop/Engine Linux acessível e recursos para SQL Server 2022 x64, acesso ao NuGet e MCR. As fixtures geram usuários, senhas, chave JWT e bancos descartáveis. Não carregar .env, segredos persistentes nem executar scripts de provisionamento local para rodar os testes.

```powershell
Set-Location C:\FinanceFlow
$ErrorActionPreference = 'Stop'
function Assert-Step([string]$Name) {
    if ($LASTEXITCODE -ne 0) { throw "$Name falhou; preserve a saída." }
}
docker info --format '{{.ServerVersion}}'
Assert-Step 'Docker'
dotnet restore FinanceFlow.slnx --configfile NuGet.Config
Assert-Step 'Restore'
dotnet build FinanceFlow.slnx --configuration Release --no-restore
Assert-Step 'Build'
dotnet test FinanceFlow.slnx --configuration Release --no-restore
Assert-Step 'Testes'
dotnet list FinanceFlow.slnx package --vulnerable --include-transitive
Assert-Step 'Auditoria'
git diff --check
Assert-Step 'Diff'
```

Esperado: **386 aprovados, zero falhas e zero ignorados**. NU1900 não significa auditoria aprovada: se a consulta não estiver disponível, registrar resultado inconclusivo mesmo que nenhuma vulnerabilidade seja listada. Não há dependências novas ou relaxamento da auditoria. A CI existente já executa toda a solução com Docker; não houve push para validar 5A.

## Arquivos e limites

Novos: Domain/Common/MonetaryLimits e Transactions/FinancialTransaction; Application/Common/IdempotencyKey e Transactions/{TransactionContracts,TransactionCreationFingerprint,FinancialTransactionsService}; Infrastructure/Persistence/Transactions/{FinancialTransactionConfiguration,FinancialTransactionStore}, migration 20261117000100 e TransactionsDelivery5AModel; Api/Contracts/Transactions/TransactionRequests e Controllers/{TransactionsController,AccountBalancesController}; seis arquivos de testes novos; docs/etapa-5/{README,entrega-5a-validacao}.

Modificados: README; AccountCreationFingerprint (delegação da validação da chave); FinancialAccount (alias do mesmo limite); DependencyInjection; FinanceFlowDbContext; DatabaseTasks; snapshot cumulativo; HealthController; Program (no-store nas novas rotas); ApiDocumentationConfiguration; CategoryPersistenceSqlTests (lista de migrations).

Regressão externa e revisão técnica consolidadas aprovadas. Permanece pendente consultar efetivamente o índice de vulnerabilidades do NuGet, pois os avisos NU1900 tornam a auditoria inconclusiva. Publicação e CI de 5A não estão confirmadas. O reconhecimento restrito de colisão ainda depende da mensagem SQL em inglês, conforme limitação herdada. A aprovação externa dos testes de bloqueios reais não é apresentada como execução no sandbox.

Fontes técnicas consultadas: [transações EF Core](https://learn.microsoft.com/en-us/ef/core/saving/transactions), [isolamento SQL Server](https://learn.microsoft.com/en-us/sql/t-sql/statements/set-transaction-isolation-level-transact-sql) e [SUM/precisão decimal](https://learn.microsoft.com/en-us/sql/t-sql/functions/sum-transact-sql).

**Sem stage, commit ou push. Sem alterações em banco persistente, credenciais, certificados, volumes ou permissões do ambiente. 5B/5C/6 não iniciadas.**
