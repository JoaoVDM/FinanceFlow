# Etapa 4 — Entrega C: edição, atividade e concorrência

## Estado e baseline

**Implementada e validada externamente pelo usuário: 311 testes aprovados, 0 falhas e 0 ignorados**, no PowerShell com Docker/SQL Server/Testcontainers, após a correção das sete falhas iniciais. A revisão estática final dos 34 arquivos foi aprovada, sem problemas bloqueantes. A consulta NuGet externa apresentou os sete projetos sem pacotes vulneráveis identificados, mas com avisos NU1900 por indisponibilidade do índice de vulnerabilidades; não é uma auditoria de segurança conclusiva. O histórico de bloqueio do Docker e do NuGet no sandbox permanece abaixo, separado das evidências externas. Não há publicação nem nova CI aprovada da Entrega C.

Workspace confirmado em C:\FinanceFlow, `main` limpa no commit **64b32d3 — feat: implement financial categories**. O usuário informou 216 testes aprovados na Entrega B e GitHub Actions **43642389840 — Success**. São evidências externas da baseline publicada, não execuções do Codex nem aprovação do código de C. O [histórico de B](entrega-b-validacao.md) preserva as validações e registra a publicação posterior.

Foram consultados AGENTS.md, roadmap, contratos, modelo, critérios de aceitação, ADR-003/007 e históricos de A/B. A implementação preserva o monólito modular e não altera ADRs. Etapa 5 não foi iniciada.

## Contratos entregues

| Método | Rota | Entrada | Sucesso |
|---|---|---|---|
| PUT | /api/v1/accounts/{id} | name, type | 204, novo ETag, sem corpo |
| PATCH | /api/v1/accounts/{id} | isActive | 204, novo ETag, sem corpo |
| PUT | /api/v1/categories/{id} | name | 204, novo ETag, sem corpo |
| PATCH | /api/v1/categories/{id} | isActive | 204, novo ETag, sem corpo |

JWT e If-Match são obrigatórios nos quatro endpoints. Não exigem Idempotency-Key. Respostas, inclusive erros anteriores à execução da action, recebem Cache-Control: no-store pelo pipeline HTTP. OpenAPI documenta Bearer, corpos, If-Match, respostas e ETag; Swagger/OpenAPI continuam somente em Development.

Erros: entrada/header malformado 400; sem JWT 401 com WWW-Authenticate; recurso alheio/inexistente 404 genérico; nome de categoria duplicado 409; versão antiga 412 (`precondition_failed`); ausência de If-Match 428 (`precondition_required`); limite 429/Retry-After; indisponibilidade conhecida 503. Erros inesperados permanecem sanitizados, sem ampliar a classificação de falhas SQL.

Autenticação e validação automática do corpo precedem a leitura do header na action. Para corpo válido, o header é validado antes da busca do recurso. A mesma sequência vale para recurso inexistente e alheio, sem consulta global por Id para inferir propriedade.

DTOs de edição são específicos. Campos desconhecidos/controlados pelo servidor e tipos JSON incorretos são rejeitados. isActive é bool? com Required: ausência/null não viram false. PUT de conta aceita apenas Name/Type; PUT de categoria somente Name, sem Kind. Limite de requisição de 16 KiB preservado.

## Regras e organização

- FinancialAccount.UpdateDetails valida nome com a política já existente (limite bruto de 100 e Trim/NFC com limite de 100) e tipo fechado checking/savings/cash/other. SetActivity altera somente atividade e timestamp. Conta inativa pode ser editada e desativação aceita saldo inicial não zero.
- Category.Rename preserva Name Trim/NFC e NormalizedName Trim/NFC/ToUpperInvariant/NFC, ambos até 100 unidades UTF-16 após transformação. Acentos são significativos. SetActivity não muda nome/tipo. A reserva do nome inclui inativas; Kind e proprietário nunca mudam.
- Normalizadores privados reutilizam as validações de criação sem alterar sua política ou representação persistida.
- UpdatedAtUtc recebe o TimeProvider em UTC; CreatedAtUtc e os campos financeiros de abertura não são modificados.
- Application obtém ICurrentUser, exige versão de oito bytes, carrega entidade por porta específica, verifica proprietário, aplica o método de domínio e solicita gravação. Não conhece EF ou SQL.
- FindForUpdateAsync/SaveUpdateAsync são portas específicas nos stores existentes. As entidades são carregadas no mesmo DbContext Scoped da gravação; tentativa interna de salvar entidade desconectada falha de modo seguro.
- Controllers cuidam de DTO/header/HTTP; não acessam DbContext ou executam regras financeiras. Não foram adicionados pacotes, novas camadas, repositório genérico ou mediador.

