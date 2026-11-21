# FinanceFlow API

API REST de gestão financeira pessoal desenvolvida como portfólio profissional de backend em C# e ASP.NET Core. A evolução prioriza regras financeiras confiáveis, isolamento entre usuários e testes automatizados.

## Estado do projeto

Etapas 1, 2 e 3 concluídas e publicadas. O usuário confirmou, após as últimas correções, build de sete projetos sem erros/warnings, 73 testes aprovados, 22 verificações HTTPS Development, três Production e GitHub Actions 43439337019 com Success no commit 29fe1c0. São evidências externas, registradas em [docs/etapa-3](docs/etapa-3/validacao.md).

A Entrega A da Etapa 4 está concluída e validada externamente pelo usuário: 151 testes aprovados, build de sete projetos sem erros/warnings, migration aplicada, permissões verificadas e API Docker healthy. Os scripts HTTPS de identidade (22 verificações) e contas passaram; em Production, readiness respondeu 200 e OpenAPI/Swagger 404. A Entrega A foi publicada no commit `5a13762`; o usuário confirmou GitHub Actions `43559125753` com **Success**. Essas são evidências externas do commit publicado, anteriores à [refatoração de legibilidade](docs/etapa-4/refatoracao-legibilidade.md), cuja validação é registrada separadamente. A Entrega C foi posteriormente validada externamente, conforme registro abaixo. Veja [escopo](docs/etapa-4/README.md) e [evidências externas e histórico do sandbox](docs/etapa-4/entrega-a-validacao.md). Esta API é um portfólio, sem prontidão operacional de produto financeiro público.

A refatoração foi publicada em **9c938c8**, com GitHub Actions **43564544104 — Success**, conforme evidência fornecida pelo usuário. Essa é a baseline da Entrega B e não comprova as alterações locais posteriores.

A **Entrega B — Categorias Financeiras** está implementada e foi validada externamente pelo usuário em **14/11/2026**: 216 testes aprovados, 0 falhas e 0 ignorados, incluindo SQL Server/Testcontainers; a auditoria posterior concluiu a restauração e não identificou vulnerabilidades conhecidas nos sete projetos nas fontes consultadas. Os testes apresentaram avisos NU1900; não se afirma novo build totalmente sem warnings. O usuário confirmou sua publicação em `64b32d3` e GitHub Actions `43642389840 — Success`; essas evidências não cobrem a Entrega C local. As evidências não confirmam aplicação a banco persistente; resultados externos e histórico do Docker inacessível no sandbox em [entrega-b-validacao](docs/etapa-4/entrega-b-validacao.md).

A **Entrega C — Edição, atividade e concorrência** está implementada e validada externamente pelo usuário no PowerShell com Docker/SQL Server/Testcontainers: **311 testes aprovados, 0 falhas e 0 ignorados**. PUT/PATCH de contas e categorias exigem If-Match forte e retornam 204 com novo ETag. Toda gravação aceita, inclusive sem mudança de valores, executa UPDATE condicionado à rowversion. Não há nova migration; a API recebe UPDATE somente nas colunas editáveis. A revisão estática final dos 34 arquivos foi aprovada, sem problemas bloqueantes. Na consulta NuGet externa, os sete projetos foram apresentados sem pacotes vulneráveis identificados, mas houve avisos NU1900 por indisponibilidade do índice de vulnerabilidades; a auditoria não é conclusiva. O histórico de bloqueio do Docker no sandbox foi preservado. A entrega permanece local, sem nova CI aprovada. Veja [evidências e procedimento externo](docs/etapa-4/entrega-c-validacao.md).

A baseline da **Etapa 5A** é `76e909b`, na branch `main`: o usuário confirmou a publicação da Etapa 4, 311 testes locais aprovados e CI aprovada. Essa confirmação é posterior ao fechamento histórico acima e não comprova as alterações locais da Entrega 5A. Posteriormente, o usuário validou a suíte completa de 5A no PowerShell com Docker/SQL Server/Testcontainers: **386 testes aprovados, 0 falhas e 0 ignorados**. A revisão consolidada dos 32 arquivos foi aprovada, sem problemas bloqueantes. O histórico de Docker inacessível no sandbox foi preservado; os avisos NU1900 mantêm a auditoria de vulnerabilidades inconclusiva. Não há aprovação de CI ou publicação de 5A confirmada. Veja [escopo e evidências](docs/etapa-5/README.md).

A **Entrega 5A foi posteriormente publicada em `f9e7d12`**, com 386 testes locais aprovados e GitHub Actions workflow **#7 aprovado**, conforme confirmação do usuário. Essa é a baseline de 5B; não comprova as alterações locais seguintes.

