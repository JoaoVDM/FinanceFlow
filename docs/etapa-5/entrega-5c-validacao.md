# Entrega 5C — Consistência financeira, concorrência e fechamento

## Estado, baseline e origem das evidências

**VALIDAÇÃO EXTERNA E AUDITORIA TÉCNICA APROVADAS — PUBLICAÇÃO E CI PENDENTES.** A primeira revisão consistiu em auditoria, cobertura e documentação, sem alteração de produção. Posteriormente, o usuário confirmou **477 executados: 475 aprovados, duas falhas e zero ignorados** com SQL Server/Testcontainers. As duas falhas motivaram a correção localizada do tratamento de deadlock no POST e da instrumentação do teste de overflow, descritas abaixo. Após as correções, o usuário confirmou nova execução externa: **477 aprovados, zero falhas e zero ignorados**, em **1m50s**, com Docker e SQL Server/Testcontainers. A auditoria NuGet permanece inconclusiva pelos NU1900. Nenhuma dessas execuções externas é atribuída ao Codex.

Workspace confirmado em `C:\FinanceFlow`, branch `main`, HEAD `b548f4f` e Git limpo antes de editar. Conforme confirmação do usuário, 5A foi publicada em `f9e7d12`, com 386 testes e CI #7 aprovados; 5B foi publicada em `b548f4f`, com 461 testes aprovados, zero falhas/ignorados e CI #8 **Success**. São evidências anteriores e externas, não resultados de 5C.

Não foram adicionados endpoints, entidades, serviços de produção, bibliotecas, schema, migrations, caches ou mecanismos de isolamento globais. POST/GET/PUT/DELETE de transações e GET de saldo mantêm seus contratos. Transferências, extratos e relatórios não foram iniciados. A Etapa 5 **não está oficialmente encerrada**: a validação externa de 5C foi confirmada, mas publicação autorizada e CI aprovada continuam pendentes.

## Análise de lacunas antes das alterações

