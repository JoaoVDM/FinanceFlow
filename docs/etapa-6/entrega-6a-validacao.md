# Entrega 6A — Criação de transferências entre contas próprias

## Estado e evidências

**APROVADA PARA COMMIT.** A execução externa final em Docker Linux comprovou **557 testes aprovados, zero falhas, zero ignorados e zero erros de runner**, com três XMLs válidos e **Docker exit code 0**. A auditoria consolidada contra `f0969b8`, incluindo os arquivos não rastreados, não encontrou problemas bloqueantes. As duas correções de preparação/instrumentação de testes estão validadas. A execução foi realizada pelo usuário e seus relatórios foram conferidos pelo Codex; não é uma execução SQL do sandbox. Sem publicação/CI de 6A e sem encerramento da Etapa 6.

Workspace confirmado em `C:\FinanceFlow`, branch `main`, commit **f0969b8**, Git inicialmente limpo. O usuário confirmou encerramento da Etapa 5 nesse commit, CI #9 aprovada e 477 testes aprovados, zero falhas/ignorados. Publicações anteriores: 5A em f9e7d12/CI #7 e 5B em b548f4f/CI #8. São evidências fornecidas pelo usuário, não novas execuções do Codex.

Foram consultados roadmap, modelo, contratos, critérios, ADRs 003/004/005/006/007 e histórico de 5C. As decisões aprovadas foram preservadas; não foi necessário novo ADR ou dependência.

## Entidades, camadas e regras

- **Domain/Transfers/Transfer:** Id/UserId, SourceAccountId, DestinationAccountId, Amount, OccurredOn, Description, IsCanceled/CanceledAtUtc, timestamps e RowVersion; setters privados. Criação valida IDs não vazios/distintos, valor positivo/centavos/limite, futuro e descrição. CheckAccounts valida as duas identidades/propriedades, BRL, atividade e aberturas. Não há operação de cancelamento, edição ou restauração.
- **Application/Transfers:** TransfersService, porta ITransferStore, DTOs/resultados específicos e fingerprint SHA-256 v1. Proprietário vem de ICurrentUser; TimeProvider e FinancialCalendar fornecem o dia de São Paulo e UTC técnico. Não há DbContext/SQL na Application ou no Domain.
- **Infrastructure/Persistence/Transfers:** TransferStore, projeção sem tracking, transação SQL, mapeamento Fluent API e snapshot persistido no IdempotencyRecord existente.
- **Api:** CreateTransferRequest/TransfersController, composição, no-store e OpenAPI. Controllers não consultam o DbContext. Reutiliza conversor monetário exato, validação JSON desconhecido, limite de 16 KiB, JWT/revogação, HTTPS, rate limiting e Problem Details existentes.

Somente BRL; Amount em decimal, maior que zero e até **999999999999.99**, sem fração efetiva de centavo. 10.000 equivale a 10.00; 10.001 e tokens JSON que perderiam frações durante parsing são rejeitados. A data financeira é DateOnly, sem conversão para UTC; vai da abertura mais recente das duas contas até hoje financeiro. Descrição aceita null/vazio, preserva espaços/texto e tem limite de 500 unidades UTF-16. Timestamps técnicos vêm do relógio injetado e são normalizados para UTC.

Não se exige saldo disponível: a origem pode ficar negativa. Não há categoria, Kind income/expense ou duas FinancialTransactions derivadas. Uma única linha de Transfer gera entrada/saída de mesmo valor; não é receita/despesa.

## Contratos HTTP

| Método | Rota | Entrada / sucesso |
|---|---|---|
| POST | /api/v1/transfers | sourceAccountId, destinationAccountId, amount, occurredOn, description opcional; 201 com DTO/Location/ETag |
| GET | /api/v1/transfers/{id} | Sem corpo; 200 com DTO e ETag forte |

DTO público: id, contas de origem/destino, amount, occurredOn, description, createdAtUtc, updatedAtUtc e version opaco. Não expõe UserId ou detalhes EF; JSON camelCase. Corpo não aceita Id/UserId, estados, RowVersion, timestamps ou campos desconhecidos. Idempotency-Key é obrigatório somente no POST, sem If-Match nesta entrega.

- 400: comando/data/valor/chave inválidos, contas iguais, JSON inválido ou campos não permitidos.
- 401: autenticação ausente/inválida/revogada; desafio Bearer preservado.
- 404: recurso/conta alheio ou inexistente, sem distinção pública indevida.
- 409: conta inativa ou mesma chave com comando diferente.
- 429: limites existentes, com Retry-After.
- 503: indisponibilidade conhecida, incluindo SQL 1205/-2 nos wrappers conhecidos do EF.
- Falhas inesperadas: tratamento global seguro, sem SQL/stack trace/credenciais na resposta. Todas as respostas em /transfers usam Cache-Control: no-store, inclusive erros anteriores ao controller.

