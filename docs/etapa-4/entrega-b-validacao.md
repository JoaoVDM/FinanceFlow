# Etapa 4 — Entrega B: categorias financeiras

## Estado e origem das evidências

Implementação local de criação e consulta de categorias, sem Entrega C ou Etapa 5. **Validação externa confirmada pelo usuário em 14/11/2026: 216 testes aprovados, incluindo integração SQL Server/Testcontainers, e auditoria dos sete projetos sem vulnerabilidades conhecidas nas fontes consultadas.** A publicação posterior de B foi confirmada pelo usuário no commit `64b32d3`, com GitHub Actions `43642389840 — Success`, conforme fechamento abaixo. O build do Codex com avisos NU1900 e o histórico do sandbox permanecem registrados abaixo; não se afirma novo build totalmente sem warnings.

Workspace inicial: C:\FinanceFlow, `main` limpa em **9c938c8 — refactor: improve code readability and maintainability**. O usuário confirmou a CI **43564544104 — Success** e as validações anteriores dos 151 testes para essa baseline. São evidências externas do código anterior, não execuções do Codex nem aprovação da Entrega B.

Fontes: [arquitetura](../etapa-1/adr/001-arquitetura-e-dependencias.md), [modelo](../etapa-1/modelo-de-dominio.md), [contratos](../etapa-1/contratos-rest.md), [ciclo financeiro](../etapa-1/adr/003-modelagem-financeira.md), [concorrência/idempotência](../etapa-1/adr/007-concorrencia-e-idempotencia.md) e requisitos específicos autorizados pelo usuário para B. Nenhum ADR foi alterado.

## Arquitetura e regras

- Domain/Categories: Category e invariantes sem EF, Identity ou ASP.NET. Id/proprietário obrigatórios, nome normalizado validado, kind fechado e timestamps UTC. Não existem operações públicas de edição ou atividade.
- Application/Categories: CategoriesService, comandos, consultas, resultados e ICategoryStore específico. Reutiliza ICurrentUser e TimeProvider; gera Guid e obtém proprietário do contexto confiável.
- Infrastructure/Persistence/Categories: projeções, consultas EF, mapeamento e gravação. DbContext e serviços Scoped; sem IQueryable exposto, repositório genérico, MediatR ou pacotes novos.
- Api: DTOs e CategoriesController, sem acesso ao DbContext. JWT, HTTPS, revogação, limite global, limite de corpo e Problem Details existentes continuam em vigor.

Category contém Id, UserId, Name, NormalizedName, Kind, IsActive, CreatedAtUtc, UpdatedAtUtc e RowVersion. Nasce ativa, com UpdatedAtUtc nulo; CreatedAtUtc vem do TimeProvider e é convertido para UTC. RowVersion é gerado pelo SQL Server. Tipo e demais campos não têm setters públicos; EF também impede alterações posteriores de Kind, que participa da chave alternativa.

### Normalização determinística

```csharp
name.Trim()
    .Normalize(NormalizationForm.FormC)
    .ToUpperInvariant()
    .Normalize(NormalizationForm.FormC);
```

Name mantém somente Trim/NFC, preservando caixa de exibição. Ambos os nomes devem conter de 1 a 100 unidades UTF-16 após suas respectivas normalizações, sem truncamento. A API não impõe um limite bruto anterior à normalização que impediria uma representação NFD equivalente de caber no cadastro. O limite global de corpo continua 16 KiB.

Alimentação/ alimentação e Café/CAFÉ/Cafe\u0301 conflitam no mesmo usuário/tipo. Cafe/Café são distintos. Mesmo nome em tipos ou proprietários diferentes é permitido. Categorias inativas continuam reservando nomes. Não se usa o normalizador do Identity nem se altera a collation global.

## Contratos HTTP

| Método | Rota | Sucesso | Erros principais |
|---|---|---|---|
| POST | /api/v1/categories | 201, CategoryView, Location e ETag | 400, 401, 409, 429, 503; 500 seguro para falha inesperada |
| GET | /api/v1/categories | 200, CategoryPage | 400, 401, 429, 503 |
| GET | /api/v1/categories/{id} | 200, CategoryView e ETag | 401, 404, 429, 503 |

