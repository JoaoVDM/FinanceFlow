# Entrega 5B — Edição e exclusão lógica de transações

## Estado e origem das evidências

Workspace confirmado em C:\FinanceFlow, branch main e Git inicialmente limpo no commit **f9e7d12 — feat: implement transactions and historical balances**. O usuário informou publicação de 5A, 386 testes locais aprovados sem falhas/ignorados e workflow #7 aprovado. São evidências externas da baseline, não execução do Codex nem aprovação de 5B.

**Implementada localmente, com regressão externa e revisão técnica final aprovadas.** O usuário confirmou 461 testes aprovados, zero falhas e zero ignorados no PowerShell com SQL Server/Testcontainers, em aproximadamente 1m39s. Essa execução é externa, não realizada pelo Codex. Não há publicação ou CI aprovada de 5B; a auditoria NuGet permanece inconclusiva devido aos NU1900. SDK utilizado pelo Codex: 10.0.404, Windows; revisão em 19/11/2026. As limitações históricas do sandbox estão preservadas abaixo.

## Contratos HTTP

| Método | Rota | Entrada | Sucesso |
|---|---|---|---|
| PUT | /api/v1/transactions/{id} | accountId, categoryId, kind, amount, occurredOn e description opcional | 204 vazio, novo ETag |
| DELETE | /api/v1/transactions/{id} | Sem corpo | 204 vazio, novo ETag |

JWT válido e If-Match são obrigatórios. O componente existente aceita somente um ETag forte, Base64 canônico de oito bytes, entre aspas. Não aceita wildcard, lista, ETag fraco, formato inválido ou header vazio. Ausência: 428; formato inválido: 400. Corpos inválidos/desconhecidos, campos gerenciados e tipos errados: 400. DELETE rejeita corpo, usando a detecção de corpo do ASP.NET Core; não aceita tentativas de atribuir campos por JSON.

Erros preservam Problem Details: 401 sem JWT; 404 para recurso alheio/inexistente/excluído e referências alheias/inexistentes; 409 para vínculos inativos quando a mudança é proibida; 412 para versão antiga em recurso vivo; 429/Retry-After e 503 para indisponibilidade conhecida. Entrada inválida, tipo de categoria incompatível, centavos ou data inválidos retornam 400. Todas as respostas das rotas financeiras continuam com no-store, inclusive antes da action. OpenAPI/Bearer e cabeçalhos foram atualizados; Swagger/OpenAPI continuam somente em Development. Não há Idempotency-Key em PUT/DELETE.

Autenticação e validação automática do corpo precedem a leitura do If-Match. Depois do header válido, recurso excluído/alheio/inexistente retorna 404 antes de comparar versões. Em recurso próprio vivo, uma versão já desatualizada retorna 412 antes de validar vínculos. Isso não substitui a condição SQL contra alterações ocorridas depois da leitura.

## Domínio e política de atividade

TransactionDetails representa os campos editáveis; ValidateDetails compartilha as mesmas regras escalares da criação. Não altera a normalização, o fingerprint ou o processamento de POST. FinancialTransaction.UpdateDetails valida tudo antes de atribuir campos; Delete impede repetição e preserva os dados financeiros. CreatedAtUtc e UserId são protegidos também pelo save behavior do EF; o SQL não concede UPDATE nas colunas imutáveis.

| Mudança | Conta original/destino | Categoria |
|---|---|---|
| Só descrição, inclusive description=null | Podem estar inativas | Vínculo histórico inalterado pode estar inativo |
| PUT com os mesmos valores | Podem estar inativas; mantém precondição SQL | Vínculo histórico inalterado pode estar inativo |
| Valor/data, conta, categoria ou Kind | Original e destino devem estar ativas | Novo vínculo deve estar ativo e compatível |
| Categoria histórica inalterada em ajuste de valor/data | Conta deve estar ativa | Não exige reativar a categoria mantida |
| Exclusão lógica | Atividade não é consultada | Atividade não é consultada |

A interpretação de conta inativa segue o modelo aprovado: somente correção descritiva; por isso mover para conta ativa não permite contornar a inatividade da origem. Categoria histórica inalterada permanece válida; atividade é exigida quando CategoryId muda. Kind só muda com categoria compatível. Não há alteração de proprietário ou de campos gerenciados por JSON.

Amount permanece decimal positivo até 999999999999.99, com centavos exatos. O PUT reutiliza o conversor que valida o número JSON original antes do parsing, sem double/float ou arredondamento silencioso. Description aceita null e até 500 unidades UTF-16, sem normalização adicional. OccurredOn fica entre a abertura do destino e hoje em America/Sao_Paulo. TimeProvider é capturado uma vez por operação; timestamps técnicos são normalizados em UTC no domínio.