OpenAPI documenta Bearer, request, precisão, respostas e cabeçalhos de criação/consulta. Swagger/spec permanecem somente em Development. Não há GET da coleção, PUT/PATCH, DELETE/cancelamento ou extrato.

## Idempotência, atomicidade e concorrência

Namespace existente **UserId + Operation + Key**, com operação nova **transfers.create.v1**. Chave opaca, sensível a caixa, 1–128 caracteres ASCII visíveis sem espaços; não recebe trim/normalização. Fingerprint SHA-256 usa ordem fixa, GUIDs D, decimal F2 e data yyyy-MM-dd; descrição é exata, inclusive distinção null/vazio. Ids/timestamps gerenciados não integram o comando. A ordem do JSON e a escala decimal equivalente não mudam o hash.

Fluxo: consulta replay próprio; inicia RepeatableRead; lê as duas contas do usuário ordenadas por Id; reconfere replay após eventual espera; valida vínculos; insere uma Transfer; captura rowversion; insere snapshot; confirma a transação. Sucesso financeiro novo só retorna depois do commit. Não há chamada externa nessa transação, bloqueio global ou DbContext compartilhado entre requisições.

As leituras retêm shared locks até o commit; desativação usa os UPDATEs existentes que disputam esses locks. Se a desativação confirmar antes, a criação rejeita atividade; se a criação adquirir os locks primeiro, confirma antes da desativação. As duas direções usam a mesma ordenação SQL de contas.

O snapshot conserva **201, corpo, Location, ETag e Content-Type** originais. Replay não reexecuta validações de atividade/persistência nem reconstrói a resposta do estado atual. A autenticação e a validação estrutural do comando/chave permanecem obrigatórias. Payload diferente no mesmo namespace retorna 409. Nenhum fingerprint ou snapshot de contas/transações foi modificado.

O índice idempotente único decide a corrida pela chave. Somente SqlException 2601 do índice exato, com a entrada EF IdempotencyRecord e mensagem restrita já usada pelo projeto, é interpretada como colisão idempotente. A transação perdedora sofre rollback e o resultado confirmado é consultado em contexto novo. Outros erros de integridade não viram replay. Permanece a limitação conhecida de mensagem SQL em inglês; não se ampliou indiscriminadamente a captura de 2601/2627.

Falhas desfazem toda a unidade. Deadlock/timeout com os wrappers conhecidos de InvalidOperationException/DbUpdateException e SQL 1205/-2 retornam indisponibilidade; cancelamento não é absorvido pelo filtro. O descarte da transação reverte o que ainda estiver ativo, sem tentar um segundo rollback explícito da vítima já abortada. Não há retries automáticos. Após timeout/falha antes do commit, a mesma chave pode criar uma vez; após resposta perdida com commit confirmado, a mesma chave recupera o registro original.

## Saldo calculado e histórico

`FinancialTransactionStore.BalanceAsync` foi alterado somente na projeção do saldo: um comando SQL obtém abertura, SUM de receitas/despesas válidas e SUM assinado de transferências não canceladas até o corte, sempre por proprietário/conta. A expressão de transferência aplica +Amount no destino e -Amount na origem. A soma final é **checked decimal em C#**, sem aritmética SQL entre agregados decimal(38,2) que poderia reduzir escala.

```text
OpeningBalance + Income - Expense + TransferIn - TransferOut
```

Não há CurrentBalance persistido ou histórico inteiro materializado. Antes da abertura retorna zero. Contas inativas continuam incluídas; retroativos afetam ambos os cortes; IsDeleted/IsCanceled removem efeitos. O endpoint/DTO de saldo é preservado. A nova consulta continua consolidada, sem habilitar RCSI/Snapshot global ou prometer visão pontual de todo o banco sob ReadCommitted. Somar respostas HTTP independentes só é usado nos testes com dados controlados e sem escritas concorrentes.

Exemplos independentes nos testes: A=2000 e B=1000; transferência de 500 em 2000-01-03 deixa A=B=1500 e preserva 3000. Outro conjunto inclui receita de 100, despesa de 50, transferência reversa retroativa, saída de 5000 sem verificação de fundos e exclusão lógica de receita em conta inativa. As posições finais esperadas são **-3300 e 6250**, total **2950**, sem transferências na lista de receitas/despesas. Os testes anteriores de overflow e reconciliação de 5C permanecem obrigatórios.

## Migration, schema e permissões

Migration incremental: **20261120000100_TransfersDelivery6A**, quinta migration. Somente dbo.Transfers e seus índices/constraints são novos:

- PK Id; FK UserId para AspNetUsers.
- FKs compostas SourceAccountId/UserId e DestinationAccountId/UserId para FinancialAccounts/Id/UserId, todas NoAction.
- decimal(18,2), date, datetimeoffset, nvarchar(500) e rowversion real.
- CHECKs Amount positivo/limite, contas distintas e par IsCanceled/CanceledAtUtc coerente.
- Índices de consulta UserId/SourceAccountId/OccurredOn/Id e UserId/DestinationAccountId/OccurredOn/Id, além dos índices de FK das duas contas. Sem índices para cada filtro futuro.