Todos exigem JWT válido. POST aceita apenas `name` e `kind`; kind deve ser exatamente income ou expense. Idempotency-Key não é obrigatório, não participa da criação e não gera replay. Repetição com o mesmo nome/tipo normalizado resulta em 409 pelo índice SQL, inclusive se o cliente enviar esse cabeçalho.

Exemplo fictício de entrada:

```json
{
  "name": "Alimentação",
  "kind": "expense"
}
```

CategoryView expõe somente id, name, kind, isActive, createdAtUtc, updatedAtUtc e version. Não expõe UserId, NormalizedName, entidades EF ou bytes de RowVersion. Location é `/api/v1/categories/{id}`; ETag forte é o version Base64 de oito bytes, entre aspas. As respostas financeiras usam Cache-Control: no-store. Campos desconhecidos no JSON, inclusive os controlados pelo servidor, são rejeitados pelo pipeline existente.

Listagem: page=1, pageSize=20 (1–100), kind/isActive opcionais, sort=name ou id, direction=asc ou desc. Parâmetros desconhecidos, repetidos, vazios ou com valores inválidos retornam 400. Offset fora do limite do provider também retorna 400. Retorno: `{ items, page, pageSize, totalCount, totalPages }`; página além do fim retorna items vazio e totais corretos.

UserId limita consulta individual, filtros, contagem e paginação no SQL. Sem filtro de atividade, ativas/inativas aparecem. Nome usa a ordenação do banco, com Id de desempate; ordenação por Id usa a semântica SQL de uniqueidentifier. Não há paginação em memória ou promessa de snapshot entre as consultas de contagem e itens sob escritas simultâneas. Recurso alheio/inexistente retorna o mesmo 404 genérico.

OpenAPI documenta Bearer, os três endpoints, kind fechado, paginação/filtros, Location/ETag e respostas relevantes. Não exige Idempotency-Key em categorias. Accounts/Identity mantêm seus contratos; OpenAPI/Swagger continuam restritos a Development.

## Persistência, migration e segurança SQL

Migration incremental **20261114000200_CategoriesDeliveryB**, posterior a AccountsDeliveryA. Cria somente dbo.Categories e suas chaves/índice/constraints:

- PK Id; FK UserId → AspNetUsers com NoAction.
- Chave alternativa (Id, UserId, Kind), para os vínculos compostos futuros, sem antecipar tabelas de lançamentos.
- Índice único (UserId, Kind, NormalizedName), **sem filtro IsActive**. Não foi adicionado índice para cada filtro.
- Name/NormalizedName nvarchar(100); NormalizedName usa Latin1_General_100_BIN2. Kind varchar(7) usa a mesma comparação binária.
- CHECKs de nomes não vazios após remoção de espaços SQL e de Kind exato. O CHECK de Kind também exige DATALENGTH 6/7 conforme o código, pois até BIN2 compara strings preenchendo espaços finais.
- Timestamps datetimeoffset, UpdatedAtUtc nullable e rowversion real.

SQL reforça estrutura, tamanho e unicidade da representação persistida; Trim Unicode, NFC e ToUpperInvariant pertencem ao domínio, sem tentar reproduzir a normalização .NET com UPPER SQL. Não há coluna calculada, trigger, seed ou alteração da collation do Identity.

CategoriesDeliveryBModel é o novo target congelado cumulativo: chama o target histórico da Entrega A e acrescenta apenas Category. O snapshot atual aponta para esse target. Migrations/targets anteriores não foram reescritos. A nova migration foi escrita seguindo a estratégia existente, sem instalar ferramentas EF. **HasPendingModelChanges() retornou false no teste offline**, mas isso não substitui a execução do DDL no SQL Server.

A criação usa um SaveChanges: a confirmação só retorna após sucesso no banco. O índice único arbitra concorrência, sem consulta prévia como garantia. Apenas erro SQL 2601 com entrada EF Category e prefixo do índice/objeto esperados é convertido em Duplicate/409; PK, FK e outras falhas não viram conflito de nome. Timeout/deadlock conhecidos retornam indisponibilidade. Falhas inesperadas continuam no tratamento seguro global. Não há tabela idempotente para categorias.