A **Entrega 5B — Edição e exclusão lógica de transações** está implementada localmente e validada externamente pelo usuário no PowerShell com SQL Server/Testcontainers: **461 testes aprovados, 0 falhas e 0 ignorados**, em aproximadamente 1m39s. PUT/DELETE exigem JWT e If-Match forte, retornam 204 sem corpo e novo ETag. A revisão técnica consolidada dos 22 arquivos contra `f9e7d12` foi aprovada, sem problemas bloqueantes. O histórico de 285 aprovações e 176 falhas na fixture Docker do sandbox permanece registrado separadamente. A auditoria NuGet continua inconclusiva pelos NU1900; não há publicação ou CI aprovada de 5B. Veja [escopo, regras e evidências de 5B](docs/etapa-5/entrega-5b-validacao.md).

Posteriormente, o usuário confirmou **5B publicada em `b548f4f` e CI #8 — Success**, com 461 testes locais aprovados. Essa é a baseline da **Entrega 5C — Consolidação financeira**. A revisão inicial acrescentou 16 casos SQL, sem novos endpoints, schema, migrations ou dependências. A primeira execução externa teve **475 aprovados, duas falhas e 0 ignorados**; após corrigir o tratamento de deadlock encapsulado pelo EF no POST e a instrumentação do teste de overflow, o usuário confirmou **477 aprovados, 0 falhas e 0 ignorados**, em **1m50s**, no PowerShell com Docker/SQL Server/Testcontainers. A auditoria consolidada dos oito arquivos foi aprovada sem problemas bloqueantes. O histórico do Codex permanece separado: build final com cinco NU1900 e regressão no sandbox com **285 aprovados, 192 falhas na fixture Docker e 0 ignorados**. A auditoria NuGet continua inconclusiva pelos NU1900. A Etapa 5 permanece oficialmente aberta até publicação autorizada e CI de 5C aprovada. Veja [cobertura, histórico e evidências de 5C](docs/etapa-5/entrega-5c-validacao.md).

A Etapa 5 foi posteriormente concluída/publicada em **f0969b8**, com **477 testes locais aprovados e CI #9 aprovada**, conforme confirmação do usuário. Essa é a baseline da **Entrega 6A — Transferências**, agora validada externamente em Docker Linux e **aprovada para commit**: **557 aprovados, zero falhas, zero ignorados, zero erros de runner e saída zero** na execução `20261120-224229-183-17da7b`, cujos três XMLs foram conferidos pelo Codex. O histórico do sandbox permanece separado: 327 aprovados/230 falhas na fixture Docker e tentativa posterior com 217 aprovações antes do bloqueio do módulo de integração pelo Controle de Aplicativo (`0x800711C7`). As execuções externas intermediárias de 555/557 e 556/557 e as duas correções de preparação/instrumentação estão documentadas. A 6A permanece local, sem publicação/CI aprovada; a auditoria NuGet anterior continua inconclusiva. Consulte [escopo e evidências de 6A](docs/etapa-6/README.md).

Estado posterior à auditoria de 6A: o usuário confirmou sua publicação em **6050035**, com **557 testes aprovados** e CI **44857771237 — success**. A **6B — Cancelamento lógico de transferências** teve **603 aprovados, zero falhas, zero ignorados e zero erros do runner** na execução externa **20261121-001714-582-bfda5d** (116 Domain, 115 Application, 372 Integration), com XMLs/códigos conferidos pelo Codex. A auditoria posterior encontrou o P2 de classificação SQL nas consultas. A correção preservou os 603 casos e acrescentou dez; a primeira execução externa dos 613 apresentou 611 aprovações e duas falhas na expectativa do tipo da exceção de deadlock, corrigida somente no teste. No sandbox após o P2 houve **228 aprovados e 269 falhas de fixture Docker**, com os 116 Domain bloqueados pelo Smart App Control; após o ajuste da asserção, 115 Application passaram, com Domain e Integration bloqueados no carregamento.

**6B aprovada para commit:** o usuário concluiu a execução Docker Linux/SQL Server/Testcontainers **20261121-020304-418-94cdd6**, com **613/613 aprovados (116 Domain, 115 Application, 382 Integration), zero falhas, ignorados e erros do runner**. O Codex conferiu os três XMLs e as saídas zero de restore/build/módulos/Docker, incluindo os dois casos P2 anteriormente falhos e suas verificações posteriores. A auditoria final não identificou impedimentos técnicos. O histórico do sandbox e a auditoria NuGet inconclusiva foram preservados. Veja [correção, cobertura e evidências da 6B](docs/etapa-6/entrega-6b-validacao.md). A 6B permanece sem publicação/CI aprovada; a 6C não foi iniciada.