O target novo TransfersDelivery6AModel é congelado e parte do target histórico de 5A; não chama configurações runtime mutáveis. O snapshot cumulativo aponta ao target novo. As quatro migrations anteriores e seus targets permanecem intactos. **HasPendingModelChanges=false passou** no teste de modelo sem banco. Três testes anteriores tiveram somente as expectativas de histórico atualizadas para a quinta migration, sem remoção das verificações antigas.

API recebe **SELECT/INSERT** em Transfers e nada mais. Sem UPDATE, DELETE, ALTER/DDL, delegação, db_owner ou usuário sa. Grants anteriores permanecem. DatabaseTasks concede e verifica os dois privilégios somente após existência da tabela; o verificador continua rejeitando UPDATE por coluna, DELETE e WITH GRANT OPTION inesperados. Readiness exige schema/migrations e acesso SELECT à nova tabela; liveness permanece independente do SQL.

**Nada foi migrado/provisionado no banco persistente.** Para usar a 6A nesse banco posteriormente, é necessário procedimento explícito e autorizado, com a imagem nova, credencial de migration separada e novo provisionamento SELECT/INSERT após a migration. Os comandos de testes abaixo não exigem isso. A sequência Docker já documentada na raiz continua válida: build da imagem, migrations explícitas e provisionamento explícito após a migration. Não executar limpeza de volumes para atualizar schema.

## Cobertura adicionada

Mantidos os **477** testes da baseline; acrescentados **80**, total **557**:

| Projeto / arquivo | Casos novos | Finalidade |
|---|---:|---|
| Domain.Tests/TransferTests | 21 | IDs, contas distintas, valores/centavos/limites, futuro/aberturas, atividade, descrição, UTC e encapsulamento |
| Application.Tests/TransfersServiceTests | 13 | Proprietário autenticado, clock/calendário, chaves, entradas, leitura, resultados sem retry e fingerprint |
| IntegrationTests/TransferContractTests | 8 | JWT/no-store, tokens numéricos originais, modelo/snapshot e OpenAPI |
| IntegrationTests/TransfersSqlTests | 12 | Criação/GET, saldos/histórico, patrimônio, isolamento, validações, namespace, replay e inatividade |
| IntegrationTests/TransfersPersistenceSqlTests | 13 | Migration incremental, preservação de dados, constraints, tipos, permissões, readiness e grants extras |
| IntegrationTests/TransfersConcurrencySqlTests | 13 | Mesma chave/payload distinto, direção reversa, desativação em ambas as ordens, rollback, timeout, deadlock, perda de resposta e colisão de PK não idempotente |

Testes SQL usam o Testcontainers.MsSql existente e bancos descartáveis. A fixture geral aplica migrations em banco vazio. O teste incremental inicia no schema da Etapa 5, cria usuário/conta/lançamento/snapshot e valida preservação de dados, JWT e versões após migrar. Estados cancelados são preparados somente por credencial de teste no banco descartável para verificar os filtros; isso não cria operação de cancelamento de 6B ou grant UPDATE para a API.

Concorrência usa conexões/DbContexts independentes, barreiras e inspeção de isolamento/shared locks reais. A barreira de criação versus desativação pausa antes do INSERT, depois de materializar e validar integralmente as duas contas, enquanto RepeatableRead mantém os locks; não depende da disponibilidade parcial de um reader. WAITFOR é usado apenas para provocar timeout real do provider. O teste de deadlock gera um ciclo real de locks com prioridade definida. Falhas são induzidas antes do snapshot, depois de ambos os INSERTs antes do commit e depois do commit. Uma colisão de PK é provocada no banco real e deve falhar sem virar replay; a chave dessa falha pode ser reutilizada. Sem mocks para substituir integridade SQL, EF InMemory, SQLite, skips ou enfraquecimento de asserções.

## Resultados executados pelo Codex

| Verificação | Evidência |
|---|---|
| SDK | .NET 10.0.404 |
| Restore | Saída 0; cinco NU1900 por índice indisponível |
| Build Release final | Sete projetos; saída 0, zero erros, nenhum warning C#/analisadores; cinco NU1900 |
| Teste isolado inicial de modelo | Um aprovado; HasPendingModelChanges=false |
| Contratos/modelo novos isolados após correção | **8 aprovados, 0 falhas, 0 ignorados** |
| Primeira suíte intermediária | 557 casos: 326 aprovados, 231 falhas, zero ignorados; 230 eram fixture Docker e uma era ausência de Bearer no OpenAPI |
| Suíte após correção do OpenAPI | **557 casos: 327 aprovados, 230 falhas de fixture Docker, 0 ignorados**; saída 2; aproximadamente 39s |
| Última execução, após refinar a barreira do teste de desativação | **217 aprovados (Domain/Application), zero casos com falha/ignorados nesses módulos**, mas processo com saída **-532462766**: módulo IntegrationTests bloqueado antes da descoberta/execução. A suíte completa não foi aprovada |
| Auditoria NuGet com transitivos | Saída 1; api.nuget.org:443 bloqueado / índice inacessível; **inconclusiva** |
| Revisão Git | `git diff --check` aprovado; 32 arquivos no escopo (14 modificados e 18 novos), staging vazio; migrations históricas e dependências preservadas |