DatabaseTasks acrescenta somente SELECT/INSERT em Categories à conta da API e à lista permitida do verificador. AspNetUsers, FinancialAccounts, IdempotencyRecords e history mantêm suas permissões. UPDATE/DELETE/ALTER, DDL, roles administrativas e grants inesperados continuam proibidos. Administrador, migrador e API permanecem separados. Readiness verifica migrations e leitura da tabela; liveness não depende do banco.

Nada foi aplicado ao banco persistente do usuário. Não houve alteração de credenciais, certificados, .env, volumes ou permissões do ambiente.

## Testes e resultados reais do Codex

Execução no sandbox Windows em 14/11/2026, SDK .NET 10.0.404. Foram acrescentados **65 casos**, mantendo os **151 preexistentes**, totalizando **216**.

| Verificação | Resultado executado |
|---|---|
| Git inicial | Limpo, main em 9c938c8 |
| Domain.Tests | 36 aprovados: 17 anteriores e 19 novos |
| Application.Tests | 45 aprovados: 32 anteriores e 13 novos |
| IntegrationTests | 135 casos: 58 aprovados e 77 falhas de infraestrutura |
| Suíte completa final | **216 casos; 139 aprovados, 77 falhas, 0 ignorados**; saída 2 |
| Restore com NuGet.Config | Saída 0, pacotes disponíveis; cinco avisos NU1900 por consulta NuGet inacessível |
| Build Release final | Sete projetos, zero erros e zero warnings C#; cinco avisos NU1900; saída 0 |
| Modelo/snapshot offline | Sem diferenças; mapeamento BIN2, índice não filtrado e chaves conferidos |
| OpenAPI em TestServer | Rotas de categorias autenticadas, sem Idempotency-Key obrigatório; metadados de paginação/enum/ETag/Location aprovados |
| Comparação OpenAPI da baseline | Nenhuma diferença nas rotas, schemas e esquemas de segurança preexistentes; apenas contratos de categorias acrescentados |
| git diff --check | Sem erros |
| Revisão estática final | Links locais válidos; nenhum arquivo sensível/temporário visível ao Git ou correspondência aos padrões de segredo examinados; sem stage |

Os 77 casos SQL (49 anteriores e 28 novos) falharam na fixture obrigatória, antes de comprovar suas asserções. Docker info retornou acesso negado a `C:\Users\jvito\.docker\config.json`; o pipe npipe:////./pipe/docker_engine ficou inacessível, com mensagem de arquivo não encontrado na tentativa desta entrega. Testcontainers registrou DockerUnavailableException. Não se alteraram permissões, contexto, ACLs, named pipes ou sandbox para contornar o bloqueio.

A primeira rodada completa tinha 214 casos (139 aprovados/75 falhas); a rodada final acrescentou os dois casos SQL de proteções HTTP/revogação e limite global, chegando aos 216 acima. Nenhum caso foi removido ou ignorado. A única expectativa preexistente atualizada foi a ausência de `/categories` no OpenAPI: agora exige sua presença, preservando as verificações de Accounts e acrescentando verificações específicas de B. Não há EF InMemory, SQLite ou mocks para simular integridade SQL.

Cobertura nova:

- Domain: nomes/limites após normalização, caixa/NFC/acentos, kind exato, Guid/owner, atividade inicial, timestamps e setters privados.
- Application: owner confiável, relógio determinístico, comandos inválidos, propagação de conflitos/indisponibilidade, falha inesperada, escopo de leitura e validação de consultas.
- Integração sem SQL: JWT obrigatório nas três operações, snapshot/modelo e contrato OpenAPI de categorias com preservação de Accounts.
- SQL obrigatório: migração desde Accounts preservando usuário/conta/token; nomes equivalentes e distintos; tipos/usuários distintos; reserva entre inativas; corrida sincronizada antes dos INSERTs reais; isolamento; paginação/filtros/ordem; JSON desconhecido; fronteiras de nome; PK/FK e rollback de inserção rejeitada; schema/constraints; grants negados/verificador; readiness sem acesso; HTTPS, revogação, ausência de escrita e 429/Retry-After.

