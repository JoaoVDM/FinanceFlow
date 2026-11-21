# Entrega 6B — Cancelamento lógico de transferências

## Estado e baseline — 21/11/2026

**APROVADA PARA COMMIT.** O usuário comprovou **613 testes executados e aprovados, zero falhas, ignorados e erros do runner**, na execução externa Docker Linux **20261121-020304-418-94cdd6**. Os três XMLs e todos os códigos de saída foram conferidos pelo Codex. A auditoria final da correção P2 e das alterações contra **6050035** não identificou impedimento técnico ao commit. A 6B permanece local, sem publicação ou CI própria aprovada; a auditoria NuGet conclusiva continua pendente e a 6C não foi iniciada.

O histórico foi preservado: **603/603** em **20261121-001714-582-bfda5d**, achado P2 e dez novos casos, **611/613** em **20261121-005532-270-9f8c53** com duas falhas na expectativa do tipo da exceção de deadlock, ajuste restrito dessa asserção e aprovação externa final **613/613**. Os resultados externos pertencem ao usuário; as limitações e execuções do sandbox estão registradas separadamente abaixo.

Workspace `C:\FinanceFlow`, branch `main`, HEAD **6050035 — feat: implement idempotent account transfers**, inicialmente sem alterações locais. O usuário confirmou a publicação de 6A, **557 testes aprovados, zero falhas/ignorados**, e GitHub Actions **44857771237 — success**. Distribuição dessa baseline: Domain 112, Application 105, Integration 340. Essas evidências externas pertencem à 6A e não comprovam as alterações da 6B.

Foram consultados AGENTS.md, roadmap, modelo, contratos, critérios, ADRs 003/004/005/006/007 e os históricos de 5B/6A. A implementação segue a exclusão lógica de FinancialTransaction, sem novo parser de ETag, dependência, camada ou framework.

## Contrato HTTP e precedência

```http
DELETE /api/v1/transfers/{id}
Authorization: Bearer <token>
If-Match: "<Base64 canônico de oito bytes>"
```

Sem DTO, sem corpo e sem exigência de Idempotency-Key. Sucesso: **204 sem corpo**, `ETag` forte da nova RowVersion persistida e `Cache-Control: no-store`. GET individual depois do cancelamento retorna 404. Não há PUT/PATCH, reativação, exclusão física, extrato ou listagem de transferências.

| Situação | HTTP |
|---|---:|
| Cancelamento confirmado | 204 |
| Corpo presente ou If-Match malformado, vazio, fraco, wildcard, múltiplo ou não canônico | 400 |
| JWT ausente, inválido ou revogado | 401 |
| Recurso inexistente, alheio ou já cancelado | 404 |
| Versão incorreta de recurso próprio ainda ativo | 412 |
| If-Match ausente | 428 |
| Rate limiting | 429, com Retry-After |
| SQL temporário conhecido | 503 |
| Exceção inesperada | Tratamento global seguro existente |

Ordem preservada: autenticação/autorização; rejeição de corpo; interpretação de If-Match; busca por proprietário e estado não cancelado; comparação antecipada da versão; gravação condicional. O componente `IfMatchHeader` não foi alterado. O estado de recursos alheios nunca é usado para responder de forma distinta de recurso inexistente. Problem Details, no-store, HTTPS, JWT/revogação, limites de tamanho e rate limiting existentes permanecem aplicáveis.

Após o primeiro cancelamento, novas requisições com header válido retornam **404**, tanto com o ETag original quanto com o ETag do 204. Headers inválidos continuam sendo rejeitados antes da busca. Em corrida, um cancelamento confirmado produz 204 e o perdedor, após observar o estado cancelado, produz 404. Se outro escritor mudar a versão mantendo o recurso ativo, o resultado é 412. Não existe retry automático com a versão atual.

## Implementação e invariantes

A entrega, incluindo a correção P2, contém **22 arquivos: 14 modificados e oito novos**. Os novos são os seis arquivos de testes da implementação inicial, o arquivo adicional de falhas de consulta do P2 e este documento. Além dos componentes da tabela, foram modificados os três testes anteriores descritos na seção de cobertura, o README principal, o README da Etapa 6 e a nota cronológica da validação de 6A. Não há arquivo fora desse escopo.

| Componente modificado | Comportamento |
|---|---|
| Domain/Transfers/Transfer | `Cancel` impede repetição, marca IsCanceled e preenche CanceledAtUtc/UpdatedAtUtc com o mesmo instante UTC; preserva todos os campos financeiros e setters privados |
| Application/Transfers/TransferContracts | Método CancelAsync na porta existente, resultado com versão e erro VersionConflict; contratos de criação/leitura mantidos |
| Application/Transfers/TransfersService | Obtém UserId de ICurrentUser, valida oito bytes de versão, copia o array e encaminha o instante do TimeProvider uma única vez |
| Infrastructure/Persistence/Transfers/TransferStore | Busca da transferência própria não cancelada, UPDATE condicionado à versão original, transação local, commit e classificação de concorrência |
| Infrastructure/Persistence/DatabaseTasks | Provisionamento e verificador aceitam somente três colunas de UPDATE novas, preservando grants anteriores |
| Api/Controllers/TransfersController | DELETE sem DTO, parser IfMatchHeader existente, rejeição de corpo, 204/ETag e mapeamento de erros |
| Api/Configuration/ApiDocumentationConfiguration | Inclui transfers no transformador existente de alterações financeiras: Bearer, If-Match, ETag, no-store e respostas; nenhum Idempotency-Key no DELETE |
| scripts/Test-DockerSuite.ps1 | Preserva execução Linux, stdout/stderr, XML e códigos de saída; usa logs 6B e, após o P2, verifica a suíte de 613 casos, inclusive passed=613 |