A falha intermediária de OpenAPI foi corrigida incluindo /transfers na lista de operações documentadas com Bearer; a autorização JWT real já estava ativa. O teste foi repetido e passou; a execução completa seguinte não apresentou outra falha fora da fixture. Na revisão final, foi aprimorada somente a posição da barreira do teste de desativação. O build repetido passou; a nova execução não pôde iniciar o módulo de integração devido ao bloqueio descrito abaixo. Não se omitiu ou alterou a expectativa do teste para obter aprovação.

Docker retornou acesso negado a `C:\Users\jvito\.docker\config.json` e ao pipe docker_engine, ou pipe indisponível na outra tentativa. Nas execuções em que o módulo de integração iniciou, os **230 casos SQL** (192 anteriores + 38 novos) falharam na construção da fixture e não atingiram asserções. Isso não comprova migration aplicada, privilégios efetivos, POST funcional, rollback, saldos com transferências ou concorrência em SQL. Não houve alteração de permissões/sandbox, fallback ou teste ignorado.

Na última execução, o runner registrou handshake failure do módulo de integração e **System.IO.FileLoadException**, ao carregar `C:\FinanceFlow\tests\FinanceFlow.IntegrationTests\bin\Release\net10.0\FinanceFlow.IntegrationTests.dll`: **“Uma política de Controle de Aplicativo bloqueou este arquivo. (0x800711C7)”**. Os 217 testes unitários/aplicação passaram; os 340 casos esperados do módulo de integração não iniciaram. O zero de falhas de casos no resumo dessa tentativa não significa sucesso: o processo terminou com erro. Nenhuma política, permissão ou proteção foi alterada para contornar o bloqueio. Esse resultado é distinto da indisponibilidade do Docker e permanece pendente de execução externa.

Logs ignorados em `.local`: 6a-restore.log, 6a-build.log, 6a-build-final.log, 6a-model.log, 6a-contracts.log, 6a-tests.log (intermediária), 6a-tests-final.log (última tentativa, bloqueio de carregamento) e 6a-audit.log. O resumo anterior de 557/327/230 está registrado na saída das ferramentas desta conversa e na tabela; não é o conteúdo atual do log da última tentativa. A aprovação externa da baseline e CI #9 não são atribuídas à 6A.

## Investigação das duas falhas externas — 20/11/2026

O usuário confirmou execução completa em Linux com SDK 10.0.404, Docker e SQL Server/Testcontainers funcionando: **557 executados, 555 aprovados, duas falhas, zero ignorados**, aproximadamente 1m47s. Isso comprova que a execução externa ultrapassou a inicialização da fixture e o bloqueio de assemblies do Windows. Não é o resultado de uma nova execução pelo Codex, nem aprovação da suíte após a correção abaixo.

O arquivo local `.local/6a-linux-tests.log`, com 7396 bytes no momento da investigação, contém somente uma entrada de falha do runner, sem resumo final ou identificação do segundo teste. Linhas internas de log com `fail:` não foram usadas para inferir nomes ou quantidade de testes com falha.

### Falha identificada: preparação da negação de SELECT

- **Nome completo:** `FinanceFlow.IntegrationTests.TransfersPersistenceSqlTests.Canceled_fixture_rows_remove_both_effects_and_readiness_requires_select_on_transfers`.
- **Arquivo:** `tests/FinanceFlow.IntegrationTests/TransfersPersistenceSqlTests.cs`; linha 161 na execução original, em `deny.ExecuteNonQueryAsync`.
- **Esperado:** preparar o `DENY SELECT` no banco descartável e então verificar readiness 503 e liveness 200.
- **Obtido:** `Microsoft.Data.SqlClient.SqlException: Cannot find the object 'Transfers', because it does not exist or you do not have permission.` durante o `DENY`, antes das requisições de health. O log não informa o número SQL dessa exceção.
- **Causa:** o comando `DENY SELECT ON OBJECT::dbo.Transfers TO FinanceFlowApi` usava a conexão do migrador. Essa identidade possui permissões de schema/DML e criação de tabelas, mas não a autoridade administrativa necessária para negar permissões de outro principal. A tabela já havia sido usada com sucesso no mesmo teste: criação, consultas de saldo e preparação do estado cancelado ocorreram antes desse comando. Portanto, a evidência aponta para a preparação de permissões do teste, não para uma tabela ausente na API.
- **Correção localizada:** somente o `DENY` passou a usar `fixture.AdminConnectionString`, com `InitialCatalog` explicitamente apontado ao mesmo banco descartável. A preparação do estado cancelado permanece na conexão de migration; nenhuma permissão foi ampliada para a API ou para o migrador. O padrão já existe no teste anterior de readiness de FinancialTransactions.