## Concorrência, transações e rollback

O store carrega a transação por Id/UserId e !IsDeleted sob o isolamento normal, antes de iniciar RepeatableRead. Isso evita reter shared locks na linha editada até duas operações tentarem convertê-los em locks exclusivos, o que produziria deadlocks em vez de concorrência otimista.

No PUT, original/destino são lidas em ordem por Id dentro de RepeatableRead, seguidas da categoria própria de destino. Shared locks protegem os estados até o commit. Validação de domínio e UPDATE ocorrem nessa unidade curta. Se a desativação confirmar antes da leitura, a mudança é rejeitada quando exige atividade; se a edição adquirir os locks primeiro, a desativação aguarda seu commit. Não há bloqueio global, transação externa longa ou DbContext compartilhado entre requisições.

A versão enviada é copiada para RowVersion.OriginalValue antes de SaveChanges: participa do WHERE do UPDATE real. UpdatedAtUtc é sempre marcado como modificado, mesmo em PUT idêntico e relógio fixo; toda alteração aceita executa a precondição e gera nova RowVersion. A comparação antecipada só define precedência de erro. Não há retry com versão atual.

DELETE tem uma transação local ReadCommitted, sem consultar atividade nem bloquear referências para validação. Marca IsDeleted=true e DeletedAtUtc/UpdatedAtUtc com o mesmo instante UTC. Não executa DELETE SQL nem cria outro lançamento.

Em DbUpdateConcurrencyException, a transação é revertida antes de uma consulta nova por proprietário e não excluído; a entidade com falha é destacada. Retorna 412 se o recurso ainda estiver vivo, ou 404 se tiver sido excluído/não estiver mais no escopo. Falhas inesperadas e retornos antecipados descartam a transação, revertendo alterações; deadlock/timeout conhecido retorna indisponibilidade sem sucesso parcial. Se um resultado de commit for desconhecido após falha de conexão, consultar o estado atual; repetir com If-Match antigo não pode sobrescrever dados silenciosamente.

### Política de exclusão repetida

Depois da primeira exclusão, qualquer PUT/DELETE com header bem formado retorna **404**, inclusive com a versão antiga ou com o ETag do 204 de exclusão. Se uma edição vencer e a transação continuar viva, um DELETE concorrente com versão antiga retorna **412**. Dois DELETEs da mesma versão produzem um 204 e um 404. A exclusão não se repete e seu timestamp não é atualizado novamente. Não há restauração.

## Saldos e snapshot de criação

As consultas de 5A não foram alteradas: agregam no SQL os valores atualmente válidos e ignoram IsDeleted. PUT recalcula os efeitos de valor/sinal/conta/data por substituir a linha existente, sem duplicar valores. DELETE remove o efeito de todos os cortes históricos aplicáveis. Contas inativas permanecem nos saldos; não há CurrentBalance persistido ou materialização de todo o histórico.

POST /transactions e as operações em IdempotencyRecords permanecem intactos. Replay conserva 201, corpo, Location e ETag originais após edição/exclusão, independentemente da representação atual. PUT/DELETE não gravam snapshots nem exigem chave idempotente.

## SQL, migration e privilégios

**Nenhuma migration necessária ou criada.** Todos os campos, RowVersion e constraints já existem em 5A. As quatro migrations históricas, seus targets e o snapshot não foram modificados. HasPendingModelChanges=false foi comprovado sem banco. As proteções AfterSaveBehavior.Throw de UserId/CreatedAtUtc são metadados de persistência, sem mudança estrutural.

FinancialTransactions mantém SELECT/INSERT e recebe UPDATE apenas em:

```text
AccountId, CategoryId, Kind, Amount, OccurredOn, Description,
UpdatedAtUtc, IsDeleted, DeletedAtUtc
```

Não recebe UPDATE em Id/UserId/CreatedAtUtc/RowVersion, UPDATE de tabela, DELETE físico, ALTER/DDL, delegação ou permissões administrativas. IdempotencyRecords continua somente SELECT/INSERT. Os grants de Identity, Accounts e Categories permanecem iguais. O verificador aceita somente as nove colunas novas autorizadas e rejeita concessões extras/with grant option.

Quando houver autorização para utilizar 5B em banco persistente, será necessário executar somente o provisionamento atualizado pelos procedimentos existentes, com credenciais administrativas separadas. Readiness valida schema/leitura e não substitui a verificação desses privilégios de escrita. Nada foi provisionado ou migrado no banco persistente nesta implementação.