Cancelamento não consulta nem valida atividade das contas. Funciona com origem, destino ou ambas inativas e com saldo negativo resultante. Não modifica contas, Amount, OccurredOn, Description, proprietário, criação ou IdempotencyRecords. Não cria Transfer reversa nem FinancialTransaction. Não altera fingerprints, snapshots ou a ordem transacional do POST; somente o helper privado de classificação SQL foi renomeado para reutilizar o mesmo filtro sem modificar sua lógica.

## Concorrência e atomicidade

A leitura da transferência acontece fora da transação de escrita, como no DELETE de transações. Isso evita reter shared locks concorrentes que precisariam ser convertidos em locks exclusivos. Não são adicionados locks de contas, isolamento Serializable ou contexto compartilhado entre requisições.

Em uma transação curta **ReadCommitted**, o domínio aplica o cancelamento, a persistência atribui `expectedVersion` a `RowVersion.OriginalValue`, marca UpdatedAtUtc como modificado e executa SaveChanges. O predicado SQL usa efetivamente a versão fornecida pelo cliente. A comparação inicial em memória serve somente à precedência de erros. Sucesso e ETag são retornados depois do commit.

Em DbUpdateConcurrencyException, a unidade é revertida com token independente do cancelamento da requisição; a entidade é destacada e uma consulta sem tracking verifica se o recurso próprio ainda está ativo. Retorna 412 se ativo ou 404 se ausente/cancelado. SQL 1205/-2, inclusive nos wrappers conhecidos de InvalidOperationException/DbUpdateException, segue a política existente de indisponibilidade. Outros erros não são convertidos indiscriminadamente em 503. Cancelamento da requisição não é absorvido pelo filtro. Nenhum retry automático foi introduzido.

Descarte da transação reverte falhas antes do commit, inclusive após SaveChanges. Um resultado de commit desconhecido exige observar o estado atual; repetir DELETE não pode reaplicar o cancelamento nem trocar seus timestamps. O teste SQL coordena dois saves em contextos distintos e inspeciona o WHERE/parametrização de RowVersion, sem sleeps arbitrários.

## Saldos, histórico e idempotência

A consulta de saldo **não foi alterada**: já exclui Transfers com IsCanceled=true e agrega os dois sinais no SQL, sem CurrentBalance. A mesma linha deixa de contribuir na origem e no destino. Contas inativas permanecem nas posições e o patrimônio conjunto não muda.

OccurredOn continua sendo a data financeira. O cancelamento retira os efeitos de todos os cortes inclusivos afetados, sem criar movimento em CanceledAtUtc. Cortes anteriores à transferência permanecem iguais; a posição anterior à abertura continua zero. O histórico representa o estado atualmente válido dos registros, conforme ADR-004.

POST repetido após cancelamento recupera **201, corpo, Location, ETag e Content-Type originais**, sem reativar nem duplicar a linha. Payload diferente continua 409. Nenhuma escrita de snapshot é feita pelo cancelamento. Há testes de contagem, saldos, dados originais, registro idempotente e replay após cancelamento de contas ativas/inativas.

## Schema e privilégios SQL

**Nenhuma migration necessária ou criada.** A migration 20261120000100_TransfersDelivery6A já contém IsCanceled, CanceledAtUtc, UpdatedAtUtc, RowVersion e CHECK coerente. Migrations históricas, target models, snapshot e mapeamento não foram alterados. O teste de modelo existente permaneceu aprovado na execução sem SQL, incluindo HasPendingModelChanges=false.

Além dos SELECT/INSERT existentes, a API recebe somente:

```sql
GRANT UPDATE ON OBJECT::dbo.Transfers
    (IsCanceled, CanceledAtUtc, UpdatedAtUtc) TO <usuario_da_api>;
```

Sem UPDATE de tabela, DELETE, ALTER, CONTROL, roles administrativas/datawriter ou GRANT OPTION. Sem UPDATE em Id/UserId/origem/destino/valor/data/descrição/criação/RowVersion; IdempotencyRecords continua SELECT/INSERT. O verificador admite somente os três minor_ids autorizados e continua rejeitando grants extras e delegação.

**Nada foi provisionado ou migrado em banco persistente.** Quando houver autorização específica para usar a 6B nesse banco, será necessário provisionamento explícito com o DatabaseTasks atualizado e credencial administrativa separada; não é necessária nova migration. Readiness confirma schema/leitura e não substitui a auditoria dos novos privilégios de escrita. Os testes usam exclusivamente bancos descartáveis de Testcontainers com a credencial SQL restrita da API.

## Cobertura inicialmente adicionada, antes da correção P2

Mantidos os **557 casos anteriores**, com **46 novos**, total esperado **603**. A contagem nova foi conferida pelos Facts/InlineData implementados, não representa aprovação de casos bloqueados.