O sucesso histórico de 151 testes/CI da baseline não foi atribuído às execuções desta entrega. Os restores com NU1900 no sandbox não comprovaram a auditoria NuGet, que não foi desabilitada ou suprimida; a consulta externa posterior está registrada na seção seguinte. O host também mantém os avisos ambientais de Data Protection do perfil Windows já conhecidos; sua configuração não foi alterada. Logs locais ficam em `.local/stage-4-b-evidence/`, ignorados pelo Git.

## Validação externa concluída

Em **14/11/2026**, o usuário confirmou os resultados abaixo no PowerShell local, fora do sandbox. São **evidências externas fornecidas pelo usuário**, não comandos executados pelo Codex nesta atualização documental.

| Verificação externa | Resultado confirmado pelo usuário |
|---|---|
| `dotnet test FinanceFlow.slnx --configuration Release --no-restore` | **216 testes aprovados, 0 falhas e 0 ignorados**, incluindo integração SQL Server com Testcontainers |
| Avisos durante os testes | NU1900 relacionados à consulta anterior ao NuGet |
| `dotnet list FinanceFlow.slnx package --vulnerable --include-transitive` | Restauração bem-sucedida; **sete projetos sem vulnerabilidades conhecidas nas fontes consultadas** |

A regressão completa e a auditoria NuGet não estão mais pendentes. A consulta posterior bem-sucedida não apaga os avisos NU1900 nem comprova novo build totalmente sem warnings. As 77 falhas de infraestrutura continuam como histórico real das execuções do Codex; os casos SQL foram posteriormente aprovados pelo usuário fora do sandbox.

Essas evidências dizem respeito à suíte com bancos descartáveis de Testcontainers. **Não confirmam aplicação de migrations ou provisionamento em banco persistente.** No fechamento externo inicial, a publicação e a CI de B ainda estavam pendentes; foram posteriormente confirmadas na seção seguinte. A CI da baseline 9c938c8 permanece uma evidência distinta.

## Publicação posterior confirmada pelo usuário

O usuário confirmou a publicação de B em **64b32d3 — feat: implement financial categories** e o GitHub Actions **43642389840 — Success**. As referências anteriores a publicação/CI pendentes descrevem o fechamento anterior a esse evento. O histórico de testes, avisos e limitações do sandbox foi preservado. Não há nova evidência sobre banco persistente.

Este documento registra o escopo histórico de criação/consulta da Entrega B. Edição, atividade e If-Match pertencem à [Entrega C](entrega-c-validacao.md), cuja validação não é comprovada pela CI de B.

## Procedimento de reprodução externa

Os comandos abaixo ficam como referência para futuras reproduções, sem representar pendência dos testes ou da auditoria já concluídos. Esta atualização não acrescenta evidência de um novo build sem warnings.

No PowerShell externo, com SDK .NET 10.0.4xx, Docker Desktop/Engine Linux acessível, recursos para SQL Server x64 e acesso a NuGet/MCR. Não é necessário instalar dotnet-ef, carregar .env ou preparar credenciais: as fixtures criam credenciais criptográficas e bancos descartáveis.

```powershell
Set-Location C:\FinanceFlow
$ErrorActionPreference = 'Stop'
function Assert-DeliveryBStep([string]$Step) {
    if ($LASTEXITCODE -ne 0) { throw "$Step falhou; preserve a saída." }
}

docker info --format '{{.ServerVersion}}'
Assert-DeliveryBStep 'Docker'
dotnet restore FinanceFlow.slnx --configfile NuGet.Config
Assert-DeliveryBStep 'Restore'
dotnet build FinanceFlow.slnx --configuration Release --no-restore
Assert-DeliveryBStep 'Build Release'
dotnet test FinanceFlow.slnx --configuration Release --no-restore
Assert-DeliveryBStep 'Testes completos'
dotnet list FinanceFlow.slnx package --vulnerable --include-transitive
Assert-DeliveryBStep 'Auditoria NuGet'
git diff --check
Assert-DeliveryBStep 'Whitespace'
```

Em futuras execuções, exigir **216 aprovados, 0 falhas e 0 ignorados**; examinar o resultado efetivo da auditoria, pois código zero sozinho não atesta ausência de vulnerabilidades nem resolve um NU1900. A suíte inclui aplicação da migration em banco vazio e upgrade desde A, HTTP com usuários reais e validação de privilégios, sempre em bancos descartáveis. Não trocar a fixture por outro provider nem desabilitar testes para obter sucesso.