## If-Match e precondição real

IfMatchHeader concentra a leitura HTTP: exatamente um validador forte, entre aspas, Base64 canônico de oito bytes. Exemplo: `If-Match: "AAAAAAAAB9E="`. O formato emitido tem 14 caracteres, incluindo as aspas; não há trim que transforme uma chave malformada em válida. Ausência retorna 428. Vazio, wildcard, W/, listas/múltiplos headers, Base64 inválido/não canônico e comprimentos diferentes retornam 400. Não há interpretação numérica ou temporal do token.

Na Infrastructure, a versão recebida é copiada para **RowVersion.OriginalValue**, que entra no WHERE do UPDATE gerado pelo EF Core junto ao Id. Não é apenas comparada à versão lida em memória. Busca inicial e checagem posterior usam Id + UserId, e UserId não pode ser atualizado pela credencial da API.

Um SaveChanges persiste campos editáveis e UpdatedAtUtc atomicamente e recupera a rowversion gerada pelo SQL Server. DbUpdateConcurrencyException resulta em 412 se o recurso ainda pertence ao usuário, ou 404 se já não é encontrado nesse escopo. Não há retry com versão atual. A entidade com falha é removida do tracking antes dessa checagem; não se grava novamente.

Renomeação duplicada de categoria usa o reconhecimento restrito existente de erro 2601, entidade EF Category e prefixo exato do objeto/índice. Outras violações não viram 409. Uma falha não confirma nome, nome normalizado ou timestamp parcialmente.

### Comandos sem mudança de valores

**Toda alteração aceita executa UPDATE e avança RowVersion**, mesmo que nome/tipo/atividade coincidam com os atuais. UpdatedAtUtc é sempre marcado como modificado no EF, inclusive se um relógio de teste fixo produzir o mesmo timestamp. Isso garante a execução do predicado SQL e rejeita versão antiga também em um comando sem mudança de valores.

O custo de um UPDATE adicional foi preferido a um caminho especial de ausência de mudança que exigiria outra forma de conferir a versão atomicamente. O 204 sempre fornece o novo ETag para a próxima alteração. Não se promete que o timestamp será estritamente maior sob relógio fixo/ajustado; a versão SQL avança independentemente disso.

## Privilégios e schema

SELECT/INSERT existentes são mantidos. A API recebe apenas estes UPDATEs por coluna:

| Objeto | Colunas permitidas |
|---|---|
| FinancialAccounts | Name, Type, IsActive, UpdatedAtUtc |
| Categories | Name, NormalizedName, IsActive, UpdatedAtUtc |

Não há UPDATE amplo financeiro, UPDATE em IdempotencyRecords, DELETE, ALTER/DDL, roles administrativas ou alteração de permissões de Identity. Id, UserId, abertura, moeda, Kind, CreatedAtUtc e RowVersion não recebem UPDATE da API. A rowversion continua sendo gerada pelo servidor.

DatabaseTasks concede os direitos com `GRANT UPDATE ON OBJECT::dbo.Table (Columns) TO ...`. O verificador mantém as regras anteriores e aceita UPDATE apenas em entradas de coluna (`minor_id > 0`) dos dois objetos e dos nomes permitidos. Concessões de tabela, colunas extras e WITH GRANT OPTION continuam sendo rejeitados; não há revogação silenciosa ou redefinição de credenciais.