| Arquivo novo | Casos | Cobertura |
|---|---:|---|
| Domain.Tests/TransferCancellationTests.cs | 4 | UTC em diferentes offsets, estado, imutabilidade, encapsulamento e repetição |
| Application.Tests/TransferCancellationServiceTests.cs | 10 | Proprietário, clock, cópia/validação de versão, sucesso, erros e ausência de retries |
| IntegrationTests/TransferCancellationContractTests.cs | 3 | JWT ausente/inválido, challenge/no-store e OpenAPI completo sem DTO/idempotência |
| IntegrationTests/TransferCancellationSqlTests.cs | 20 | Atividade das duas contas, histórico/centavos/patrimônio, replay/contagens, 400/401/404/412/428/429, body, versão e token revogado |
| IntegrationTests/TransferCancellationConcurrencySqlTests.cs | 5 | Dois contextos concorrentes, versão alterada após leitura, falhas antes/depois de SaveChanges, timeout real, rollback e recuperação |
| IntegrationTests/TransferCancellationPermissionsSqlTests.cs | 4 | Grants por coluna com credencial restrita, imutáveis, DELETE/DDL e verificador contra UPDATE amplo/CONTROL/delegação |

Distribuição esperada: **Domain 116; Application 115; Integration 372**. No projeto Integration: 113 sem banco e 259 SQL. Não há Skip, EF InMemory, SQLite ou alteração de fixture para ocultar indisponibilidade.

Expectativas anteriores legitimamente atualizadas:

- TransferContractTests preserva criação/leitura/headers, passando a esperar também DELETE na rota individual; continua proibindo PUT/PATCH/listagem. Nome do teste foi ajustado para refletir o contrato ampliado.
- TransfersPersistenceSqlTests mantém UPDATE de Amount/UserId e DELETE proibidos. Com grants por coluna, as duas negações de UPDATE passam de SQL **229 para 230**; DELETE continua 229. Não há aceitação genérica de códigos. O nome do teste reflete a proteção financeira atual.
- O double de TransfersServiceTests somente implementa o novo método da porta, sem alterar seus testes de criação/leitura.
- O caso de header vazio usa a preparação existente de FinancialMutationSqlTests: o valor é preservado no HttpContext de entrada porque HttpClient/TestServer pode omiti-lo. JWT continua sendo validado normalmente.

## Resultados executados pelo Codex

| Verificação | Resultado real |
|---|---|
| Git inicial | main, 6050035; working tree limpo |
| Docker | Acesso negado a config.json e ao pipe docker_engine; nenhuma política/ACL alterada |
| Restore com NuGet.Config | Saída 0; cinco NU1900 por índice inacessível |
| Build Release final | Saída 0; sete projetos, zero erros e zero warnings C#/analisadores; cinco NU1900 |
| Primeira regressão intermediária | 485 executados: 227 aprovados, 258 falhas de fixture Docker, zero ignorados; Domain bloqueado antes da execução |
| Regressão final | **487 executados: 228 aprovados, 259 falhas de fixture Docker, zero ignorados**, em aproximadamente 27,9s; saída 1 e uma falha de handshake de módulo |
| Application final | **115 aprovados**, incluindo dez novos; zero falhas/ignorados |
| Integration final | **113 aprovados sem SQL**, incluindo três novos; **259 falhas na construção da fixture**, sem atingir asserções SQL |
| Domain final | **116 esperados, nenhum executado**: Smart App Control bloqueou a assembly antes de iniciar; processo -532462766, FileLoadException/0x800711C7 |
| Auditoria NuGet com transitivos | Saída 1; socket api.nuget.org:443 proibido, índice inacessível; **inconclusiva** |
| Script Docker no sandbox | Saída 1 antes de iniciar contêiner; zero XML; não é uma execução de testes ou aprovação. Sintaxe PowerShell validada |
| Git/diff | git diff --check aprovado; staging vazio; migrations/dependências/workflow preservados |

As 259 entradas de falha do runner final foram conferidas: todas contêm DockerUnavailableException. Não são falhas funcionais comprovadas. Também não comprovam cancelamento, rollback, concorrência ou grants reais em SQL. Os 116 casos Domain bloqueados não são testes ignorados nem aprovações; por isso 487 executados não equivale à suíte completa de 603. Os avisos de Data Protection do host de teste e logs de exceções negativas não foram classificados como falhas adicionais do runner.

Um aviso xUnit2013 encontrado no build intermediário foi corrigido usando a asserção Assert.Single já existente, sem suprimir analisadores. O build final no sandbox foi repetido com somente os cinco NU1900. Até esse momento da implementação ainda não havia evidência externa ou CI da 6B; a validação externa posterior está registrada abaixo. Os resultados 557/557 e CI 44857771237 continuam pertencendo à baseline publicada da 6A.

Logs ignorados: `.local/6b-restore.log`, `6b-build.log`, `6b-tests.log` (intermediário), `6b-tests-final.log`, `6b-audit.log`, `6b-docker-tests.log` e `.local/6b-docker-results/`. Não foram versionados nem contêm credenciais reais de desenvolvimento fornecidas pela tarefa.

## Validação externa concluída e auditoria técnica final — 21/11/2026

**Parecer: NÃO APROVADA PARA COMMIT. Um achado de gravidade média requer correção localizada e cobertura adicional.** A auditoria revisou os **21 arquivos locais (14 modificados e sete novos)** contra a baseline publicada **6050035**, incluindo todos os não rastreados. Foram lidos AGENTS.md, ADRs relevantes, diff de produção/testes/script/documentação e o conteúdo dos arquivos novos. Nesta auditoria foram atualizados somente este documento, o README da Etapa 6 e o README principal; produção, testes, script, migrations e dependências foram preservados.