O SQL Server exige `CONTROL` no objeto para esse `DENY`, conforme a [documentação oficial de permissões](https://learn.microsoft.com/en-us/sql/t-sql/statements/deny-object-permissions-transact-sql?view=sql-server-ver16#permissions). Não se concedeu `CONTROL` ao migrador para fazer o teste passar. Não houve alteração de código de produção, cálculo financeiro, migration ou contrato HTTP nesta investigação.

### Segunda falha: identificação pendente na primeira investigação

Naquele momento, o nome, a asserção, o resultado esperado/obtido e a causa da segunda falha **não constavam no log disponível**. Nenhuma correção especulativa foi aplicada. O script diagnóstico foi criado para preservar os resultados completos do runner. A identificação na execução seguinte está registrada abaixo.

### Verificações posteriores à correção, executadas pelo Codex

| Verificação | Resultado real |
|---|---|
| Baseline e alterações locais | `main`, `f0969b8`; alterações locais da 6A preservadas |
| Build Release com `--no-restore` | Saída 0; sete projetos, zero erros e cinco NU1900; sem warning C#/analisadores |
| Sintaxe de `scripts/Test-DockerSuite.ps1` | Parser PowerShell: zero erros |
| Execução do script no sandbox | Docker retornou saída 1: acesso negado ao arquivo de configuração e ao pipe docker_engine. Log completo e código de saída foram preservados; nenhum contêiner iniciou e nenhum módulo de testes foi executado |
| Teste SQL corrigido isoladamente | Não reexecutado: Docker inacessível no sandbox |
| Suíte SQL completa após a correção | Não reexecutada; **557/557 ainda não comprovado** |
| Auditoria NuGet | Inconclusiva; NU1900 continua registrado, sem supressão de avisos |
| Revisão Git desta investigação | `git diff --check` aprovado; 33 arquivos locais da 6A (14 modificados e 19 novos), incluindo o novo script diagnóstico; staging vazio. Logs/relatórios permanecem ignorados; nenhum arquivo sensível ou temporário inesperado na lista de alterações |

O novo log dessa tentativa está em `.local/6a-docker-tests.log`, com saída por execução em `.local/6a-docker-results/`. Esses arquivos são ignorados pelo Git. A mensagem de ausência de XML nessa tentativa indica que o Docker não iniciou; não representa zero testes com falha nem aprovação dos módulos. Os registros históricos das tentativas anteriores do Codex permanecem acima.

## Nova execução externa e correção da captura de colisão de PK — 20/11/2026

O usuário executou `scripts/Test-DockerSuite.ps1` no PowerShell externo, com Linux/SQL Server/Testcontainers. Os três XMLs e `docker.exitcode` em `.local/6a-docker-results/20261120-222221-315-36152a` foram inspecionados pelo Codex:

| Projeto | Total | Aprovados | Falhas | Ignorados |
|---|---:|---:|---:|---:|
| Domain.Tests | 112 | 112 | 0 | 0 |
| Application.Tests | 105 | 105 | 0 | 0 |
| IntegrationTests | 340 | 339 | 1 | 0 |
| **Total** | **557** | **556** | **1** | **0** |

Os três relatórios estão presentes; zero erros de runner; **Docker exit code 1**. O XML registra `Pass` para `Canceled_fixture_rows_remove_both_effects_and_readiness_requires_select_on_transfers`, validando externamente a correção anterior da preparação de permissões. Esses resultados pertencem à execução do usuário antes da alteração de instrumentação descrita a seguir, não a testes SQL executados pelo Codex.

### Falha restante e causa

- **Teste:** `FinanceFlow.IntegrationTests.TransfersConcurrencySqlTests.Unrelated_primary_key_collision_is_not_replayed_and_does_not_create_a_success_snapshot`.
- **Local original:** `TransfersConcurrencySqlTests.cs:266`, em `Assert.Equal(2627, observer.SqlError)`.
- **Resultado:** esperado 2627, obtido `null`.
- **Preparação:** o teste confirma uma transferência original, usa outra chave idempotente e, em `PrimaryKeyCollision.SavingChangesAsync`, substitui o Id da nova entidade Added pelo Id já persistido. O alvo é a constraint **PK_Transfers**, não o índice de IdempotencyRecords. O mapeamento usa Id não gerado pelo banco; os demais campos do comando continuam válidos.
- **Evidência de execução:** `console.log:10264` registra `Request failed with DbUpdateException` imediatamente antes da falha do runner. O handler registra somente o tipo; a resposta pública e esse log sanitizado não carregam o código SQL interno. O XML confirma que a única asserção que falhou foi a captura de 2627.
- **Defeito de instrumentação:** `TransferObserver.SqlError` era preenchido somente em `DbCommandInterceptor.CommandFailedAsync`. A execução do reader pode completar antes de o SqlClient receber o erro durante o consumo dos resultados do INSERT. Essa falha é encapsulada em `DbUpdateException` pelo fluxo de SaveChanges, sem necessariamente disparar CommandFailed. Portanto, `null` representa a ausência de captura nesse evento, não a ausência de falha de persistência.

O fluxo está documentado no [código oficial do EF Core 10](https://github.com/dotnet/efcore/blob/release/10.0/src/EFCore.Relational/Update/ReaderModificationCommandBatch.cs): `ExecuteReaderAsync` e `ConsumeAsync` são etapas separadas, com encapsulamento em DbUpdateException. O [DbContext](https://github.com/dotnet/efcore/blob/release/10.0/src/EFCore/DbContext.cs) encaminha falhas de SaveChangesAsync à instrumentação de SaveChangesFailedAsync. O número SQL interno dessa requisição não aparece no log anterior; sua comprovação pela nova captura permanece parte obrigatória da próxima execução.

### Correção mínima e garantias preservadas

Somente `tests/FinanceFlow.IntegrationTests/TransfersConcurrencySqlTests.cs` foi alterado nesta correção, além deste documento:

1. `PrimaryKeyCollision` captura `eventData.Exception` em `SaveChangesFailedAsync`, somente depois de efetivamente injetar a colisão. A instrumentação é privada dessa fixture de teste; não transforma, absorve ou substitui a exceção.
2. O teste exige exatamente `DbUpdateException` com `SqlException` interna, código **2627** e mensagem contendo **PK_Transfers**. Não foi adicionada tolerância genérica a 2601 ou a outras falhas. Neste cenário há colisão de uma PRIMARY KEY nomeada, não de um índice único independente.
3. Permanecem as verificações de HTTP 500, uma única transferência original, ausência de snapshot para a chave que falhou, saldos sem efeito adicional e criação válida ao repetir a chave após remover a injeção. Na execução anterior, essas asserções posteriores ao código SQL não chegaram a ser executadas; precisam ser comprovadas na regressão.

O store permanece inalterado: replay de colisão depende de IdempotencyRecord, código 2601 e nome exato do índice idempotente. A colisão PK_Transfers não satisfaz esse filtro nem o filtro temporário 1205/-2, e continua no tratamento global seguro de erro inesperado. Não foi identificado defeito financeiro que justifique alterar a aplicação. Atomicidade, rollback, snapshots, cálculos, permissões, migrations e tratamento de cancelamento não foram modificados. A instrumentação lê o erro internamente e não o expõe ao cliente HTTP.

### Validações após esta última correção

| Verificação | Resultado efetivamente obtido pelo Codex |
|---|---|
| Build Release `--no-restore` | Saída 0; sete projetos, zero erros, cinco NU1900; sem warnings C#/analisadores. Log: `.local/6a-pk-diagnostics-build.log` |
| Docker no sandbox | Saída 1; acesso negado ao config.json do Docker e ao pipe docker_engine; sem alteração de proteções |
| Teste corrigido e suíte SQL após a alteração | Não executados no sandbox; aguardam execução externa com o script existente |
| Auditoria NuGet | Inconclusiva; cinco NU1900 no build, sem supressão |
| Revisão estática | Mantidas as asserções específicas de SQL/PK e as verificações financeiras; nenhum teste adicionado, removido ou ignorado; total esperado continua 557 |
| `git diff --check` | Aprovado; nenhum stage, commit ou push |

## Validação externa no PowerShell com Docker Linux

Pré-requisitos: Docker Engine Linux acessível ao usuário, imagem `mcr.microsoft.com/dotnet/sdk:10.0`, acesso NuGet/MCR e recursos para SQL Server 2022 x64/Testcontainers. Reutiliza os mounts de código e socket já confirmados pelo usuário, com `TESTCONTAINERS_HOST_OVERRIDE=host.docker.internal`. Não exige carregar `.env`, SDK no host ou executar scripts contra o banco persistente.

Execute este único comando no PowerShell externo:

```powershell
& C:\FinanceFlow\scripts\Test-DockerSuite.ps1
```

O script copia o código montado somente para leitura para `/work`, excluindo `.git`, `.local`, `bin`, `obj`, `.env` e diretórios locais de configuração. Restaura e compila no contêiner; executa uma vez cada uma das três assemblies de testes, sem filtros ou skips, usando o reporter nativo `-result-xml` já fornecido pelo xUnit instalado. Esse modo diagnóstico não altera `global.json`, Microsoft.Testing.Platform, projetos, dependências ou CI. Nenhuma assembly de testes é carregada no Windows.

Resultados preservados:

- `.local/6a-docker-tests.log`: stdout/stderr completos de todas as tentativas, acrescentados sem apagar logs anteriores.
- `.local/6a-docker-results/<execução>/console.log`: saída completa dessa execução.
- Na mesma pasta: XML nativo de cada projeto, códigos de restore/build/testes e `docker.exitcode`, contendo o código real do Docker.
- Console: resumo e, para cada falha registrada no XML, nome completo, mensagem de asserção/exceção e stack trace. Mensagens internas `fail:` não são classificadas como falhas de teste.

Falhas de restore/build interrompem a tentativa sem indicar aprovação. Após build bem-sucedido, os três módulos são executados mesmo que um falhe. O primeiro código de falha é propagado pelo Docker; o PowerShell preserva esse código. Se o Docker retornar zero sem os três relatórios ou sem os **557 aprovados, zero falhas e zero ignorados** esperados, o diagnóstico retorna erro 3 e mantém o código real do Docker no arquivo separado. Não há tolerância a casos ignorados, fixture indisponível ou descoberta incompleta.

O script foi verificado sintaticamente e seu tratamento da recusa do Docker foi executado no sandbox. A primeira execução Linux com XML identificou a última falha; a execução posterior comprovou **557/557**, conforme o registro final abaixo. O comando permanece disponível para regressões, sem exigir alterações manuais de código. Os NU1900 históricos mantêm a auditoria NuGet inconclusiva; a ausência desse aviso no novo build não substitui uma auditoria de vulnerabilidades conclusiva.

## Validação externa final e auditoria consolidada — 20/11/2026

**Parecer: APROVADA PARA COMMIT, sem problemas bloqueantes encontrados.** A revisão compreendeu os **33 arquivos locais da 6A: 14 modificados e 19 novos**, em relação a `f0969b8`, incluindo código, testes, migration/target/snapshot, script diagnóstico e documentação. Nesta auditoria foram editados somente este documento, `docs/etapa-6/README.md` e o README principal para reconciliar o estado de validação.

### Evidência externa conferida pelo Codex

O usuário executou `scripts/Test-DockerSuite.ps1` no PowerShell com Docker Linux e SQL Server/Testcontainers. Foram lidos os três relatórios XML e os arquivos de código de saída em **`.local/6a-docker-results/20261120-224229-183-17da7b`**, além do log dessa execução. A saída combinada é preservada em `.local/6a-docker-tests.log`.

| Projeto | Total | Aprovados | Falhas | Ignorados | Erros do runner |
|---|---:|---:|---:|---:|---:|
| FinanceFlow.Domain.Tests | 112 | 112 | 0 | 0 | 0 |
| FinanceFlow.Application.Tests | 105 | 105 | 0 | 0 | 0 |
| FinanceFlow.IntegrationTests | 340 | 340 | 0 | 0 | 0 |
| **Total** | **557** | **557** | **0** | **0** | **0** |

Os XMLs contêm efetivamente 557 entradas de teste com resultado `Pass`: **477 da baseline e 80 da 6A**. `restore.exitcode`, `build.exitcode`, os três códigos dos módulos de teste e **`docker.exitcode` são todos zero**. `console.log` registra SDK **10.0.404**, build com **zero warnings e zero erros**, e inicialização do Testcontainers. Nenhuma ocorrência de NU1900 aparece nesse log específico. Isso não apaga os avisos históricos nem permite declarar a auditoria de vulnerabilidades concluída; não foi apresentada nova auditoria NuGet conclusiva separada.

Os dois testes anteriormente corrigidos constam como `Pass`:

- `TransfersPersistenceSqlTests.Canceled_fixture_rows_remove_both_effects_and_readiness_requires_select_on_transfers`: a identidade administrativa altera permissões somente no banco descartável; permanecem as verificações de readiness 503/liveness 200 e reversão simétrica dos efeitos preparados como cancelados.
- `TransfersConcurrencySqlTests.Unrelated_primary_key_collision_is_not_replayed_and_does_not_create_a_success_snapshot`: a captura de SaveChanges comprova `DbUpdateException → SqlException`, **2627 e PK_Transfers**, HTTP 500, ausência de snapshot de sucesso indevido, preservação do saldo e recuperação com a mesma chave. Não há tolerância a código genérico ou falso replay.

Logs internos de exceções provocadas intencionalmente não foram tratados como falhas do runner. Os resultados são da execução externa do usuário, posteriormente inspecionada pelo Codex. Não se executaram novamente os testes nem se aplicaram alterações a banco persistente nesta auditoria.

### Resultado da revisão técnica

| Área | Conclusão da auditoria |
|---|---|
| Arquitetura e escopo | Domain sem EF/ASP.NET; Application com casos de uso e porta específica; Infrastructure concentra SQL; controller usa o serviço. Somente POST e GET individual de transfers, sem antecipar 6B/6C |
| Regras financeiras | Contas distintas, próprias, BRL e ativas; Amount positivo/exato/limitado; data financeira respeita ambas as aberturas e o calendário injetado; negativo permitido; nenhuma FinancialTransaction derivada |
| Saldos e histórico | Agregação SQL por usuário/corte, sinais opostos a partir da mesma linha Transfer, filtro de canceladas e posição zero antes da abertura. Preservados decimal verificado, contas inativas e saldo sem materialização |
| Idempotência | Namespace transfers.create.v1 por usuário/chave; hash canônico; replay do snapshot completo; conflito de payload; índice único e reconhecimento restrito da colisão esperada; PK não relacionada não vira replay |
| Atomicidade e concorrência | Leituras das duas contas ordenadas sob RepeatableRead; locks retidos até o commit; Transfer e snapshot confirmados juntos; nenhum sucesso novo antes do commit. Testes reais de desativação nas duas ordens, disputa por chave, rollback, timeout, deadlock e perda da resposta |
| Segurança HTTP | JWT/proprietário autenticado, consultas isoladas, 404 para recurso alheio/inexistente, JSON desconhecido rejeitado, conversor monetário exato, no-store, Problem Details e OpenAPI alinhados ao escopo |
| Migration e modelo | Nova 20261120000100_TransfersDelivery6A, FKs compostas/NoAction, CHECKs, índices, date/decimal(18,2)/datetimeoffset/rowversion. Históricas intactas e snapshot coerente; migration incremental e modelo validados nos testes |
| Privilégios | Somente SELECT/INSERT novos em Transfers; sem UPDATE/DELETE/DDL para a API. Verificador continua rejeitando grants excessivos e delegação; permissões anteriores preservadas |
| Regressão e higiene | 477 testes anteriores preservados, sem remoções/skips; nenhuma dependência ou workflow alterado; logs/XML ignorados pelo Git; nenhum segredo ou arquivo temporário inesperado encontrado nos arquivos do diff |

`git diff --check` aprovado. Staging vazio. Nenhum código de produção, teste, migration, dependência ou configuração foi alterado nesta auditoria. Não houve stage, commit, push ou publicação.

Limitações não bloqueantes para o commit: a auditoria NuGet anterior segue inconclusiva; a classificação da colisão idempotente continua dependente da mensagem SQL esperada em inglês, como nas implementações anteriores; o saldo consolidado em um comando ReadCommitted não promete snapshot global entre requisições independentes. Publicação autorizada e CI da 6A ainda não ocorreram. O uso em banco persistente exige migration e provisionamento explícitos/autorizados, sem execução nesta auditoria.

## Componentes e pendências

Novos componentes: Domain/Transfers/Transfer; Application/Transfers/{TransferContracts,TransfersService,TransferCreationFingerprint}; Infrastructure/Persistence/Transfers/{TransferConfiguration,TransferStore}; migration/target 6A; Api/Contracts/Transfers/CreateTransferRequest; TransfersController; seis arquivos de testes e dois documentos desta pasta. Alterações localizadas: composição/DbContext, snapshot, provisionamento/verificador, readiness/no-store, saldo e OpenAPI. README e histórico de fechamento de 5C foram atualizados. Nenhuma biblioteca nova.

Não resta pendência funcional bloqueante identificada para o commit da 6A. O histórico externo evoluiu de **555 aprovações/duas falhas**, para **556 aprovações/uma falha** e finalmente **557/557**, comprovando as duas correções sem enfraquecer as asserções. Permanecem a auditoria NuGet conclusiva e, quando autorizadas, publicação/CI da 6A. Os bloqueios históricos locais de Docker/Controle de Aplicativo permanecem distintos da execução externa aprovada; não foram contornados. Não há publicação ou aprovação de CI de 6A.

Limites preservados: mensagens SQL de colisão esperadas em inglês; consulta consolidada sob ReadCommitted não é garantia de snapshot pontual de todo o banco; timeout com resultado incerto exige a mesma chave. Cancelamento operacional pertence à 6B; listagem e extrato à 6C; relatórios à Etapa 7. Nenhuma dessas funcionalidades foi iniciada. Sem alteração em bancos, credenciais, certificados ou volumes persistentes.

**Sem stage, commit ou push. Etapa 6 ainda não concluída.**

## Publicação posterior confirmada pelo usuário — baseline da 6B

Após a auditoria histórica acima, o usuário confirmou **6A publicada em 6050035 — feat: implement idempotent account transfers**, com **557 aprovados, zero falhas/ignorados**, e GitHub Actions **44857771237 — success**. Esses resultados pertencem à 6A, não à [6B](entrega-6b-validacao.md), autorizada posteriormente. As evidências e limitações anteriores foram preservadas. O script Test-DockerSuite.ps1 foi posteriormente atualizado para os logs/contagem da 6B; os caminhos 6a e a contagem 557 descritos acima representam a execução histórica. Nenhum commit ou push foi realizado pelo Codex.