Referências oficiais consultadas: [GRANT por objeto/coluna](https://learn.microsoft.com/en-us/sql/t-sql/statements/grant-object-permissions-transact-sql) e [sys.database_permissions e minor_id](https://learn.microsoft.com/en-us/sql/relational-databases/system-catalog-views/sys-database-permissions-transact-sql).

**Nenhuma migration foi necessária**: RowVersion, IsActive e UpdatedAtUtc já existem. Configurações EF, schema, snapshot e todos os targets/migrations históricos permaneceram intactos; HasPendingModelChanges continua false nos testes sem SQL. Nenhuma mudança foi aplicada ao banco persistente. Os novos grants precisam de provisionamento explícito quando autorizado para esse ambiente; readiness existente verifica schema/leitura e não substitui a validação dos novos direitos de UPDATE.

## Idempotência e compatibilidade

POST /accounts continua exigindo chave e comparando o hash canônico de criação. Nenhuma alteração foi feita em sua transação, reconhecimento de colisões ou snapshot. Edição/atividade não carregam nem atualizam IdempotencyRecords; replay permanece 201 com corpo, Location e ETag originais, mesmo com representação atual diferente.

Não há idempotência em categorias ou PUT/PATCH. Não há exclusão, troca de proprietário/tipo de categoria, saldos, lançamentos, transferências ou relatórios.

## Testes e validações executadas pelo Codex

SDK **10.0.404**, sandbox Windows. Acrescentados **95 casos** aos 216 existentes: 20 de Domain, 19 de Application, 20 de contrato HTTP/header sem SQL e 36 com SQL Server real. Total final **311**.

| Validação | Resultado real |
|---|---|
| Git inicial | main limpa em 64b32d3 |
| Restore solicitado | Saída 0; cache disponível; cinco NU1900 por consulta NuGet indisponível |
| Build inicial antes do novo restore | Sete projetos, 0 erros e 0 warnings |
| Build Release final após restore | Sete projetos, 0 erros de compilação e 0 warnings C#; cinco NU1900; saída 0 |
| Domain.Tests | 56 aprovados |
| Application.Tests | 64 aprovados |
| IntegrationTests | 191 casos: 78 aprovados, 113 falhas na fixture SQL |
| Suíte completa final | **311 casos: 198 aprovados, 113 falhas, 0 ignorados**; saída 2 |
| Auditoria solicitada, com transitivos | Saída 1; acesso ao índice api.nuget.org falhou por bloqueio de socket em api.nuget.org:443 |
| Header/OpenAPI sem SQL | Formato/canonicalidade, autenticação/no-store, quatro rotas, If-Match obrigatório, 204 sem schema JSON e novo ETag aprovados |
| Modelo/snapshot | HasPendingModelChanges false; sem nova migration ou modificação histórica |
| Revisão estática final | git diff --check sem erros; links locais válidos; sem arquivos sensíveis/temporários visíveis ao Git ou padrões de segredo encontrados na inspeção; índice Git sem alterações staged |

Docker info retornou acesso negado a C:\Users\jvito\.docker\config.json e ao pipe npipe:////./pipe/docker_engine. Os **113 testes SQL (77 anteriores + 36 novos)** falharam antes de executar suas asserções, por DockerUnavailableException. Isso não comprova permissões, DDL anterior ou concorrência no SQL Server. Não se desabilitaram testes, proteção, auditoria ou sandbox. Avisos ambientais de Data Protection do perfil Windows continuaram presentes, sem alteração de configuração.

Rodadas anteriores: 275 casos (198 aprovados/77 bloqueados) antes de acrescentar os casos SQL de C; depois 308 (198 aprovados/110 bloqueados); a rodada final inclui os três casos adicionais de edição em inativas e namespace de renomeação. Não houve teste removido ou ignorado.

Cobertura nova SQL preparada:

- PUT/PATCH dos dois recursos, 204 vazio, no-store, ETag/GET atual, timestamps UTC, preservação de CreatedAtUtc/dados imutáveis e edição em inativas.
- Ausência/má formação de If-Match, múltiplos headers, corpos nulos/ausentes/tipos incorretos, campos desconhecidos e mass assignment com valores dos tipos esperados.
- Ausência de mudança com relógio fixo, versão antiga, proprietário alheio/inexistente e falha de renomeação sem efeito parcial.
- Duas requisições independentes sincronizadas após as leituras reais, com resultados 204/412. Interceptor observa o parâmetro RowVersion no WHERE do UPDATE e exige os bytes enviados pelo cliente, inclusive após carregar uma versão mais recente.
- Unicidade de categoria inativa, NFC/caixa, acentos distintos, namespace por usuário/tipo, reativação e versão antiga que não deve ser confundida com duplicidade.
- Direitos efetivos por coluna, operações permitidas, campos imutáveis/DELETE/DDL/idempotência negados, concessões extras/delegáveis rejeitadas em bancos descartáveis.
- Replay de criação de conta com corpo/Location/ETag originais depois de PUT e PATCH reais.

Expectativas antigas legitimamente ajustadas: OpenAPI agora exige PUT/PATCH em ambos os recursos; a prova de UPDATE negado em Category passou de Name (agora permitido) para Kind (imutável); o teste de rotas de categorias verifica 428 nos novos verbos sem If-Match e mantém DELETE 405. Os testes antigos contra UPDATE amplo, DELETE, alteração de saldo inicial, excesso de privilégios, dinheiro e autenticação foram preservados. Os doubles de aplicação antigos apenas implementam os novos membros da porta, sem remover verificações.

Logs da implementação inicial ignorados em `.local/stage-4-c-evidence/`. Não houve nova execução GitHub Actions. As 216 aprovações informadas pelo usuário e a CI da baseline não foram atribuídas a esta implementação. A validação externa posterior de C está registrada a seguir, sem substituir o histórico do sandbox.

## Validação externa e correção das sete falhas — 14/11/2026

**Evidência fornecida pelo usuário:** com Docker e SQL Server/Testcontainers disponíveis no PowerShell local, a execução teve **311 testes: 304 aprovados, sete falhas e zero ignorados**. O build não apresentou erros de compilação. Foram informados cinco avisos NU1900 por indisponibilidade temporária da consulta de vulnerabilidades. Isso não comprova auditoria bem-sucedida nem aprovação integral da Entrega C.

### Causas e correções restritas aos testes

- `CategoryPersistenceSqlTests.Category_permissions_and_actual_column_types_are_restricted`: a tentativa `UPDATE dbo.Categories SET Kind='income' WHERE Id=@id` deve ser negada com **230**, pois Kind é uma coluna imutável sem permissão UPDATE. A tentativa de DELETE continua exigindo **229**, negação no objeto. Cada operação agora declara seu código esperado; não se aceita uma lista indiscriminada de erros.
- `FinancialConcurrencySqlTests.Column_updates_are_allowed_but_immutable_fields_delete_and_ddl_are_denied`, para contas e categorias: cada `UPDATE dbo.<tabela> SET [<coluna imutável>]=[<coluna imutável>] WHERE Id=@id` passa a exigir exatamente **230**. Permanecem as verificações de permissões efetivas, UPDATE permitido apenas nas colunas editáveis e negação de DELETE/DDL/escrita em IdempotencyRecords. Não se concederam novos direitos.
- `FinancialMutationSqlTests.Missing_or_invalid_preconditions_never_modify_the_resource`, nos quatro verbos/recursos: a primeira entrada do loop era `""`. A reprodução local mostrou que `HttpRequestMessage.Headers.TryAddWithoutValidation("If-Match", "")` não entrega o header ao contexto do TestServer. O parser recebe ausência real e retorna corretamente 428; o corpo do comando é válido. O teste agora fornece o valor vazio diretamente ao contexto de entrada por `TestServer.CreateHandler`, mantendo o cliente autenticado, o corpo e o pipeline real da API. Os demais valores continuam sendo enviados pelo HttpClient habitual.
- O caso existente `IfMatchContractTests.Absent_header_is_428_and_empty_header_is_400` foi fortalecido com requisições ao TestServer para comprovar ausência **428** e presença vazia **400**. Os testes existentes de formato inválido e versão antiga **412** foram preservados. Não foram adicionados nem removidos casos: o total continua **311**.

O middleware de Program.cs trata HTTPS e Cache-Control; a leitura de If-Match ocorre nas actions após autenticação e validação do corpo. A reprodução confirmou que `IfMatchHeader.Read` já distingue ausência de valor vazio corretamente. **Nenhum código de produção, grant, verificador, RowVersion, snapshot idempotente, migration ou schema foi alterado nesta correção.**

### Revalidação executada pelo Codex após a correção

| Verificação | Resultado efetivamente observado |
|---|---|
| Reprodução isolada do transporte | Header ausente e header vazio via HttpClient chegam como ausência: 428; header presente vazio configurado no contexto: 400; espaço e wildcard: 400 |
| Build Release `--no-restore` | Sete projetos, saída 0, zero erros, zero warnings C# e cinco NU1900 |
| `IfMatchContractTests` selecionados | **20 aprovados, zero falhas e zero ignorados**, saída 0 |
| Suíte completa `dotnet test ... --no-restore` | **311 casos: 198 aprovados, 113 falhas na fixture Docker, zero ignorados**, saída 2 |
| Docker | Acesso negado a config.json e ao pipe docker_engine; as asserções dos testes SQL não foram alcançadas |
| Revisão | Alterações desta correção apenas nos quatro arquivos de testes citados e neste documento; git diff --check aprovado |

Logs e reprodução local de diagnóstico em `.local/ifmatch-diagnostic/`, ignorados pelo Git. Os avisos ambientais de Data Protection continuaram presentes; nenhuma chave, credencial ou configuração foi alterada. Não se executou nova auditoria nessa correção. Naquele momento, a confirmação externa dos 311 testes ainda estava pendente; ela foi posteriormente fornecida pelo usuário, conforme a seção seguinte.

## Validação externa final concluída

Após as correções descritas acima, o usuário executou no PowerShell local, com Docker/SQL Server/Testcontainers disponíveis:

```powershell
dotnet test FinanceFlow.slnx --configuration Release --no-restore
```

Resultado externo confirmado: **311 testes aprovados, 0 falhas e 0 ignorados**, incluindo a integração real com SQL Server/Testcontainers. A regressão completa não está mais pendente. Esse resultado foi fornecido pelo usuário; não é uma execução do Codex nem substitui o histórico das sete falhas anteriores e das limitações do sandbox.

Na auditoria NuGet externa, os **sete projetos foram apresentados sem pacotes vulneráveis identificados**, porém a consulta emitiu **NU1900 por indisponibilidade do índice de vulnerabilidades**. A ausência de pacotes identificados nessa saída não comprova ausência de vulnerabilidades: **a auditoria de segurança não é conclusiva** e deve ser repetida quando o índice estiver disponível. Não se afirma novo build sem avisos nem desaparecimento dos NU1900.

A revisão estática final do Codex comparou os **34 arquivos alterados/criados** com `64b32d3` e foi **aprovada, sem problemas bloqueantes**. Abrangeu isolamento por proprietário, concorrência real com If-Match/RowVersion, permissões por coluna, imutabilidade, contratos HTTP, idempotência e escopo. A ressalva documental sobre a validação externa pendente foi atualizada neste fechamento.

Essas evidências não comprovam aplicação de grants ou migrations a banco persistente, publicação ou nova execução aprovada do GitHub Actions da Entrega C. Nenhum teste foi reexecutado nesta atualização documental.

## Reprodução no PowerShell externo

Pré-requisitos: SDK .NET 10.0.4xx, Docker Desktop/Engine Linux acessível, recursos para SQL Server 2022 x64 e acesso ao NuGet/MCR. As fixtures existentes geram credenciais criptográficas e bancos descartáveis; não é preciso carregar .env, certificados ou credenciais persistentes. Não instalar dotnet-ef nem executar migrations/provisionamento no banco local para validar a suíte.

```powershell
Set-Location C:\FinanceFlow
$ErrorActionPreference = 'Stop'
function Assert-DeliveryCStep([string]$Step) {
    if ($LASTEXITCODE -ne 0) { throw "$Step falhou; preserve a saída." }
}
docker info --format '{{.ServerVersion}}'
Assert-DeliveryCStep 'Docker'
dotnet restore FinanceFlow.slnx --configfile NuGet.Config
Assert-DeliveryCStep 'Restore'
dotnet build FinanceFlow.slnx --configuration Release --no-restore
Assert-DeliveryCStep 'Build'
dotnet test FinanceFlow.slnx --configuration Release --no-restore
Assert-DeliveryCStep 'Testes SQL e regressão'
dotnet list FinanceFlow.slnx package --vulnerable --include-transitive
Assert-DeliveryCStep 'Auditoria'
git diff --check
Assert-DeliveryCStep 'Whitespace'
```

Esperado: **311 aprovados, 0 falhas e 0 ignorados**, build sem erros e auditoria efetivamente consultada. Registrar avisos NU1900 separadamente; não suprimir nem tratar consulta indisponível como ausência de vulnerabilidades. Se houver falha SQL real, preservar o resultado para correção; não ampliar grants ou relaxar asserts para obter sucesso.

Para usar os novos endpoints posteriormente no ambiente persistente, será preciso aplicar **apenas o provisionamento atualizado dos grants**, com autorização específica. Não há nova migration de C. Não execute essa alteração apenas para reproduzir testes descartáveis; nenhuma ação em banco persistente, .env, credenciais, certificados ou volumes foi executada pelo Codex.

## Arquivos

Criados:

```text
docs/etapa-4/entrega-c-validacao.md
src/FinanceFlow.Api/Concurrency/IfMatchHeader.cs
tests/FinanceFlow.Application.Tests/FinancialChangesServiceTests.cs
tests/FinanceFlow.Domain.Tests/CategoryChangesTests.cs
tests/FinanceFlow.Domain.Tests/FinancialAccountChangesTests.cs
tests/FinanceFlow.IntegrationTests/FinancialChangeTestSupport.cs
tests/FinanceFlow.IntegrationTests/FinancialConcurrencySqlTests.cs
tests/FinanceFlow.IntegrationTests/FinancialMutationSqlTests.cs
tests/FinanceFlow.IntegrationTests/IfMatchContractTests.cs
```

Modificados: README.md; docs/etapa-4/README.md; docs/etapa-4/entrega-b-validacao.md; os dois arquivos de contratos HTTP Accounts/Categories; os dois controllers; ApiDocumentationConfiguration; ApiProblemDetails; Program; os dois arquivos de contratos Application; FinancialAccountsService/CategoriesService; FinancialAccount/Category; FinancialAccountStore/CategoryStore; DatabaseTasks; FinancialAccountsServiceTests/CategoriesServiceTests; AccountInputAndModelTests/CategoryInputAndModelTests; CategoriesSqlTests; CategoryPersistenceSqlTests.

## Pendências e riscos

- Regressão completa concluída externamente pelo usuário após as correções: 311 aprovados, 0 falhas e 0 ignorados, incluindo SQL Server/Testcontainers. Revisão estática final dos 34 arquivos aprovada, sem problemas bloqueantes.
- Reexecutar a auditoria NuGet quando o índice de vulnerabilidades estiver disponível. A saída externa apresentou os sete projetos sem pacotes vulneráveis identificados, mas os avisos NU1900 tornam o resultado inconclusivo. Nenhuma dependência foi adicionada ou alterada.
- A dependência de prefixos SQL em inglês para duplicidade/idempotência continua restrita e falha de modo fechado; não foi ampliada nesta entrega.
- PUT/PATCH sem mudança de valores custam um UPDATE e avançam versão. É uma decisão explícita para garantir a precondição em todas as gravações, sem retry/otimização prematuros.
- A suíte usa somente bancos descartáveis; não há aprovação para alterar banco persistente, iniciar a Etapa 5 ou publicar C.

**Sem stage, commit, push ou nova CI. Etapa 5 não iniciada.**