### Evidência externa conferida

O usuário executou no PowerShell externo:

```powershell
& C:\FinanceFlow\scripts\Test-DockerSuite.ps1
```

Resultados preservados em **`.local/6b-docker-results/20261121-001714-582-bfda5d`**, com saída combinada em `.local/6b-docker-tests.log`. Foram lidos os três XMLs completos, conferidas suas entradas individuais e os arquivos de código de saída:

| Projeto | Executados | Aprovados | Falhas | Ignorados | Erros do runner |
|---|---:|---:|---:|---:|---:|
| FinanceFlow.Domain.Tests | 116 | 116 | 0 | 0 | 0 |
| FinanceFlow.Application.Tests | 115 | 115 | 0 | 0 | 0 |
| FinanceFlow.IntegrationTests | 372 | 372 | 0 | 0 | 0 |
| **Total** | **603** | **603** | **0** | **0** | **0** |

Há **603 entradas efetivas com resultado Pass**, três relatórios válidos e nenhum erro do runner. `restore.exitcode`, `build.exitcode`, os três códigos de teste e **`docker.exitcode` são todos zero**. O log dessa execução registra SDK **10.0.404**, build Release com **zero erros e zero warnings** e execução pelo runner xUnit em Linux. Nenhum NU1900 aparece nesse log específico.

Esses são testes executados externamente pelo usuário e posteriormente inspecionados pelo Codex. Não foram reexecutados nesta auditoria. As 259 falhas anteriores de fixture e o bloqueio dos 116 Domain no sandbox não foram apagados nem reclassificados como aprovações. A ausência de NU1900 no novo restore/build não representa, por si só, uma auditoria de vulnerabilidades conclusiva; permanece o registro da consulta NuGet anterior bloqueada, sem nova auditoria conclusiva fornecida.

### Conclusões da revisão

| Área | Verificação técnica |
|---|---|
| Domínio e finanças | Cancelamento único na linha original; preservação de Id/UserId/contas/Amount/OccurredOn/Description/CreatedAtUtc; timestamps UTC do relógio injetado; sem reativação, exclusão física ou movimentação compensatória |
| Contas inativas e saldos | Cancelamento não consulta atividade nem exige fundos. Casos das quatro combinações de atividade e saldo negativo passaram. GET e cálculo financeiro filtram IsCanceled; os efeitos desaparecem simetricamente dos cortes afetados, preservando patrimônio e datas anteriores |
| HTTP e segurança | JWT/revogação, isolamento por proprietário, body rejeitado, parser existente, 204 vazio/ETag/no-store e precedência de 400/401/404/412/428/429 conferidos. Timeout de UPDATE retorna 503, mas a classificação de deadlock nas consultas tem a lacuna descrita abaixo. Bearer e contrato documentados no OpenAPI |
| Concorrência | OriginalValue recebe a versão do cliente; testes inspecionam o parâmetro no WHERE real. Duas requisições independentes coordenadas por barreira confirmam um 204 e um 404. Alteração de versão após a leitura produz 412 sem cancelamento parcial |
| Atomicidade e falhas | ReadCommitted curto, sem locks de contas, sucesso após commit. Falhas antes do UPDATE e após SaveChanges reverteram estado, timestamps, versão e saldos; timeout SQL -2 retornou 503 e permitiu cancelamento posterior. Sem retry automático |
| Criação e idempotência | Namespace transfers.create.v1, fingerprint e snapshots preservados. Replay após cancelamento manteve 201/corpo/Location/ETag/Content-Type sem reativar ou duplicar; payload diferente manteve 409. O helper SQL só foi renomeado, sem alteração da política de criação |
| Schema e privilégios | Nenhuma migration nova/histórica, snapshot ou mapeamento alterado; HasPendingModelChanges=false preservado. UPDATE somente nas três colunas técnicas, testado pela credencial restrita; imutáveis, DELETE, DDL, UPDATE amplo, CONTROL e delegação continuam negados/rejeitados |
| Arquitetura e escopo | Domain/Application/Infrastructure/Api preservados, sem novos pacotes ou abstrações genéricas. DELETE é a única operação nova; nenhuma listagem, extrato, PUT/PATCH ou funcionalidade da 6C |
| Testes e script | 557 casos anteriores e 46 novos presentes; testes SQL reais sem Skip/fallback/sleeps arbitrários. Script executa as três assemblies integrais, preserva logs/XML/códigos e recusa sucesso incompleto |

Os XMLs finais foram comparados aos da execução publicada da 6A (`20261120-224229-183-17da7b`): **555 nomes permanecem iguais e dois foram renomeados**, como explicado na seção de cobertura. Somados os 46 casos novos, resultam 603. As duas mudanças de expectativas anteriores permanecem específicas: novo DELETE no OpenAPI e SQL 230 somente para UPDATE de colunas proibidas; não houve remoção ou enfraquecimento dos testes.

Não foram encontrados arquivos inesperados, segredos ou artefatos temporários entre os arquivos modificados/novos visíveis ao Git. Logs e XMLs em `.local` permanecem ignorados. Migrations, cálculo de saldo, fingerprint, pacotes, configuração de runner e workflow não apresentam alterações contra a baseline. `git diff --check` aprovado e staging vazio; arquivos fora dos três documentos permitidos foram conferidos por hash antes/depois da edição.