## Histórico de testes e resultados executados pelo Codex

Mantidos os 386 casos da baseline e acrescentados **75**: 16 Domain, 14 Application, nove contratos/modelo sem SQL e 36 SQL Server. Nenhum teste removido, ignorado ou substituído por SQLite/InMemory.

| Verificação do Codex | Resultado comprovado |
|---|---|
| Git inicial | main limpa em f9e7d12 |
| Restore com NuGet.Config | Saída 0; cinco NU1900 por índice indisponível |
| Build Release | Sete projetos, zero erros, sem warnings C#/analisadores; cinco NU1900 |
| Domain.Tests | 91 aprovados; 16 casos novos também executados isoladamente |
| Application.Tests | 92 aprovados; 14 casos novos também executados isoladamente |
| IntegrationTests | 102 aprovados sem SQL; 176 falhas na fixture Docker |
| Contratos novos isolados | Nove aprovados, zero falhas/ignorados |
| Suíte completa | **461 casos: 285 aprovados, 176 falhas, zero ignorados**, saída 2 |
| Auditoria com transitivos | Saída 1; índice api.nuget.org inacessível, socket api.nuget.org:443 proibido pelo ambiente; inconclusiva |
| Revisão Git | 22 arquivos no escopo: 15 modificados e sete novos; git diff --check aprovado; nenhuma alteração em migrations/snapshot, dependências ou índice staged |

Docker retornou acesso negado a C:\Users\jvito\.docker\config.json e ao pipe docker_engine. Os 176 casos SQL (140 anteriores e 36 novos) não atingiram as asserções, por DockerUnavailableException. Isso não comprova atualização das permissões, timeout, rollback ou concorrência no SQL Server. As proteções do sandbox não foram alteradas. Os avisos ambientais de Data Protection do perfil de teste continuam sem alteração de chaves/configurações.

Casos novos cobrem receita/despesa, valor/Kind/conta/categoria/data/descrição, nulidade e limites, correção em vínculos inativos, novos vínculos inativos/alheios, abertura do destino, corpo incompleto/desconhecido, If-Match, exclusão preservando dados, saldos históricos e replay. Testes de concorrência usam requisições/DbContexts independentes, barreiras explícitas, inspeção do predicado SQL e shared locks em sys.dm_tran_locks. A leitura inicial da transação é verificada fora de transação explícita; a validação dos vínculos é verificada em RepeatableRead.

Falhas são induzidas depois de SaveChanges, antes do commit, para comprovar rollback de dados/versão/saldos. O teste de timeout usa WAITFOR no banco descartável acima do timeout do comando, para provocar erro real do provider; não é um sleep para inferir concorrência. Permissões efetivas, campos imutáveis, DELETE/DDL, grants extras e delegação também são testados em bancos descartáveis.

Expectativas anteriores legitimamente atualizadas: OpenAPI inclui PUT/DELETE na rota individual; o teste SQL que antes negava UPDATE em Amount passou a negar CreatedAtUtc (imutável, erro 230) e mantém DELETE 229. A verificação de UPDATE amplo no objeto permanece negada. Os doubles antigos apenas implementam os novos métodos da porta.

Logs ignorados: `.local/5b-restore.log`, `.local/5b-build.log`, `.local/5b-tests.log`, `.local/5b-domain.log`, `.local/5b-application.log`, `.local/5b-contracts.log` e `.local/5b-audit.log`. Não foram publicados. A auditoria é inconclusiva por bloqueio de rede; não se declarou ausência de vulnerabilidades nem se desabilitou a consulta. A revisão final não encontrou arquivos sensíveis/temporários visíveis ao Git ou indicadores de segredos no conteúdo adicionado.

## Validação externa concluída e auditoria técnica final

Na solicitação de auditoria final, o usuário confirmou a execução da suíte completa no PowerShell, com Docker/SQL Server/Testcontainers disponíveis:

| Evidência externa fornecida pelo usuário | Resultado |
|---|---|
| Suíte completa, incluindo integração SQL Server/Testcontainers | **461 aprovados, 0 falhas, 0 ignorados** |
| Duração aproximada | 1m39s |
| Consulta de vulnerabilidades | **Inconclusiva**, com avisos NU1900 |

Esse resultado encerra a pendência de regressão SQL de 5B. Não foi executado pelo Codex e não substitui nem apaga as 176 falhas históricas de inicialização da fixture no sandbox. A aprovação dos testes não comprova ausência de vulnerabilidades; não há evidência de uma auditoria NuGet conclusiva ou de desaparecimento dos avisos. Também não se infere aplicação de migrations/provisionamento em banco persistente, publicação ou aprovação de CI de 5B.