Referências: [critérios da Etapa 5](../etapa-1/criterios-de-aceitacao.md#etapa-5--lançamentos-e-saldos), [contratos](../etapa-1/contratos-rest.md), modelo e ADRs 003/004/006/007. “Coberto na baseline” indica testes existentes, com aprovação externa informada para 5B; não significa nova execução SQL aprovada no sandbox.

| Critério aprovado | Situação inicial | Evidência existente / complementação 5C |
|---|---|---|
| Receita/despesa com sinal correto; saldo negativo permitido | Coberto | `TransactionsSqlTests.Creation_retrieval_and_current_balance_use_the_correct_sign`; 5C acrescenta reconciliação conjunta |
| Rejeitar categoria incompatível, recursos alheios/inativos, abertura e futuro | Coberto | `TransactionsSqlTests`, `TransactionsPersistenceSqlTests`, testes de domínio e aplicação; preservados |
| Edição financeira exige atividade; descrição/exclusão preservam vínculos históricos | Coberto | `TransactionMutationSqlTests` e `FinancialTransactionChangesTests`; ampliada corrida com conta de origem |
| Histórico com corte, abertura, retroativos, exclusões e centavos exatos | Parcialmente coberto | Havia cenários individuais; faltava reconciliação independente de uma sequência combinada, com várias contas/usuários e paginação |
| Operação e registro idempotente atômicos | Coberto | `TransactionsConcurrencySqlTests.Failure_between_transaction_insert_and_snapshot_insert_rolls_back_everything_and_allows_retry` e equivalente de contas; acrescentada falha após ambos os INSERTs, antes do commit |
| Timeout/repetição não duplica; replay permanece original após edição/exclusão | Parcialmente coberto | Replay pós-edição/exclusão já coberto; faltavam POST com timeout SQL real e perda de resposta após commit confirmado |
| Criação versus desativação tem ordenação válida | Coberto | `TransactionsConcurrencySqlTests` verifica as duas ordens para conta/categoria, RepeatableRead e shared locks reais |
| Edição/exclusão concorrentes respeitam versão e não repetem efeito | Coberto | `TransactionMutationConcurrencySqlTests` verifica dois PUTs, PUT/DELETE e dois DELETEs, com barreiras e inspeção do WHERE SQL |

Os oito critérios foram revisados: inicialmente, seis estavam cobertos na baseline e dois parcialmente cobertos. As ampliações foram implementadas e sua execução SQL ficou pendente no sandbox. A confirmação externa posterior de 477/477 cobre a regressão e as ampliações, incluindo as duas correções. Nenhum critério foi simplesmente desabilitado ou dispensado; a tabela preserva a análise inicial das lacunas.

Lacunas adicionais da consolidação: conta vazia com saldo inicial assinado em vários cortes; virada exata do dia financeiro no pipeline HTTP; namespace por Operation e JSON semanticamente equivalente; overflow seguro da agregação; deadlock SQL real na criação; disputa com desativação da conta de origem, além do destino já testado. Esses subcenários específicos não estavam cobertos integralmente pela baseline.

## Auditoria do comportamento preservado

- `FinancialTransaction`, `FinancialTransactionsService` e stores continuam validando proprietário, tipo, centavos, limites, datas e atividade. Somente descrição/no-op pode permanecer em conta inativa; categoria histórica inalterada não exige reativação. Exclusão lógica independe de atividade e preserva os campos financeiros.
- `FinancialTransactionStore.SaveChangeAsync` aplica a versão recebida a `RowVersion.OriginalValue` e força `UpdatedAtUtc` modificado. A precondição participa do UPDATE, inclusive no PUT sem efeito financeiro. Não há retry com uma versão nova. Recurso excluído/alheio/inexistente retorna 404; versão antiga de recurso vivo retorna 412; formato/ausência continuam 400/428.
- PUT protege conta original/destino e categoria com RepeatableRead até o commit, carregando a transação inicialmente fora dessa unidade para evitar conversões de shared locks entre editores. POST protege os vínculos na mesma transação da criação/snapshot. DELETE usa transação local e não depende da atividade.
- `FinancialTransactionStore.BalanceAsync` filtra proprietário, corte inclusivo e `!IsDeleted` no SQL. OpeningBalance e SUM assinado ficam separados na projeção; a soma final é `checked decimal` em C#, evitando redução de escala SQL. Antes da abertura retorna zero. Não depende da lista/página, não armazena CurrentBalance e inclui contas inativas.
- Fingerprints e snapshots existentes não foram alterados. A chave é segregada por UserId/Operation/Key; replay usa a resposta persistida, não o estado atual da entidade. Só a colisão do índice idempotente reconhecido é tratada como replay.
- Configurações, FKs compostas, CHECKs, rowversion, campos imutáveis e exclusão lógica permanecem. `DatabaseTasks` mantém SELECT/INSERT e UPDATE nas nove colunas editáveis de transações; sem UPDATE em Id/UserId/CreatedAtUtc/RowVersion, DELETE físico, DDL ou UPDATE/DELETE em IdempotencyRecords. Os testes existentes de grants excessivos permanecem obrigatórios.
- JWT/revogação, isolamento de consultas/contagens, Problem Details, parsing do JSON monetário original, If-Match, no-store e OpenAPI não sofreram mudanças. Não há double/float nas operações financeiras revisadas.

Na primeira execução não houve correção funcional de produção: as mudanças se limitaram a cobertura e documentação. A correção posterior do tratamento de deadlock está registrada na seção seguinte. Falhas da fixture por Docker indisponível não foram classificadas como defeitos financeiros.

## Testes acrescentados: 16 casos SQL

### FinancialReconciliationSqlTests — seis casos

- Três casos de conta sem lançamentos: saldo inicial zero, positivo e negativo, antes/na/depois da abertura e no dia financeiro atual. Lista vazia comprova que saldo inicial não vira receita.
- Um cenário integrado de lançamentos fictícios em duas contas próprias e uma conta de outro usuário. Valores esperados são posições literais calculadas separadamente, sem chamar o agregado de produção como oráculo. Inclui receitas/despesas iguais na abertura, saldo negativo, retroativo, edição de valor, inversão de Kind nas duas direções com categoria compatível, troca de conta/data, exclusão de receitas/despesas, patrimônio inativo e lista paginada sem impacto no saldo.
- Um caso na fronteira `2026-11-19T02:59:59Z` → `03:00:00Z`: o calendário de São Paulo muda de 18 para 19. POST, PUT, futuro/asOf e corte padrão são exercitados com TimeProvider controlado somente no serviço financeiro. O relógio e a validação JWT permanecem reais e inalterados; nenhuma expectativa de fronteira depende do relógio da máquina.
- Um caso de erro SQL 8115 na agregação, induzido com SUM de valores decimal(38,2) em um comando de teste. Verifica erro seguro/no-store, ausência de saldo parcial e recuperação na consulta seguinte. É injeção de falha aritmética real do SQL, não persistência de valores fora do contrato nem prova de um overflow atingível com poucos lançamentos válidos. As fronteiras monetárias normais já têm testes preservados.

Posições independentes de referência do cenário integrado:

| Momento / corte | Conta A | Conta B |
|---|---:|---:|
| Antes da abertura | 0.00 | 0.00 |
| Abertura, após receita/despesa iguais | 100.25 | -20.10 |
| Estado inicial em 2000-01-02 | -50.25 | 20.30 |
| Estado inicial em 2000-01-04 | -25.00 | 19.29 |
| Após retroativo, em 2000-01-04 | -24.93 | 19.29 |
| Após reduzir despesa, em 2000-01-04 | 75.07 | 19.29 |
| Após inverter os dois tipos, em 2000-01-04 | 24.57 | 21.31 |
| Após mover retroativo entre contas, em 2000-01-04 | 24.50 | 21.38 |
| Após exclusões e desativação, em 2000-01-04 | 64.90 | -19.02 |

O teste também verifica cortes intermediários após as alterações. O histórico é recalculado a partir do estado atualmente válido, não de versões temporais anteriores.

### FinancialRecoverySqlTests — oito casos

- Mesma chave em accounts/transactions não colide entre operações; ordem das propriedades JSON, notação exponencial exata e caixa dos GUIDs não alteram o comando. Status, corpo, Content-Type, Location e ETag originais são comparados.
- Dois casos de falha após SaveChanges do snapshot, antes do commit (contas/transações): nenhum efeito financeiro nem registro de sucesso permanece; repetição com mesma chave cria uma única vez.
- Dois casos de perda simulada da resposta após commit SQL real (contas/transações): a primeira chamada falha, mas o registro confirmado é recuperado por replay; não há segunda criação. Para transações, o replay também ocorre após desativar ambos os vínculos.
- Dois casos de timeout real no INSERT (contas/transações): erro -2, resposta 503, rollback, ausência de snapshot de sucesso e retry/replay com a mesma chave. WAITFOR provoca timeout do provider; não é usado para inferir ordenação concorrente.
- Um deadlock real em banco descartável: conexão independente segura a tabela de transações enquanto o POST mantém shared lock na conta; a segunda conexão pede UPDATE nessa conta. Uma barreira marca o INSERT, e a prioridade alta do bloqueador determina a vítima. O teste exige erro SQL 1205, 503, rollback e retry/replay sem duplicação. Não sintetiza uma SqlException nem usa mock para provar integridade.

### TransactionMutationConcurrencySqlTests — dois casos adicionais

As duas teorias existentes foram ampliadas para incluir a **conta de origem**, mantendo os casos anteriores de conta/categoria de destino. Testam edição confirmando antes da desativação e desativação confirmando antes da validação; mantêm DbContexts independentes, barreiras, inspeção de locks/isolation e saldos de ambas as contas. Nenhum sleep arbitrário foi introduzido.

## Histórico da primeira execução de validações pelo Codex

| Verificação | Resultado comprovado nesta execução |
|---|---|
| SDK | 10.0.404 |
| Restore com NuGet.Config | Saída 0; cinco NU1900 |
| Build Release | Sete projetos compilados; zero erros, nenhum warning C#/analisadores; cinco NU1900 |
| Suíte completa | **477 casos: 285 aprovados, 192 falhas, 0 ignorados**; saída 2; aproximadamente 42s |
| Domain/Application | Projetos aprovados; cobertura anterior preservada |
| Falhas SQL | Todos os 192 casos falharam na construção da IdentitySqlFixture por DockerUnavailableException; não atingiram suas asserções |
| Auditoria NuGet com transitivos | Saída 1; índice api.nuget.org inacessível / socket api.nuget.org:443 proibido; **inconclusiva** |
| Revisão Git | `git diff --check` aprovado; sete arquivos no escopo (quatro modificados e três novos), nenhum staged; sem alterações em src, migrations/snapshot, dependências ou workflow |
| Revisão documental e conteúdo adicionado | Links locais válidos, sem whitespace final nos arquivos novos, sem arquivos sensíveis/temporários ou indicadores de segredos identificados entre as alterações |

Docker retornou acesso negado a `C:\Users\jvito\.docker\config.json` e impossibilidade de conectar ao pipe `docker_engine`. Nenhuma proteção, ACL ou configuração do sandbox foi alterada. Os 192 casos SQL incluem os 176 anteriores e os 16 adicionados. Não foram ignorados ou substituídos por SQLite/EF InMemory. Não se atribui a 5C a aprovação externa dos 461 testes da baseline.

Logs locais ignorados: `.local/5c-restore.log`, `.local/5c-build.log`, `.local/5c-tests.log` e `.local/5c-audit.log`. Podem ser inspecionados localmente; não são artefatos de publicação. NU1900 não foi suprimido e não comprova ausência de vulnerabilidades.

## Validação externa posterior e correções de 20/11/2026

O usuário executou a suíte no PowerShell com Docker Desktop e SQL Server/Testcontainers funcionando: **477 testes executados, 475 aprovados, duas falhas e zero ignorados**. Essa execução é externa; não foi realizada pelo Codex. Os testes falharam nas asserções de HTTP do deadlock (503 esperado, 500 recebido) e da captura privada do número do overflow (8115 esperado, null recebido). O log externo confirmou `SqlException` 8115. Naquele momento ainda não havia aprovação externa das correções seguintes; a nova confirmação está registrada na seção de validação externa final. A CI de 5C permanece sem aprovação.

### Deadlock: correção de produção restrita ao POST de transações

O teste cria uma disputa real entre duas conexões: o bloqueador retém a tabela de transações e depois pede UPDATE na conta; o POST retém o shared lock da conta e tenta inserir o lançamento. A asserção de SQL 1205 precedia a comparação HTTP e passou na execução externa, confirmando a vítima de deadlock. Não foi necessário mudar a preparação nem aceitar 500 como sucesso.

A estratégia SQL Server padrão do EF Core 10.0.13, mesmo sem retry habilitado, encapsula falhas transitórias de SaveChanges em `InvalidOperationException`, contendo `DbUpdateException`/`SqlException`. O catch anterior reconhecia somente `DbUpdateException` com SqlException diretamente interna. O encapsulamento escapava dessa classificação; a Application não o traduzia e o handler global sanitizava a InvalidOperationException como 500. O caminho foi identificado no código do projeto e no [código oficial da estratégia EF Core utilizada](https://github.com/dotnet/efcore/blob/v10.0.13/src/EFCore.SqlServer/Storage/Internal/SqlServerExecutionStrategy.cs); não se atribui ao sandbox a observação runtime dessa cadeia.

`FinancialTransactionStore.CreateAsync` agora usa um filtro privado que remove apenas os wrappers conhecidos e exige SqlException com número **1205 ou -2**. Retorna `TransactionFailure.Unavailable`, já mapeado para 503 pelo controller. InvalidOperationException sem essa causa SQL específica continua fora desse catch. Não houve mudança global de classificação de exceções, retry, fingerprint, snapshots, cálculos, regras de atividade, PUT/DELETE ou reconhecimento de colisão idempotente.

No caminho de indisponibilidade, a transação é descartada pelo `await using`, revertendo qualquer unidade ainda ativa; não se tenta um segundo Rollback explícito de uma vítima já abortada pelo SQL Server. O teste mantém a exigência de 503 e acrescenta observação privada de `SaveChangesFailedAsync` para conferir a cadeia de exceções do EF. Preserva as verificações de ausência de criação/snapshot, saldo zero e reutilização da mesma chave com um único lançamento e replay original. As asserções de rollback agora precedem a comparação HTTP; elas não haviam sido atingidas na execução externa que parou no 500. Sua aprovação integra a confirmação externa posterior de 477/477.

### Overflow: correção exclusiva da instrumentação do teste

O teste não procurava o código SQL na resposta HTTP: sua propriedade privada `SqlError` era preenchida somente em `DbCommandInterceptor.CommandFailedAsync`. ExecuteReader pode entregar um reader e o erro 8115 surgir depois, em ReadAsync, durante a enumeração do resultado. Nesse caso não há falha na execução inicial do comando para esse callback capturar, embora o EF registre a exceção de leitura no log.

Somente a factory do teste de overflow passa a observar o evento **CoreEventId.QueryIterationFailed** do EF, cujo payload é **DbContextTypeErrorEventData**, por callback estruturado de LogTo, além de manter CommandFailedAsync para erros antecipados. O tipo do payload foi conferido na documentação XML do pacote instalado e no [código oficial do evento](https://github.com/dotnet/efcore/blob/v10.0.13/src/EFCore/Diagnostics/CoreLoggerExtensions.cs). A exceção SQL real é consultada privadamente no processo de teste após a injeção da consulta; não há parsing de mensagens, logger público adicional ou habilitação de dados sensíveis. A asserção `SqlError == 8115` continua obrigatória.

Nenhum cálculo de saldo ou handler de produção foi alterado para esse problema. A expectativa HTTP permanece **503**, coerente com o contrato atual de falha de banco/DbException. O teste exige Problem Details, no-store, ausência de balance parcial, código público `service_unavailable`, ausência de campos SQL/stack trace e de mensagens internas. A consulta seguinte deve retornar o saldo exato original. Nenhum código interno do SQL foi adicionado ao JSON público.

### Validações das correções no sandbox

| Verificação | Resultado efetivamente executado |
|---|---|
| Baseline e alterações preexistentes | main em b548f4f; sete arquivos locais anteriores preservados |
| Build Release final | Saída 0; sete projetos, zero erros, cinco NU1900 e nenhum warning C#/analisadores |
| Dois testes afetados isoladamente | Dois executados, zero aprovados, duas falhas de fixture Docker, zero ignorados; não atingiram as asserções |
| Regressão completa final | **477 executados: 285 aprovados, 192 falhas de fixture Docker, zero ignorados**; saída 2, aproximadamente 28s |
| Verificação das falhas SQL | 192 cabeçalhos de falha e 192 exceções de construção da IdentitySqlFixture; Docker inacessível no sandbox |
| Auditoria NuGet | Não repetida nesta correção; permanece inconclusiva. Build/testes exibiram cinco NU1900 |
| Revisão Git final | `git diff --check` aprovado; oito arquivos locais no total (cinco modificados e três novos), nenhum staged. Esta correção alterou seis arquivos, preservando as demais alterações anteriores; nenhuma migration, dependência, privilégio ou banco persistente foi alterado |

A primeira tentativa de compilação da instrumentação revelou dois erros de ordem dos argumentos de LogTo; foram corrigidos antes do build final aprovado. Uma tentativa de obter ajuda passando `-- --help` pelo dotnet test encerrou o SDK com erro de protocolo de ajuda, sem executar testes; a execução isolada foi feita depois diretamente pelo runner xUnit da assembly compilada, sem alterar configuração, descoberta ou segurança.

Logs locais ignorados: `.local/5c-fixes-build.log` (tentativa inicial), `.local/5c-fixes-build-final.log`, `.local/5c-fixes-affected.log` e `.local/5c-fixes-tests.log`. O total permanece **477**: nenhum teste removido, ignorado ou acrescentado para contornar as duas falhas. Ao encerrar essa rodada do sandbox, a confirmação externa ainda estava pendente; o resultado posterior está registrado a seguir, sem substituir esse histórico.

## Validação externa final e auditoria consolidada de 20/11/2026

O usuário confirmou nova execução no PowerShell com Docker e SQL Server/Testcontainers após as correções:

| Evidência externa fornecida pelo usuário | Resultado |
|---|---|
| Suíte completa de 5C | **477 aprovados, 0 falhas, 0 ignorados** |
| Duração | **1m50s** |
| Consulta de vulnerabilidades do NuGet | Avisos **NU1900**; auditoria **inconclusiva** |

Essa aprovação encerra a pendência de regressão SQL e dos dois testes corrigidos. Inclui as asserções de cadeia real de exceções/1205, rollback, repetição com a mesma chave e captura privada de 8115 com resposta HTTP segura. Não é uma execução do Codex, uma nova comprovação de build sem warnings ou uma auditoria de vulnerabilidades concluída. As falhas de fixture no sandbox e a primeira execução externa de 475/477 foram preservadas. Não se infere aplicação de migrations/provisionamento em banco persistente.

**Parecer da revisão técnica consolidada: APROVADO, sem problemas bloqueantes identificados.** A comparação com `b548f4f`, em `main`, incluiu todos os **oito arquivos locais: cinco modificados e três novos ainda não rastreados**.

- **Deadlock/timeout:** o filtro examina tipos e InnerException, sem depender de mensagens. Aceita somente SqlException 1205/-2 diretamente ou nos wrappers conhecidos de InvalidOperationException/DbUpdateException. Não captura OperationCanceledException/TaskCanceledException nem transforma exceções não transitórias em 503. Os tokens de cancelamento continuam propagados às operações; o descarte da transação preserva a limpeza. Sucesso permanece depois do commit, sem retry automático; a colisão idempotente mantém seu reconhecimento restrito.
- **Overflow:** o callback de QueryIterationFailed usa DbContextTypeErrorEventData, com SqlException consultada somente na instrumentação privada. Cada teste cria sua própria factory/observer; a captura é ativada pela consulta financeira injetada e não há outras requisições concorrentes nesse cenário. O código 8115 não vem do JSON nem é exposto nele. São exigidos 503/Problem Details/no-store, ausência de saldo parcial e detalhes SQL/stack trace, além da consulta seguinte com saldo correto. O cálculo de produção não foi alterado.
- **Consistência e testes:** as posições esperadas são literais calculados independentemente da consulta de produção; cobrem saldo inicial, retroativos, inversão de tipos, troca de conta/data, exclusão, inatividade, paginação e usuário distinto. Relógio financeiro controlado, barreiras, conexões/DbContexts independentes, inspeção de locks e erros SQL reais sustentam os cenários. WAITFOR provoca timeout real; não é evidência de ordenação baseada em atraso arbitrário. Não há skips, mocks substituindo SQL ou asserções enfraquecidas.
- **Compatibilidade e segurança:** somente o tratamento conhecido de indisponibilidade na criação foi alterado em produção. Preservados invariantes, isolamento, precisão decimal, snapshots e contratos de 5A/5B. Sem alteração de permissões, dependências, migrations históricas/snapshot ou funcionalidades da Etapa 6. Nenhum arquivo sensível/temporário indevido ou indicador de segredo foi identificado no conjunto de alterações.

Nesta auditoria foram atualizados exclusivamente `README.md`, `docs/etapa-5/README.md` e este documento. Código de produção e testes foram preservados; não houve nova execução de build/testes. `git diff --check` foi aprovado e o staging permanece vazio. A revisão estática e os testes informados não comprovam ausência de vulnerabilidades nem substituem futura CI/validação operacional. **5C não foi publicada e não tem CI aprovada; a Etapa 5 permanece oficialmente aberta.**

## Reprodução externa obrigatória

Pré-requisitos: SDK da faixa aprovada 10.0.4xx, Docker Desktop/Engine Linux acessível, recursos para SQL Server 2022 e acesso a NuGet/MCR. As fixtures geram credenciais e bancos descartáveis. Não carregar `.env`/segredos locais nem executar scripts contra o banco persistente; não é necessário provisioná-lo ou migrá-lo para esta entrega.

```powershell
Set-Location C:\FinanceFlow
$ErrorActionPreference = 'Stop'
function Assert-5CStep([string]$Name) {
    if ($LASTEXITCODE -ne 0) { throw "$Name falhou; preserve a saída." }
}
docker info --format '{{.ServerVersion}}'
Assert-5CStep 'Docker'
dotnet restore FinanceFlow.slnx --configfile NuGet.Config
Assert-5CStep 'Restore'
dotnet build FinanceFlow.slnx --configuration Release --no-restore
Assert-5CStep 'Build'
dotnet test FinanceFlow.slnx --configuration Release --no-restore
Assert-5CStep 'Regressão completa com SQL Server'
dotnet list FinanceFlow.slnx package --vulnerable --include-transitive
Assert-5CStep 'Auditoria NuGet'
git diff --check
Assert-5CStep 'Diff'
git status --short
```

Referência para reprodução da suíte: **477 aprovados, 0 falhas, 0 ignorados**, resultado confirmado externamente pelo usuário após as correções. Se houver NU1900, manter a auditoria inconclusiva mesmo que o comando liste projetos sem vulnerabilidades conhecidas. Uma falha da auditoria não deve ser contornada desabilitando-a.

Para repetir primeiro somente os dois testes afetados, depois do build Release, usando o runner xUnit existente:

```powershell
dotnet tests/FinanceFlow.IntegrationTests/bin/Release/net10.0/FinanceFlow.IntegrationTests.dll -method FinanceFlow.IntegrationTests.FinancialRecoverySqlTests.Real_deadlock_victim_has_no_partial_creation_and_can_repeat_the_same_key -method FinanceFlow.IntegrationTests.FinancialReconciliationSqlTests.Sql_aggregate_overflow_returns_a_safe_error_instead_of_a_partial_balance
```

## Pendências, limites e fechamento

- Regressão externa e dois testes corrigidos aprovados na nova execução de **477/477** informada pelo usuário. Essa pendência está encerrada; nenhum teste SQL é apresentado como aprovado no sandbox do Codex.
- Obter consulta efetiva ao índice de vulnerabilidades. Restore/build aprovados e ausência de erros de compilação não equivalem a auditoria de segurança concluída.
- Preservada a limitação histórica do reconhecimento restrito da colisão por mensagem SQL em inglês; não se alterou essa política sem defeito reproduzido.
- Resultado de commit pode ser desconhecido para o cliente em falha de comunicação; POST deve repetir a mesma chave e PUT/DELETE devem consultar o estado e respeitar a versão. Não há retry automático com versão nova.
- A consulta de saldo continua consolidada em um comando; não promete snapshot pontual de todo o banco sob ReadCommitted. Não foi habilitado RCSI/Snapshot global. Se extratos da Etapa 6 precisarem de resposta multiquery com visão pontual, implementar/testar seu isolamento nessa etapa, conforme ADR-004. Não somar páginas/respostas independentes como se fossem o mesmo instante.
- Não há nova migration: nenhum schema, target histórico ou snapshot foi alterado. Nenhuma operação foi executada em banco persistente, credenciais, certificados ou volumes.
- Publicação de 5C exige autorização; CI de 5C ainda não foi executada/aprovada. A validação externa já foi confirmada, mas o encerramento oficial da Etapa 5 depende de publicação autorizada e CI aprovada.

**Sem stage, commit ou push. Etapa 6, transferências, extratos e relatórios não iniciados.**

## Publicação e encerramento posterior confirmados pelo usuário

Após os registros históricos acima, o usuário confirmou **5C publicada em f0969b8, CI workflow #9 aprovada e 477 testes locais aprovados, zero falhas e zero ignorados**, concluindo a Etapa 5. A consulta NuGet inconclusiva por NU1900 e as falhas históricas do sandbox não foram apagadas. A [Entrega 6A](../etapa-6/entrega-6a-validacao.md) foi autorizada posteriormente sobre essa baseline; sua implementação e validações não são comprovadas pelos resultados de 5C. Nenhum commit ou push foi realizado pelo Codex.