### Achado registrado pela auditoria anterior — gravidade média (P2)

**Deadlock nas consultas do cancelamento pode resultar em HTTP 500, em vez do 503 contratado.**

- **Locais na versão auditada, antes da correção P2:** `src/FinanceFlow.Infrastructure/Persistence/Transfers/TransferStore.cs:83` (leitura inicial) e `:111` (reconsulta após conflito); classificação final em `src/FinanceFlow.Api/Errors/GlobalExceptionHandler.cs:17`. As referências de linhas e o alcance dos blocos abaixo descrevem aquela versão histórica.
- **Evidência no código:** o try do cancelamento começa na linha 97, depois da leitura inicial. A reconsulta está dentro do catch de DbUpdateConcurrencyException; exceções lançadas por esse catch não são tratadas pelo catch seguinte, na linha 115. Portanto, o filtro IsTemporarySqlFailure não abrange essas duas consultas.
- **Cadeia relevante:** o provider SQL Server sem retries continua usando SqlServerExecutionStrategy. Essa estratégia encapsula SQL 1205 em `InvalidOperationException` durante consultas; o handler global mapeia somente DbException/TimeoutException para 503 e deixa InvalidOperationException em 500. Não se trata de inferir o erro pela mensagem textual.
- **Fontes primárias:** [SqlServerExecutionStrategy do EF Core 10](https://github.com/dotnet/efcore/blob/release/10.0/src/EFCore.SqlServer/Storage/Internal/SqlServerExecutionStrategy.cs) e [detector de erros transitórios, incluindo 1205](https://github.com/dotnet/efcore/blob/release/10.0/src/EFCore.SqlServer/Storage/Internal/SqlServerTransientExceptionDetector.cs). A estratégia é usada na inicialização de consultas de [SingleQueryingEnumerable](https://github.com/dotnet/efcore/blob/release/10.0/src/EFCore.Relational/Query/Internal/SingleQueryingEnumerable.cs). A configuração local usa UseSqlServer, sem EnableRetryOnFailure.
- **Impacto:** classificação de indisponibilidade temporária inconsistente com o contrato e com a política já aplicada no bloco de gravação. Não foi identificado vazamento de SQL nem corrupção financeira decorrente desse caminho.
- **Limite da evidência:** é uma conclusão da inspeção do fluxo e da estratégia do EF, condicionada a um deadlock nessas consultas. Não foi reproduzido SQL 1205 nesses pontos nesta auditoria e não houve tal falha registrada nos 603 casos aprovados. Os testes novos de falha só interceptam UPDATE ou SavedChanges; não exercitam deadlock no SELECT inicial nem na reconsulta.
- **Correção recomendada, não aplicada durante a auditoria:** estender o alcance do mesmo tratamento restrito de erros SQL conhecidos à operação de cancelamento inteira, incluindo as duas consultas, mantendo a leitura inicial fora da transação de escrita. Preservar rollback/descarte, 404/412, cancelamento de requisição e ausência de retries. Não converter genericamente InvalidOperationException em 503 no handler global. A implementação posterior autorizada está registrada na seção seguinte.

Permanecem como ressalvas operacionais a auditoria NuGet anterior inconclusiva, o resultado de commit potencialmente desconhecido após falha de conexão e a necessidade de provisionamento explicitamente autorizado antes de usar os novos privilégios em banco persistente. A classificação idempotente por mensagem SQL e as limitações de leitura ReadCommitted já existentes não foram alteradas. Publicação e CI da 6B ainda não ocorreram.

## Correção localizada do P2 — 21/11/2026

Após a auditoria acima, o usuário autorizou exclusivamente a correção do alcance do tratamento SQL e sua cobertura. A branch/HEAD permanecem **main@6050035**; as alterações locais anteriores foram preservadas.

### Alteração de produção

`TransferStore.CancelAsync` agora possui um try/catch externo, usando **o mesmo IsTemporarySqlFailure, sem ampliar sua classificação**. O fluxo anterior foi preservado no método privado `CancelWithConcurrencyAsync`. Isso inclui no alcance do filtro a leitura inicial, a escrita, a reconsulta do catch de concorrência e o descarte da transação.

Somente SqlException **1205/-2**, direta ou nos wrappers conhecidos já tratados, produz TransferFailure.Unavailable/HTTP 503. Exceções inesperadas e OperationCanceledException continuam propagando; o handler global não foi alterado. Não há consulta de recuperação ou retry quando um desses erros temporários é reconhecido.

A leitura otimista continua antes da transação ReadCommitted. Permanecem a precedência de 404/412, a cópia de expectedVersion para RowVersion.OriginalValue, o UPDATE real condicionado, o commit antes de 204/ETag, o rollback e a consulta própria/não cancelada após conflito. Nenhum lock de contas foi acrescentado. Não há alteração em criação, idempotência, cálculo de saldo, permissões, schema, migrations, dependências ou contratos HTTP.

### Cobertura específica acrescentada

Novo arquivo: `tests/FinanceFlow.IntegrationTests/TransferCancellationQueryFailureSqlTests.cs`, **dez casos**:

| Casos | Quantidade | Garantia a validar em SQL Server |
|---|---:|---|
| 1205 e -2, na leitura inicial e na reconsulta | 4 | Código SQL real, 503 seguro, estado ativo/saldos/snapshot preservados, sem leituras de recuperação adicionais; cancelamento posterior válido e replay original |
| InvalidOperationException e DbUpdateException não SQL, nas duas consultas | 4 | Falha controlada continua no handler seguro de 500, sem falsa indisponibilidade ou cancelamento parcial |
| OperationCanceledException, nas duas consultas | 2 | A mesma exceção propaga pelo store, sem ser transformada em Unavailable |

Os quatro casos SQL temporários não fabricam SqlException nem usam RAISERROR para simular números. Para 1205, uma conexão independente retém um lock de linha; o teste observa nos DMVs a consulta da API efetivamente bloqueada e com IS na tabela. Em seguida solicita lock X da tabela pela conexão bloqueadora, formando um ciclo real; a prioridade HIGH do bloqueador faz a consulta da API ser a vítima. O UPDATE preparatório é revertido, inclusive sua RowVersion. Há barreiras de início/liberação e prazo limitado, sem sleeps para supor ordenação.

Para -2, WAITFOR no comando e CommandTimeout=1 provocam timeout real do SqlClient. A instrumentação captura CommandFailed e QueryIterationFailed, podendo observar SqlException direta ou o wrapper InvalidOperationException de SQL 1205, sem expor dados internos na resposta. A execução externa posterior demonstrou a forma direta no evento observado. Os testes verificam o número efetivamente recebido do provider e a ausência de informações SQL no Problem Details.

Nos casos de reconsulta, o cancelamento é pausado imediatamente antes do UPDATE; outra conexão avança a versão, provocando um DbUpdateConcurrencyException real. Só depois do rollback/detach o teste permite a consulta de recuperação e induz nela o erro. Asserções conferem contagens de leituras/escritas para detectar retries ou recuperação indevida. Os casos de exceções não SQL e cancelamento usam injeção controlada no caminho de consulta, sem alegar que isso comprova integridade SQL; os casos de deadlock, timeout e disputa de versão dependem do SQL Server real.

Os **603 casos anteriores permanecem**, com dez novos: **613 esperados**, distribuídos em **116 Domain, 115 Application e 382 Integration**. Integration contém 113 casos sem banco e 269 SQL. Os testes anteriores de 204/404/412, cancelamentos simultâneos e rollback não foram modificados.

### Verificações da correção executadas pelo Codex

| Verificação | Resultado real |
|---|---|
| Restore com NuGet.Config | Saída 0; cinco NU1900 por índice inacessível |
| Build Release final | Saída 0; sete projetos, zero erros e nenhum warning C#/analisadores; cinco NU1900 |
| Regressão disponível após a correção | **497 executados: 228 aprovados, 269 falhas na fixture Docker, zero ignorados**, saída 1; aproximadamente 27,7s na última tentativa |
| Application | **115 aprovados**, zero falhas/ignorados |
| Integration | **113 aprovados sem SQL; 269 falhas antes das asserções SQL**, todas com DockerUnavailableException; inclui os dez casos novos bloqueados |
| Domain | **116 não executados**, bloqueados no carregamento da assembly por Smart App Control 0x800711C7, processo -532462766 e uma falha de handshake de módulo |
| Docker | Acesso negado a config.json e pipe docker_engine; nenhuma alteração de proteção ou permissão |
| Script | Sintaxe PowerShell válida; guarda atualizada para 613 executados/aprovados, sem ignorados/falhas/erros do runner |
| Git | git diff --check aprovado; nenhum stage, commit ou push; alterações fora do escopo P2 preservadas por comparação de hashes |

Logs ignorados: `.local/6b-p2-restore.log`, `6b-p2-build-final.log`, `6b-p2-tests.log` (primeira tentativa) e `6b-p2-tests-final.log` (última tentativa). As 269 falhas de fixture não constituem defeitos funcionais comprovados e não aprovam os novos testes. Naquele momento, ainda não havia evidência de **613/613**, de execução dos novos cenários SQL ou de auditoria final aprovada após esta correção. A auditoria NuGet continua inconclusiva pelos NU1900; nenhum aviso foi suprimido.

A alteração desta correção ficou restrita ao store, ao novo arquivo de testes, à contagem esperada do script e à documentação necessária. Os outros arquivos locais da 6B não foram modificados. Não foram executados provisionamento, migrations ou operações em banco persistente, nem iniciada a 6C.

## Validação externa do P2 e ajuste da asserção — 21/11/2026

O usuário executou a suíte em Docker Linux com SQL Server/Testcontainers. Os três XMLs e os códigos de saída em `.local/6b-docker-results/20261121-005532-270-9f8c53` foram conferidos pelo Codex; esta não foi uma execução SQL no sandbox.

| Projeto | Executados | Aprovados | Falhas | Ignorados | Erros do runner |
|---|---:|---:|---:|---:|---:|
| Domain | 116 | 116 | 0 | 0 | 0 |
| Application | 115 | 115 | 0 | 0 | 0 |
| Integration | 382 | 380 | 2 | 0 | 0 |
| Total | 613 | 611 | 2 | 0 | 0 |

Restore e build externos tiveram saída 0; Docker e Integration tiveram saída 1. As únicas falhas ocorreram em `TransferCancellationQueryFailureSqlTests.Real_query_deadlocks_and_timeouts_are_503_without_cancellation_or_extra_recovery_reads`, para `(recovery: false, sqlNumber: 1205)` e `(recovery: true, sqlNumber: 1205)`.

**Causa comprovada:** a antiga linha 68 exigia exatamente InvalidOperationException em QueryIterationFailed, mas os dois XMLs registraram SqlException direta. A asserção anterior `Assert.Equal(sqlNumber, fault.SqlError)` já havia passado, comprovando o código 1205. O teste provoca um ciclo real de locks com conexões independentes e observa o bloqueio no SQL Server antes de fechar o ciclo; não fabrica SqlException. O evento de diagnóstico não garante que o erro já esteja encapsulado pela estratégia do EF Core. Não foi identificado defeito adicional no filtro do store, que reconhece as duas formas.

**Correção restrita ao teste:** se a exceção observada for InvalidOperationException, examinar sua InnerException; em seguida exigir exatamente SqlException com Number=1205. Assim, somente SqlException direta ou InvalidOperationException contendo diretamente SqlException 1205 são aceitas; outros tipos e números continuam falhando. Nenhum código de produção, mecanismo financeiro, migration, grant, fingerprint ou script foi alterado nesta correção. A contagem permanece **613**.

Foram preservadas todas as asserções posteriores: HTTP 503, Problem Details sanitizado, no-store, contagens de leituras/escritas, ausência de recuperação adicional, conflito real na reconsulta, estado não cancelado, saldos exatos, snapshot original e novo cancelamento após a falha. Essas verificações **não foram alcançadas nos dois casos que falharam na antiga linha 68**; naquele momento, sua aprovação após o ajuste dependia de nova execução externa. Os outros 611 casos passaram naquela execução, incluindo timeout -2 nas duas consultas, exceções inesperadas e OperationCanceledException.

### Verificações do ajuste executadas pelo Codex

- `dotnet build FinanceFlow.slnx --configuration Release --no-restore`: saída 0, sete projetos, zero erros e cinco NU1900. Auditoria NuGet inconclusiva; nenhum aviso suprimido.
- `dotnet test FinanceFlow.slnx --configuration Release --no-restore`: **115 testes de Application aprovados, zero falhas e zero ignorados**. Domain (116) e Integration (382) não executaram: Smart App Control bloqueou ambas as assemblies com FileLoadException/0x800711C7, duas falhas de handshake e saída -532462766. Não houve aprovação dos testes SQL após este ajuste.
- Docker continua inacessível no sandbox: acesso negado a config.json e ao pipe docker_engine; nenhuma proteção foi alterada.
- Logs locais ignorados: `.local/6b-p2-assertion-build.log` e `.local/6b-p2-assertion-tests.log`. Logs/XMLs anteriores preservados.
- `git diff --check` aprovado; nenhum stage, commit ou push. Somente este documento e a asserção do arquivo de testes foram alterados nesta intervenção; demais alterações locais da 6B preservadas.

## Validação externa final e auditoria de encerramento — 21/11/2026

**Parecer: APROVADA PARA COMMIT**, sem novo defeito bloqueante identificado. O usuário executou `scripts/Test-DockerSuite.ps1` no PowerShell com Docker Linux e SQL Server/Testcontainers. O Codex leu os resultados existentes; não executou novamente a suíte SQL durante esta auditoria.

Evidências: `.local/6b-docker-results/20261121-020304-418-94cdd6`, com log da execução, três XMLs e códigos de saída individuais. Log acumulado preservado em `.local/6b-docker-tests.log`.

| Projeto | Executados | Aprovados | Falhas | Ignorados | Erros do runner |
|---|---:|---:|---:|---:|---:|
| Domain | 116 | 116 | 0 | 0 | 0 |
| Application | 115 | 115 | 0 | 0 | 0 |
| Integration | 382 | 382 | 0 | 0 | 0 |
| Total | 613 | 613 | 0 | 0 | 0 |

Os totais das assemblies conferem com os **613 nós de testes**. Restore, build Release, Domain, Application, Integration e Docker possuem arquivos de exit code com valor **0**. A comparação dos nomes com a execução anterior de 603 casos confirmou **nenhum caso ausente e dez casos novos**.

### Encerramento do P2 e confiabilidade dos testes

Os XMLs finais registram `Pass` nos dois casos de `TransferCancellationQueryFailureSqlTests.Real_query_deadlocks_and_timeouts_are_503_without_cancellation_or_extra_recovery_reads` anteriormente falhos: `(recovery: false, sqlNumber: 1205)` e `(recovery: true, sqlNumber: 1205)`. Como não há retorno antecipado ou supressão de asserções, o sucesso confirma também as verificações posteriores à exceção: 503 seguro, número SQL exato, ausência de leituras/escritas extras, estado financeiro preservado, saldos simétricos, snapshot intacto e cancelamento posterior válido.

Os outros oito casos do P2 também constam como `Pass`: timeout **-2** nas duas consultas, quatro casos de exceções inesperadas com HTTP 500 seguro e dois de OperationCanceledException propagada como a mesma instância, sem conversão em indisponibilidade.

A revisão confirmou que o try externo de `TransferStore.CancelAsync` abrange leitura inicial, gravação, rollback/descarte e reconsulta após DbUpdateConcurrencyException. O filtro considera somente SqlException **1205/-2**, direta ou nos wrappers conhecidos de InvalidOperationException/DbUpdateException; não depende de mensagens textuais. Erros inesperados e cancelamento da requisição continuam propagando. Não há nova consulta de recuperação nem retry após reconhecimento de SQL temporário.

A leitura inicial permanece fora da transação. A escrita usa transação curta ReadCommitted, aplica expectedVersion como OriginalValue de RowVersion e confirma antes de retornar sucesso. O conflito provoca rollback e detach antes da consulta por proprietário/estado: ativo retorna 412; inexistente ou cancelado retorna 404. O cancelamento válido permanece 204 vazio com nova ETag/no-store.

Os testes usam DbContexts/conexões independentes, barreiras e observação real de locks no SQL Server. O deadlock é provocado por ciclo real de locks; timeout usa CommandTimeout com espera SQL controlada. Não há sleeps para presumir ordenação, skips, fallback para outro banco, exceções SQL fabricadas ou asserções genéricas substituindo o número esperado. A injeção de exceções não SQL verifica a classificação e a propagação, sem ser apresentada como prova de integridade SQL.

### Integridade da entrega e Git

- Cancelamento único na linha original, timestamps UTC do relógio injetado, campos financeiros preservados, sem reativação, exclusão física ou movimentações compensatórias. Contas inativas e saldos negativos continuam permitidos.
- Saldos atuais/históricos excluem IsCanceled, mantendo os efeitos simétricos e o patrimônio; cortes anteriores à transferência não mudam. Não houve alteração da fórmula nem criação de saldo persistido.
- JWT, revogação, isolamento por proprietário, If-Match forte e respostas 400/404/412/428 permanecem coerentes com os endpoints anteriores. OpenAPI documenta somente o novo DELETE, sem corpo ou Idempotency-Key.
- Namespace `transfers.create.v1` e snapshots originais preservados. Replay após cancelamento mantém status/corpo/Location/ETag/Content-Type, sem reativar, duplicar ou restaurar efeitos financeiros.
- UPDATE em Transfers limitado a **IsCanceled, CanceledAtUtc e UpdatedAtUtc**, mantendo SELECT/INSERT anteriores. O verificador rejeita concessões excessivas; testes usam a conta restrita e comprovam negação de UPDATE financeiro, DELETE físico e DDL.
- **Nenhuma migration, snapshot, mapeamento, dependência, workflow ou configuração de autenticação alterada.** Schema de 6A atende à 6B; nenhuma operação em banco persistente foi executada.
- Escopo contra **main@6050035**: **22 arquivos, 14 modificados e oito novos**. Nenhum arquivo inesperado, segredo identificado ou artefato temporário visível ao Git; logs e XMLs continuam ignorados em `.local`.
- Esta auditoria alterou **somente README.md, docs/etapa-6/README.md e este documento**. Os outros 19 arquivos locais foram preservados por comparação de hashes. `git diff --check` aprovado, staging vazio, sem stage/commit/push. Nenhuma funcionalidade da 6C foi iniciada.

A validação externa final fecha a pendência funcional do P2. Permanecem a auditoria NuGet conclusiva, a publicação mediante autorização e uma execução aprovada da CI da 6B. Os NU1900 e os bloqueios históricos do sandbox não foram apagados nem convertidos em aprovações presumidas.

## Reprodução externa e pendências

Pré-requisitos: PowerShell, Docker Desktop/Engine Linux acessível ao usuário, socket/mounts já usados na 6A, recursos para SQL Server 2022 x64 e acesso às imagens/NuGet. SDK e testes executam em Linux; não se carrega a assembly bloqueada no Windows. Não carregar .env, não usar o banco persistente e não executar migrations/provisionamento desse ambiente para rodar a suíte.

```powershell
& C:\FinanceFlow\scripts\Test-DockerSuite.ps1
```

O script preserva o código real do Docker, logs completos de stdout/stderr em `.local/6b-docker-tests.log`, pasta única `.local/6b-docker-results/<execucao>`, XMLs e códigos individuais de restore/build/testes. Após a correção P2, exige três relatórios e **613 executados/aprovados**, zero falhas/ignorados/erros de runner. O console apresenta somente resumo, falhas e stack traces. Os registros históricos de 603/603 e os logs de 6A não são apagados. Falhas ou descoberta incompleta nunca viram sucesso; a CI existente permanece em Microsoft.Testing.Platform, sem alteração.

Comandos padrão, quando SDK/segurança/Docker permitirem execução nativa:

```powershell
dotnet restore FinanceFlow.slnx --configfile NuGet.Config
dotnet build FinanceFlow.slnx --configuration Release --no-restore
dotnet test FinanceFlow.slnx --configuration Release --no-restore
dotnet list FinanceFlow.slnx package --vulnerable --include-transitive
git diff --check
```

A regressão externa anterior **603/603** permanece comprovada e histórica; a execução final **613/613** e a revisão da correção P2 estão agora conferidas, com **6B aprovada para commit**. Permanecem a auditoria NuGet conclusiva e, após autorização, commit/push/publicação e aprovação da CI da 6B. Nenhuma validação bloqueada no sandbox foi substituída por mock ou aprovação presumida. A 6C não foi iniciada e a Etapa 6 inteira não está concluída.

**Sem stage, commit, push, migrations/provisionamento persistentes, alteração de segredos/credenciais, volumes ou proteções do Windows. Entrega 6C não iniciada.**