Se o banco persistente ainda estiver no schema da Entrega A, a API permanecerá notReady até a aplicação explícita da nova migration e dos grants. **Essa alteração de banco não foi executada pelo Codex nem confirmada pelas evidências externas acima.** Quando houver autorização específica para esse ambiente, o procedimento existente é provisionar, migrar com credencial separada e repetir o provisionamento/verificador (`docker compose run --rm provision`, `docker compose run --rm migrations`, `docker compose run --rm provision`), usando o ambiente e a imagem atualizados pelo usuário. Não destruir volumes ou redefinir credenciais para resolver problemas. A validação externa já concluída utilizou a suíte com bancos descartáveis.

## Inventário

Criados:

```text
docs/etapa-4/entrega-b-validacao.md
src/FinanceFlow.Api/Contracts/Categories/CategoryRequests.cs
src/FinanceFlow.Api/Controllers/CategoriesController.cs
src/FinanceFlow.Application/Categories/CategoriesService.cs
src/FinanceFlow.Application/Categories/CategoryContracts.cs
src/FinanceFlow.Domain/Categories/Category.cs
src/FinanceFlow.Infrastructure/Persistence/Categories/CategoryConfiguration.cs
src/FinanceFlow.Infrastructure/Persistence/Categories/CategoryStore.cs
src/FinanceFlow.Infrastructure/Persistence/Migrations/20261114000200_CategoriesDeliveryB.cs
src/FinanceFlow.Infrastructure/Persistence/Migrations/CategoriesDeliveryBModel.cs
tests/FinanceFlow.Application.Tests/CategoriesServiceTests.cs
tests/FinanceFlow.Domain.Tests/CategoryTests.cs
tests/FinanceFlow.IntegrationTests/CategoriesSqlTests.cs
tests/FinanceFlow.IntegrationTests/CategoryInputAndModelTests.cs
tests/FinanceFlow.IntegrationTests/CategoryPersistenceSqlTests.cs
```

Modificados:

```text
README.md
docs/etapa-4/README.md
docs/etapa-4/refatoracao-legibilidade.md
src/FinanceFlow.Api/Configuration/ApiDocumentationConfiguration.cs
src/FinanceFlow.Api/Controllers/HealthController.cs
src/FinanceFlow.Infrastructure/DependencyInjection.cs
src/FinanceFlow.Infrastructure/Persistence/DatabaseTasks.cs
src/FinanceFlow.Infrastructure/Persistence/FinanceFlowDbContext.cs
src/FinanceFlow.Infrastructure/Persistence/Migrations/FinanceFlowDbContextModelSnapshot.cs
tests/FinanceFlow.IntegrationTests/AccountInputAndModelTests.cs
```

## Pendências e limites

- Testes SQL e auditoria NuGet concluídos externamente pelo usuário em 14/11/2026; não são mais pendências. A pendência de publicação/CI de B foi posteriormente resolvida pelo usuário com 64b32d3/43642389840; isso não comprova a entrega seguinte. Não há confirmação de aplicação ao banco persistente nem de novo build sem NU1900.
- O reconhecimento da duplicidade, seguindo o padrão restrito existente, depende do prefixo em inglês da mensagem SQL. Outro idioma falha de modo fechado, sem transformar um erro desconhecido em 409. Uma alternativa independente de idioma exige avaliação funcional própria.
- PUT/PATCH/DELETE, renomeação, atividade, If-Match, lançamentos, saldos, transferências e relatórios não foram implementados. As considerações funcionais de erros SQL de Accounts permanecem separadas e seu código não mudou.

Status final: **Entrega B implementada e validada externamente pelo usuário: 216 testes aprovados, incluindo SQL Server/Testcontainers, e sete projetos sem vulnerabilidades conhecidas na auditoria das fontes consultadas. Posteriormente publicada em 64b32d3, com CI 43642389840 aprovada conforme confirmação do usuário.** Preservados o histórico de bloqueio do Docker no sandbox e os avisos NU1900; não se afirma novo build totalmente sem warnings ou aplicação a banco persistente. Entrega C e Etapa 5 não iniciadas. Sem stage, commit ou push pelo Codex.
