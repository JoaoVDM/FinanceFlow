# FinanceFlow API

API REST de gestão financeira pessoal desenvolvida como portfólio profissional de backend em C# e ASP.NET Core. A evolução prioriza regras financeiras confiáveis, isolamento entre usuários e testes automatizados.

## Estado do projeto

Etapas 1, 2 e 3 concluídas e publicadas. O usuário confirmou, após as últimas correções, build de sete projetos sem erros/warnings, 73 testes aprovados, 22 verificações HTTPS Development, três Production e GitHub Actions 43439337019 com Success no commit 29fe1c0. São evidências externas, registradas em [docs/etapa-3](docs/etapa-3/validacao.md).

A Entrega A da Etapa 4 está concluída e validada externamente pelo usuário: 151 testes aprovados, build de sete projetos sem erros/warnings, migration aplicada, permissões verificadas e API Docker healthy. Os scripts HTTPS de identidade (22 verificações) e contas passaram; em Production, readiness respondeu 200 e OpenAPI/Swagger 404. A Entrega A foi publicada no commit `5a13762`; o usuário confirmou GitHub Actions `43559125753` com **Success**. Essas são evidências externas do commit publicado, anteriores à [refatoração de legibilidade](docs/etapa-4/refatoracao-legibilidade.md), cuja validação é registrada separadamente. A Etapa 4 completa depende de B/C, ainda não iniciadas. Veja [escopo](docs/etapa-4/README.md) e [evidências externas e histórico do sandbox](docs/etapa-4/entrega-a-validacao.md). Esta API é um portfólio, sem prontidão operacional de produto financeiro público.

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

O SQL usa um volume nomeado e porta `127.0.0.1:14333`; a API usa o host interno `sqlserver`, nunca sa. Há três identidades separadas: administrador apenas no provisionamento, FinanceFlowMigrator para DDL e FinanceFlowApi para SELECT/INSERT/UPDATE em AspNetUsers, SELECT em __EFMigrationsHistory e SELECT/INSERT em FinancialAccounts/IdempotencyRecords. A API não recebe UPDATE financeiro, DELETE, DDL ou credenciais administrativas. O provisionamento rejeita privilégios excessivos, sem remover permissões existentes silenciosamente.

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

Entrada de criação: name, type, currency, openingBalance e openingDate. Tipos estritos: checking, savings, cash, other; somente BRL; saldo inicial entre -999999999999.99 e 999999999999.99 com centavos exatos; abertura não futura no calendário America/Sao_Paulo. Valores JSON não são convertidos via double/float, e precisão excessiva não pode apagar frações. Usuário e timestamps não são aceitos no corpo. Contas inativas são incluídas por padrão. Não há PUT/PATCH, categorias ou saldo atual nesta entrega.

Na preparação de um ambiente, aplique explicitamente a migration incremental 20261114000100_AccountsDeliveryA e repita o provisionamento para conceder SELECT/INSERT nas novas tabelas. A API não recebe UPDATE/DELETE nelas e não executa migrations no startup. O usuário confirmou essa sequência e o fluxo HTTPS de `./scripts/Test-LocalAccounts.ps1` no fechamento da Entrega A; o Codex não executou o [procedimento externo](docs/etapa-4/entrega-a-validacao.md#validação-externa) no banco persistente. O script preserva os registros e desativa somente o usuário fictício que criou, sem imprimir tokens/senhas.

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
  FinanceFlow.Application/      Casos de uso de contas, proprietário e contratos de identidade
  FinanceFlow.Domain/           FinancialAccount e invariantes financeiras
  FinanceFlow.Infrastructure/   Identity, EF Core, JWT e operações explícitas de banco
tests/
  FinanceFlow.Domain.Tests/
  FinanceFlow.Application.Tests/
  FinanceFlow.IntegrationTests/
docs/
  etapa-1/                     Planejamento aprovado e sete ADRs
  etapa-2/                     Implementação e evidências da fundação
  etapa-3/                     Identidade, evidências externas e revisão final
  etapa-4/                     Entrega A: contas, idempotência e evidências de validação
```

Referências: Api → Application/Infrastructure; Application → Domain; Infrastructure → Application/Domain; Domain sem referências de projeto ou pacotes de infraestrutura. Os projetos sem funcionalidades permanecem sem classes artificiais. Não há CQRS completo, MediatR, repositório genérico ou mapeamento automático.

## Erros e testes

Tratamento global de exceções e Problem Details em todos os ambientes. Respostas de erro incluem status, title, instance, code e traceId; validação acrescenta errors. Stack traces e mensagens internas de exceção não são retornadas ao cliente. Erros são registrados no servidor; não registrar credenciais/corpos sensíveis quando as próximas funcionalidades forem adicionadas.

Os testes usam xUnit e Microsoft.Testing.Platform no .NET 10. Domain.Tests cobre contas; Application.Tests cobre identidade e casos de uso financeiros. Nenhum projeto tolera mais código 8 por ausência de testes. IntegrationTests preserva a fundação/identidade e acrescenta contas, parsing exato, modelo/migration, idempotência e SQL Server real com Testcontainers.MsSql. Não há EF InMemory ou SQLite. Fixtures geram credenciais efêmeras e bancos isolados; indisponibilidade do Docker falha os testes obrigatórios, sem skip/fallback. Controllers técnicos de falha existem somente no assembly de teste.

O workflow em .github/workflows/build.yml verifica Docker e executa restore, build e toda a suíte, incluindo SQL. Não exige segredos reais da máquina do runner. Conforme evidência fornecida pelo usuário, a CI da Etapa 3 foi aprovada e a Entrega A publicada em `5a13762` teve **Success** na execução `43559125753`. Isso não representa validação remota das alterações posteriores de legibilidade.

## Próximos passos

Revisar a manutenção de legibilidade antes de preparar um commit autorizado. Em 14/11/2026, o usuário confirmou externamente 151 testes aprovados, incluindo integração SQL com Testcontainers, e auditoria posterior sem pacotes vulneráveis nos sete projetos. Os três avisos NU1900 observados durante os testes permanecem registrados; não foi informado novo build sem esses avisos. A publicação e a CI da Entrega A já foram confirmadas pelo usuário; as evidências da manutenção estão [registradas separadamente](docs/etapa-4/refatoracao-legibilidade.md), sem novo resultado de CI para esta refatoração. As Entregas B/C e a Etapa 5 não foram iniciadas. Normalização de categorias já aprovada (Trim/NFC/ToUpperInvariant, acentos significativos e comparação binária) está registrada para B, sem entidade ou endpoints antecipados. Veja [roadmap](docs/etapa-1/backlog-e-roadmap.md) e [contratos](docs/etapa-1/contratos-rest.md).

Licença MIT é a proposta registrada para a preparação da release; nenhum arquivo de licença foi incluído nesta etapa. Resultados de testes e prontidão devem ser avaliados pelas evidências, sem badges de sucesso não verificados.