## Requisitos e instalação

- .NET SDK estável da faixa 10.0.4xx; global.json aceita patches dessa faixa, incluindo 10.0.404.
- Git e um editor como VS Code.
- Acesso HTTPS ao NuGet para restaurar dependências.
- Docker Desktop com Engine Linux/WSL 2 acessível e recursos para SQL Server x64. Os testes obrigatórios usam Testcontainers.MsSql e falham se Docker estiver indisponível.

Instale o SDK pelo [site oficial do .NET](https://dotnet.microsoft.com/download/dotnet/10.0) e confirme `dotnet --version`. No PowerShell, a partir da pasta do projeto:

```powershell
dotnet restore FinanceFlow.slnx --configfile NuGet.Config
dotnet build FinanceFlow.slnx --configuration Release --no-restore
dotnet test FinanceFlow.slnx --configuration Release --no-build --no-restore
```

NuGet.Config usa somente a fonte pública nuget.org e mantém os pacotes em `.local/packages`, ignorado pelo Git. Versões diretas são fixadas em Directory.Packages.props. Não há instalação de ferramentas globais.

## Ambiente local, segredos e HTTPS

Gere segredos criptográficos e um certificado localhost de 30 dias, sem alterar o repositório de certificados ou a confiança do Windows:

```powershell
./scripts/Initialize-LocalEnvironment.ps1
```

O script Windows PowerShell cria `.env`, `.local/certs/api.pfx` e `.local/certs/api.crt`, todos ignorados pelo Git. Não sobrescreve um ambiente existente. Não imprima esses valores, não execute `docker compose config` sem `--quiet` em logs públicos e não apague volumes para resolver problemas de senha. O arquivo `.env.example` contém apenas nomes e valores vazios.

Segredos locais: MSSQL_SA_PASSWORD, MIGRATION_DB_PASSWORD, API_DB_PASSWORD, JWT_SIGNING_KEY_BASE64 e HTTPS_CERTIFICATE_PASSWORD. A chave JWT é Base64 de 32 bytes aleatórios. A API exige Database:ConnectionString, Jwt:Issuer, Jwt:Audience e Jwt:SigningKeyBase64 válidos; falha na inicialização se faltarem. A configuração é fornecida por variáveis de ambiente com `__`; senhas não entram nos appsettings.

## SQL Server, provisionamento e migrations explícitas

```powershell
docker compose config --quiet
docker compose up -d sqlserver
docker compose build
docker compose run --rm provision
docker compose run --rm migrations
docker compose run --rm provision
docker compose up -d api
```

Os serviços de ferramentas têm perfil próprio e são acionados explicitamente. O segundo provisionamento concede acesso aos objetos após a migration. Não há migration automática no startup da API. Nenhum passo apaga volumes ou redefine senhas de logins já existentes.

O SQL usa um volume nomeado e porta `127.0.0.1:14333`; a API usa o host interno `sqlserver`, nunca sa. Há três identidades separadas: administrador apenas no provisionamento, FinanceFlowMigrator para DDL e FinanceFlowApi para SELECT/INSERT/UPDATE em AspNetUsers, SELECT em __EFMigrationsHistory e SELECT/INSERT em FinancialAccounts/Categories/IdempotencyRecords/FinancialTransactions/Transfers. UPDATE financeiro permanece restrito às colunas editáveis de contas/categorias da Entrega C e às nove colunas de transações da Entrega 5B, documentadas abaixo. Transfers não recebe UPDATE/DELETE na 6A; IdempotencyRecords continua sem UPDATE/DELETE; a API não recebe DELETE financeiro, DDL ou credenciais administrativas. O provisionamento rejeita privilégios excessivos, sem remover permissões existentes silenciosamente.

Imagem SQL fixada: `mcr.microsoft.com/mssql/server:2022-CU22-GDR1-ubuntu-22.04`, SQL Server 2022 Developer. Tag confirmada no registro oficial e execução healthy confirmada na validação externa. A revisão da atualização de segurança mais recente continua necessária antes de exposição pública. Este pin serve ao ambiente local reproduzível, não declara a imagem atualizada para publicação. SQL usa criptografia com certificado do contêiner aceito apenas no ambiente local; em hospedagem, use validação de certificado adequada.

## Executar a API em Kestrel no host

Com SQL provisionado, carregue somente as configurações da aplicação no processo PowerShell atual:

```powershell
./scripts/Use-LocalEnvironment.ps1
dotnet run --project src/FinanceFlow.Api --launch-profile https
```

O perfil HTTPS e o Compose usam `https://localhost:5443`, em Development. Execute apenas uma dessas opções nessa porta. Para encerrar a instância em terminal, use Ctrl+C. Para ferramentas de banco no host, abra outro PowerShell, use `./scripts/Use-LocalEnvironment.ps1 -Mode DatabaseTools` e então `dotnet run --project src/FinanceFlow.Api --no-launch-profile -- --provision-database` ou `--migrate`. Essas operações encerram sem iniciar servidor. Volte ao modo Api antes de iniciar o host para não herdar segredos administrativos.

| Endpoint | Resultado esperado |
|---|---|
| GET /health | 200 e `{"status":"healthy"}` |
| GET /health/live | Alias técnico da mesma verificação de processo |
| GET /health/ready | 200 com schema disponível, ou 503 genérico sem detalhes internos |
| GET /openapi/v1.json | Documento OpenAPI em Development |
| GET /swagger/index.html | Swagger UI em Development |

```powershell
curl.exe --cacert .local/certs/api.crt https://localhost:5443/health
curl.exe --cacert .local/certs/api.crt https://localhost:5443/health/ready
curl.exe --cacert .local/certs/api.crt https://localhost:5443/openapi/v1.json
```

Com a API em Development e o schema pronto, `./scripts/Test-LocalApi.ps1` verifica healths, documentação, cadastro, login, perfil, token adulterado e revogação por senha/logout/desativação. Usa somente um usuário fictício, preservado como inativo ao final. Senhas e tokens são gerados em memória e enviados por stdin ao curl, sem imprimi-los ou passá-los como argumentos de processo. O usuário confirmou as 22 verificações na Etapa 3 e novamente no fechamento externo da Entrega A; o Codex não as repetiu por HTTPS no sandbox. A sequência completa para o PowerShell externo está em [validação da Entrega A](docs/etapa-4/entrega-a-validacao.md#validação-externa).

Readiness consulta conexão, migrations pendentes, colunas do usuário e acesso aos objetos de contas/idempotência. A indisponibilidade do SQL não torna o liveness uma falha. Saúde é técnica e não usa `/api/v1`. Os endpoints de credenciais rejeitam HTTP; não é necessário enfraquecer a validação TLS para usar o certificado local com `--cacert`.

## Contas financeiras — Entrega A

| Método | Rota | Contrato |
|---|---|---|
| POST | /api/v1/accounts | JWT e Idempotency-Key obrigatório; 201 com JSON/Location/ETag originais no replay; 400/401/409/429/503 |
| GET | /api/v1/accounts | JWT; filtros isActive/type; page=1, pageSize=20 (máximo 100), sort=name ou id, direction=asc ou desc |
| GET | /api/v1/accounts/{id} | JWT; 200 com ETag forte; recurso alheio/inexistente retorna 404 |

Entrada de criação: name, type, currency, openingBalance e openingDate. Tipos estritos: checking, savings, cash, other; somente BRL; saldo inicial entre -999999999999.99 e 999999999999.99 com centavos exatos; abertura não futura no calendário America/Sao_Paulo. Valores JSON não são convertidos via double/float, e precisão excessiva não pode apagar frações. Usuário e timestamps não são aceitos no corpo. Contas inativas são incluídas por padrão. A Entrega A original limitou-se à criação e consulta. Edição/atividade estão na Entrega C abaixo; saldo atual permanece fora do escopo.

Na preparação de um ambiente, aplique explicitamente a migration incremental 20261114000100_AccountsDeliveryA e repita o provisionamento para conceder SELECT/INSERT nas novas tabelas. A API não recebe UPDATE/DELETE nelas e não executa migrations no startup. O usuário confirmou essa sequência e o fluxo HTTPS de `./scripts/Test-LocalAccounts.ps1` no fechamento da Entrega A; o Codex não executou o [procedimento externo](docs/etapa-4/entrega-a-validacao.md#validação-externa) no banco persistente. O script preserva os registros e desativa somente o usuário fictício que criou, sem imprimir tokens/senhas.

## Categorias financeiras — Entrega B

| Método | Rota | Contrato |
|---|---|---|
| POST | /api/v1/categories | JWT; name/kind; 201 com Location/ETag; sem Idempotency-Key; 400/401/409/429/503 |
| GET | /api/v1/categories | JWT; kind/isActive opcionais; page=1, pageSize=20 (máximo 100); sort=name ou id; direction=asc ou desc |
| GET | /api/v1/categories/{id} | JWT; 200 com ETag forte; alheia/inexistente retorna o mesmo 404 |

Kind é exatamente income ou expense. Name preserva Trim/NFC; NormalizedName usa Trim/NFC/ToUpperInvariant/NFC, com ambos limitados a 100 unidades UTF-16 após normalização. Café/CAFÉ conflitam; Cafe/Café são distintos. O índice único SQL por proprietário, tipo e nome normalizado usa comparação binária e inclui inativas. Campos internos/controlados pelo servidor são rejeitados no JSON. A Entrega B original limitou-se à criação e consulta, sem replay; edição/atividade estão na Entrega C abaixo. Exclusão continua indisponível.

A migration incremental é `20261114000200_CategoriesDeliveryB`. Provisionamento atualizado concede somente SELECT/INSERT em Categories, após aplicação explícita da migration com credencial separada. Nada disso foi executado no banco persistente pelo Codex. Readiness passa a exigir a nova tabela e acesso de leitura; liveness permanece independente. Veja [modelo, testes e procedimento externo](docs/etapa-4/entrega-b-validacao.md).

## Edição e atividade — Entrega C

| Método | Rota | Corpo permitido |
|---|---|---|
| PUT | /api/v1/accounts/{id} | name, type |
| PATCH | /api/v1/accounts/{id} | isActive obrigatório |
| PUT | /api/v1/categories/{id} | name |
| PATCH | /api/v1/categories/{id} | isActive obrigatório |

JWT e um If-Match forte são obrigatórios: use o ETag recebido no GET ou na última alteração, incluindo as aspas. O token deve ser Base64 canônico de oito bytes. Ausente retorna 428; vazio, fraco, wildcard, lista ou formato inválido retorna 400; versão antiga retorna 412; alheio/inexistente retorna o mesmo 404; nome de categoria duplicado retorna 409. Sucesso retorna 204 sem JSON, novo ETag e Cache-Control: no-store. Não há Idempotency-Key nessas operações.

Contas inativas podem ser editadas; desativar não exige saldo zero. Categoria mantém o tipo e reserva seu nome mesmo inativa. Proprietário, dados de abertura, moeda, tipo da categoria e CreatedAtUtc permanecem imutáveis. UpdatedAtUtc vem do TimeProvider, em UTC. Comandos que repetem os valores também executam uma gravação condicionada e avançam RowVersion, mesmo com relógio fixo; não há aceitação de versão antiga por ausência de mudança de valores.

Não há nova migration. O provisionamento passa a permitir UPDATE por coluna: FinancialAccounts (Name, Type, IsActive, UpdatedAtUtc) e Categories (Name, NormalizedName, IsActive, UpdatedAtUtc). A API mantém SELECT/INSERT e não recebe UPDATE financeiro amplo, DELETE, DDL ou UPDATE em IdempotencyRecords. O provisionamento desses grants no banco persistente não foi executado pelo Codex e depende de autorização específica. O snapshot idempotente de criação de contas não é modificado. Testes, limitações e comandos em [entrega-c-validacao](docs/etapa-4/entrega-c-validacao.md).

## Transações e saldos — Entrega 5A local

| Método | Rota | Contrato |
|---|---|---|
| POST | /api/v1/transactions | JWT e Idempotency-Key; 201, Location, ETag e snapshot preservado em replay |
| GET | /api/v1/transactions | JWT; paginação e filtros accountId/categoryId/kind/from/to/minAmount/maxAmount |
| GET | /api/v1/transactions/{id} | JWT; recurso próprio e ETag; alheio/inexistente retorna 404 |
| GET | /api/v1/accounts/{id}/balance | JWT; asOf opcional, inclusivo e sem futuro; accountId/currency/asOf/balance |

POST aceita somente accountId, categoryId, kind (income/expense), amount positivo com centavos exatos, occurredOn e description opcional até 500 caracteres. Conta e categoria devem ser próprias e ativas; a categoria deve ter o mesmo tipo. A data vai da abertura da conta até hoje em America/Sao_Paulo. A transação SQL RepeatableRead mantém protegidas as leituras de atividade até a gravação e o commit do snapshot idempotente. Vínculo inativo: 409; referência alheia/inexistente: 404; valor/data/tipo incompatível: 400. O escopo original de 5A não incluía edição/exclusão; esses endpoints estão em 5B abaixo.

As listas usam page=1/pageSize=20 (máximo 100), sort=occurredOn/amount/id e direction=asc/desc, com padrão occurredOn desc e desempate por Id. Datas são YYYY-MM-DD; filtros monetários são não negativos, limitados e com centavos exatos, inclusive na query string. Respostas financeiras usam no-store.

O saldo parte de OpeningBalance + receitas válidas - despesas válidas e, desde a 6A, inclui transferências recebidas menos enviadas, até o corte inclusivo. Retroativos recalculam o histórico; antes da abertura retorna zero. Contas inativas continuam incluídas. Não há CurrentBalance persistido. A migration da 5A é `20261117000100_TransactionsDelivery5A`; a nova migration de transferências está descrita abaixo. Ambas exigem aplicação e provisionamento explícitos/autorizados em ambiente persistente. Nada foi aplicado ao banco persistente nesta implementação. Consulte [histórico de 5A](docs/etapa-5/entrega-5a-validacao.md) e [validação de 6A](docs/etapa-6/entrega-6a-validacao.md).

## Edição e exclusão de transações — Entrega 5B local

| Método | Rota | Contrato |
|---|---|---|
| PUT | /api/v1/transactions/{id} | Substitui accountId/categoryId/kind/amount/occurredOn/description; JWT e If-Match |
| DELETE | /api/v1/transactions/{id} | Sem corpo; exclusão lógica própria com JWT e If-Match |

Sucesso: 204 vazio, novo ETag e no-store. If-Match ausente: 428; malformado: 400; versão antiga em recurso vivo: 412; recurso alheio, inexistente ou excluído: 404. Exclusões repetidas com header válido retornam 404. PUT sem mudanças continua executando UPDATE condicional, inclusive com relógio fixo.

Correções exclusivamente descritivas são permitidas com conta/categoria históricas inativas. Qualquer mudança financeira ou reassociação exige conta original e destino ativas; categoria nova deve estar ativa. Categoria histórica inalterada pode permanecer inativa. O PUT valida categoria compatível, valor positivo/centavos exatos e data entre abertura do destino e hoje financeiro. Description é opcional, aceita null e tem limite de 500 caracteres. DELETE independe da atividade e preserva os dados financeiros.

O UPDATE utiliza a rowversion enviada pelo cliente no predicado SQL. A leitura inicial é otimista; o PUT protege as referências dentro de RepeatableRead até o commit, evitando conversões de shared lock na linha da transação entre dois editores. DELETE não consulta atividade. As duas operações têm rollback local e não fazem retry em conflitos. Saldos e cortes históricos usam os valores atuais e ignoram excluídas. O POST e seu snapshot idempotente permanecem originais após edição/exclusão.

Não há nova migration: schema e quatro migrations históricas permanecem intactos. O provisionamento passa a conceder UPDATE somente em AccountId, CategoryId, Kind, Amount, OccurredOn, Description, UpdatedAtUtc, IsDeleted e DeletedAtUtc de FinancialTransactions. Id/UserId/CreatedAtUtc/RowVersion não recebem UPDATE; DELETE físico/DDL e UPDATE em IdempotencyRecords continuam proibidos. Aplicação desse provisionamento em banco persistente depende de autorização específica e não foi executada. Detalhes e comandos de teste em [validação de 5B](docs/etapa-5/entrega-5b-validacao.md).

## Transferências — 6A publicada e 6B local

| Método | Rota | Contrato |
|---|---|---|
| POST | /api/v1/transfers | JWT e Idempotency-Key; sourceAccountId/destinationAccountId/amount/occurredOn/description opcional; 201/Location/ETag |
| GET | /api/v1/transfers/{id} | JWT; recurso próprio, 200/ETag; alheio/inexistente retorna 404 |
| DELETE | /api/v1/transfers/{id} | JWT e If-Match; sem corpo; cancelamento lógico, 204 vazio/novo ETag/no-store |

Na criação, as duas contas precisam ser próprias, distintas, ativas e BRL, com abertura até a data da transferência; data futura e frações de centavo são rejeitadas. Uma Transfer gera dois efeitos simétricos sem criar FinancialTransactions. Saldo negativo é permitido. O saldo existente inclui entradas menos saídas de transferências válidas até o corte, sem saldo materializado. Não há categoria, listagem de transferências, edição, extrato ou relatório nesta entrega.

O cancelamento da 6B marca a linha original, com timestamps UTC e versão SQL, e retira ambos os efeitos dos saldos atuais/históricos afetados. Permanece permitido com contas inativas e saldo negativo. GET após cancelamento retorna 404; replay do POST original mantém 201/corpo/Location/ETag/Content-Type sem reativar. A 6B não exige migration nova; exige provisionamento explícito, quando autorizado, de UPDATE somente em IsCanceled/CanceledAtUtc/UpdatedAtUtc. Nenhuma alteração foi aplicada a banco persistente.

Criação e snapshot são confirmados juntos sob RepeatableRead, com contas lidas em ordem por Id. A operação idempotente é transfers.create.v1; replay preserva status/corpo/Location/ETag/Content-Type originais, inclusive após desativação. Nova migration: **20261120000100_TransfersDelivery6A**, com FKs compostas, CHECKs e rowversion. O uso em banco persistente exige migration e provisionamento explícitos/autorizados após reconstruir a imagem; nada disso foi executado nesta implementação. Readiness passa a verificar a nova tabela. Veja [testes, limites e validação externa](docs/etapa-6/entrega-6a-validacao.md).

## Endpoints de identidade

| Método | Rota sob /api/v1 | Corpo / acesso |
|---|---|---|
| POST | /auth/register | displayName, email, password; anônimo; 201/400/409/429 |
| POST | /auth/login | email, password; anônimo; 200/400/401/429 |
| POST | /auth/logout | Bearer; 204; revoga todas as sessões anteriores |
| GET | /users/me | Bearer; perfil próprio, sem hashes ou flags internas |
| PATCH | /users/me | Bearer; apenas displayName; 204 |
| POST | /users/me/change-password | Bearer; currentPassword, newPassword; 204 |
| DELETE | /users/me | Bearer e JSON currentPassword; desativação lógica; 204 |

Senha: 12–128 caracteres; nome obrigatório até 100; e-mail normalizado único também no banco. Use somente dados fictícios. Exemplos com senha devem ser enviados por arquivo temporário ignorado ou entrada padrão; não coloque credenciais reais no histórico do terminal. DELETE com JSON foi validado na suíte SQL e no fluxo HTTPS externos; clientes/proxies adicionais precisam de verificação própria.

JWT HS256 expira em 15 minutos, valida algoritmo, assinatura, issuer, audience, exp e nbf. A versão e a atividade do usuário são consultadas em cada requisição autenticada, sem cache. Alteração de senha, logout e desativação revogam tokens anteriores. Login e alterações de segurança serializam a linha do usuário no SQL; não se emite uma versão nova para uma senha antiga. Requisições já autenticadas antes de uma revogação podem estar em execução; a revogação vale para validações subsequentes.

Não há refresh token, sessões por dispositivo, recuperação/confirmação de e-mail, MFA ou OAuth/OIDC. Mensagens de login são genéricas; a inferência por conflito no cadastro é uma limitação documentada do MVP. Limites iniciais: login 5/minuto/IP, cadastro 3/minuto/IP, alteração de senha 5/15 minutos/usuário e 120/minuto/usuário autenticado; 429 inclui Retry-After. Identity bloqueia por 15 minutos após cinco falhas de senha. CORS permanece sem origens liberadas e cabeçalhos de proxy não são confiados automaticamente.

## Ambientes e configuração

appsettings.json contém configurações não sensíveis; appsettings.Development.json ajusta logs. Variáveis de ambiente podem sobrescrever valores, usando `__` como separador: `OpenApi__Title` define o título da documentação e é validado na inicialização. `ASPNETCORE_ENVIRONMENT` define o ambiente; fora de Development, OpenAPI e Swagger não são expostos.

Para executar sem o perfil de desenvolvimento:

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Production'
dotnet run --project src/FinanceFlow.Api --no-launch-profile --urls https://localhost:5443
```

AllowedHosts limita os nomes de host locais; outra hospedagem deverá configurá-lo explicitamente. Não habilitamos CORS amplo nem adicionamos credenciais de exemplo. O perfil HTTP é local e só expõe endpoints técnicos sem credenciais; HTTPS e configuração do proxy são obrigatórios antes de expor autenticação ou dados sensíveis. Não existe terminação TLS produtiva nesta etapa.

Segredos devem ser fornecidos por mecanismo apropriado ao ambiente, nunca pelo Git. `.env`, certificados, chaves, logs e configuração local são ignorados. Desde a Etapa 3, JWT e conexão SQL são configurações obrigatórias; a indisponibilidade do SQL é refletida no readiness, sem migration automática ou falha indevida do liveness.

O framework registra Data Protection com a autenticação, mas as funcionalidades atuais não consomem seu key ring: não há cookies, sessões ou tokens de recuperação de conta. Os avisos de chaves não persistentes/sem criptografia em repouso permanecem documentados, sem supressão; persistência protegida será obrigatória antes de adotar um consumidor. O aviso de HTTP_PORTS sobrescrito é esperado: URLS configura HTTPS em 8443. Detalhes na [revisão técnica](docs/etapa-3/validacao.md#revisão-técnica-final-antes-do-commit).

## Arquitetura

```text
FinanceFlow.slnx
src/
  FinanceFlow.Api/              HTTP, controllers, erros, OpenAPI e composição
  FinanceFlow.Application/      Casos de uso financeiros, proprietário e contratos de identidade
  FinanceFlow.Domain/           Contas, categorias, transações, transferências e invariantes
  FinanceFlow.Infrastructure/   Identity, EF Core, JWT e operações explícitas de banco
tests/
  FinanceFlow.Domain.Tests/
  FinanceFlow.Application.Tests/
  FinanceFlow.IntegrationTests/
docs/
  etapa-1/                     Planejamento aprovado e sete ADRs
  etapa-2/                     Implementação e evidências da fundação
  etapa-3/                     Identidade, evidências externas e revisão final
  etapa-4/                     Entregas A/B, refatoração e evidências de validação
  etapa-5/                     Etapa concluída; histórico e evidências de 5A/5B/5C
  etapa-6/                     Criação de transferências, saldo integrado e validação de 6A
```

Referências: Api → Application/Infrastructure; Application → Domain; Infrastructure → Application/Domain; Domain sem referências de projeto ou pacotes de infraestrutura. Os projetos sem funcionalidades permanecem sem classes artificiais. Não há CQRS completo, MediatR, repositório genérico ou mapeamento automático.

## Erros e testes

Tratamento global de exceções e Problem Details em todos os ambientes. Respostas de erro incluem status, title, instance, code e traceId; validação acrescenta errors. Stack traces e mensagens internas de exceção não são retornadas ao cliente. Erros são registrados no servidor; não registrar credenciais/corpos sensíveis quando as próximas funcionalidades forem adicionadas.

Os testes usam xUnit e Microsoft.Testing.Platform no .NET 10. Domain.Tests cobre contas, categorias, transações e transferências; Application.Tests cobre identidade e casos de uso financeiros. Nenhum projeto tolera mais código 8 por ausência de testes. IntegrationTests preserva a fundação/identidade e acrescenta contas/categorias/transações/transferências/saldos, parsing exato, normalização, modelo/migrations, idempotência e SQL Server real com Testcontainers.MsSql. Não há EF InMemory ou SQLite. Fixtures geram credenciais efêmeras e bancos isolados; indisponibilidade do Docker falha os testes obrigatórios, sem skip/fallback. Controllers técnicos de falha existem somente no assembly de teste.

O workflow em .github/workflows/build.yml verifica Docker e executa restore, build e toda a suíte, incluindo SQL. Não exige segredos reais da máquina do runner. Conforme evidência fornecida pelo usuário, a CI da Etapa 3 foi aprovada e a Entrega A publicada em `5a13762` teve **Success** na execução `43559125753`. A refatoração publicada em `9c938c8` também teve CI `43564544104` aprovada, conforme confirmação do usuário. O usuário confirmou também a publicação de B em `64b32d3` e a CI `43642389840 — Success`. Nenhuma dessas execuções comprova a Entrega C local; seu workflow não foi disparado pelo Codex.

## Próximos passos

A publicação de 5A/5B/5C e os workflows #7/#8/#9 foram confirmados pelo usuário, concluindo a Etapa 5 com 477 testes aprovados. A **6A foi publicada em 6050035 com 557/557 e CI 44857771237 aprovada**. A **6B foi validada externamente com 613/613 e aprovada na auditoria final para commit**, incluindo a correção P2. Publicação autorizada e aprovação da CI da 6B continuam pendentes. Repetir a auditoria NuGet com índice acessível, pois o histórico NU1900 não permite conclusão de segurança. Sem stage, commit ou push pelo Codex; 6C e Etapa 7 não iniciadas e Etapa 6 ainda não concluída. Veja [correção e evidências de 6B](docs/etapa-6/entrega-6b-validacao.md), [roadmap](docs/etapa-1/backlog-e-roadmap.md) e [contratos](docs/etapa-1/contratos-rest.md).

Licença MIT é a proposta registrada para a preparação da release; nenhum arquivo de licença foi incluído nesta etapa. Resultados de testes e prontidão devem ser avaliados pelas evidências, sem badges de sucesso não verificados.