**Revisão técnica estática de 19/11/2026: APROVADO, sem problemas bloqueantes identificados.** Foram conferidos todos os 22 arquivos locais contra `f9e7d12`: 15 modificados e sete novos ainda não rastreados. A revisão abrangeu autenticação/isolamento, validações financeiras e HTTP, predicado SQL com RowVersion, atividade protegida por RepeatableRead, rollback e tratamento de falhas, exclusão lógica, saldos atuais/históricos, snapshots de criação, grants por coluna, OpenAPI e testes. Não foram encontradas migrations, dependências, funcionalidades futuras ou arquivos sensíveis indevidos no conjunto de alterações.

Nesta auditoria, somente README.md, docs/etapa-5/README.md e este documento foram atualizados. Código, testes, migrations e dependências foram preservados; a suíte não foi executada novamente. A revisão estática não comprova disponibilidade em produção, resultados de uma futura CI ou ausência de vulnerabilidades em dependências. Permanecem as limitações operacionais já descritas: em falha de conexão com resultado de commit desconhecido, consultar o estado atual antes de nova tentativa; alterações em banco persistente dependem de autorização específica.

## Comandos para reprodução da validação

Pré-requisitos: SDK .NET 10.0.4xx, Docker Desktop/Engine Linux acessível, recursos para SQL Server 2022 x64 e acesso ao NuGet/MCR. As fixtures existentes geram credenciais/chaves e bancos descartáveis; não carregar .env, segredos persistentes ou scripts de provisionamento do ambiente local para rodar os testes.

```powershell
Set-Location C:\FinanceFlow
$ErrorActionPreference = 'Stop'
function Assert-5BStep([string]$Name) {
    if ($LASTEXITCODE -ne 0) { throw "$Name falhou; preserve a saída." }
}
docker info --format '{{.ServerVersion}}'
Assert-5BStep 'Docker'
dotnet restore FinanceFlow.slnx --configfile NuGet.Config
Assert-5BStep 'Restore'
dotnet build FinanceFlow.slnx --configuration Release --no-restore
Assert-5BStep 'Build'
dotnet test FinanceFlow.slnx --configuration Release --no-restore
Assert-5BStep 'Regressão SQL e completa'
dotnet list FinanceFlow.slnx package --vulnerable --include-transitive
Assert-5BStep 'Auditoria'
git diff --check
Assert-5BStep 'Diff'
```

Esperado: **461 aprovados, zero falhas e zero ignorados**. Se houver NU1900, a auditoria continua inconclusiva, mesmo sem vulnerabilidades listadas. Não ampliar grants, suprimir avisos ou ignorar testes para obter aprovação. Não é necessário aplicar migrations/provisionamento em banco persistente para validar os bancos descartáveis.

## Arquivos e pendências

Novos: FinancialTransactionChangesTests (Domain); FinancialTransactionChangesServiceTests (Application); TransactionMutationContractTests, TransactionMutationSqlTests, TransactionMutationConcurrencySqlTests e TransactionMutationPermissionsSqlTests (Integration); este documento.

Modificados: FinancialTransaction; TransactionContracts; FinancialTransactionsService; FinancialTransactionStore; FinancialTransactionConfiguration; DatabaseTasks; TransactionRequests; TransactionsController; ApiDocumentationConfiguration; FinancialTransactionsServiceTests; TransactionInputAndModelTests; TransactionsPersistenceSqlTests; README; docs/etapa-5/README; nota posterior em entrega-5a-validacao. Nenhuma dependência foi adicionada.

Pendências: concluir a consulta de vulnerabilidades com índice acessível e, após autorização de publicação, obter uma execução da CI de 5B. A regressão externa dos 461 testes e a revisão técnica estão aprovadas; publicação e CI de 5B continuam pendentes. Nenhuma alteração foi aplicada pelo Codex a banco persistente. As evidências publicadas de 5A não são atribuídas a este código.

Referências técnicas consultadas: [concorrência EF Core](https://learn.microsoft.com/en-us/ef/core/saving/concurrency), [isolamento SQL Server](https://learn.microsoft.com/en-us/sql/t-sql/statements/set-transaction-isolation-level-transact-sql) e [rollback/limpeza da transação no EF Core](https://github.com/dotnet/efcore/blob/v10.0.0/src/EFCore.Relational/Storage/RelationalTransaction.cs).

**Sem stage, commit, push, alterações em credenciais/certificados/volumes ou no sandbox. Entrega 5C e Etapa 6 não iniciadas.**
