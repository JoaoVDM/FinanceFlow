# Etapa 3 — Identidade, autenticação e persistência

Status: **Etapa 3 implementada e validada funcionalmente no PowerShell externo, conforme resultados comprovados e informados pelo usuário**. A revisão final corrigiu CS8602 e a ordenação do readiness; seu build local passou sem avisos. A repetição dos testes SQL e HTTP depois dessas duas correções permanece pendente antes do commit, pois o sandbox continua sem acesso ao Docker Engine. As evidências externas e as verificações do Codex são discriminadas abaixo; os bloqueios anteriores foram preservados como histórico.

## Validação externa informada pelo usuário

Esta rodada ocorreu **antes das duas correções da revisão final**. O usuário confirmou os resultados abaixo; não são apresentados como comandos executados pelo Codex nem como uma nova execução remota de CI.

| Verificação externa | Resultado informado |
|---|---|
| Restore | Aprovado |
| Auditoria NuGet | Nenhum pacote vulnerável identificado |
| Build Release | Sete projetos; 0 erros; 1 warning CS8602 na fixture SQL |
| Testes automatizados | **73 aprovados, 0 falhas, 0 ignorados** |
| Docker Compose / imagem API | Build concluído |
| SQL Server | Healthy |
| Provisionamento e migration inicial | Executados com sucesso |
| Permissões SQL da API | Verificação aprovada |
| HTTPS Development | **22 verificações concluídas** |
| HTTPS Production | /health/ready 200; /openapi/v1.json 404; /swagger/index.html 404 |
| Encerramento | API temporária Production encerrada; somente SQL Server permanece executando, segundo o usuário |

Esses resultados substituem as pendências funcionais da primeira implementação. A correção NuGet também foi confirmada externamente pela auditoria limpa; nesta revisão, o Codex conseguiu conferir no grafo local SqlClient 6.1.8 → Pkcs 9.0.21. A aprovação externa não dispensa repetir a regressão após alterações posteriores.

## Revisão técnica final antes do commit

### CS8602 na fixture SQL

A expressão parameters.HostConfig.PortBindings["1433/tcp"] encadeava referências potencialmente nulas da API Docker, sem validar sua presença. IdentitySqlFixture agora exige HostConfig, o mapa de portas, a entrada 1433/tcp, uma lista não vazia e bindings não nulos. Ausência de qualquer componente encerra a configuração com InvalidOperationException; nenhum cenário libera o bind implicitamente para todas as interfaces. HostIP continua 127.0.0.1. Não foram adicionados operadores !, supressões ou alterações no analisador. O build local desta revisão confirmou zero warnings.

### Skip/Take sem OrderBy

A busca em src/tests encontrou somente Take(1) no readiness de HealthController. A linha escolhida era indeterminada, mas não havia efeito no conteúdo da resposta: o resultado é descartado e a projeção serve para conferir acesso às colunas necessárias. Foi acrescentado OrderBy(x => x.Id), uma chave única, antes da projeção e do Take. O limite de uma linha, as colunas verificadas e os retornos 200/503 foram preservados. Não foi usado Any como substituição, pois isso poderia deixar de selecionar as colunas cuja disponibilidade se pretende conferir. A execução SQL após a mudança ainda precisa da rodada externa; o warning não foi ocultado.

### Data Protection no contêiner

[AddAuthentication registra Data Protection no framework](https://github.com/dotnet/aspnetcore/blob/v10.0.15/src/Security/Authentication/Core/src/AuthenticationServiceCollectionExtensions.cs). A revisão dos consumidores atuais não encontrou cookies, antiforgery, sessões ou tokens de recuperação/confirmação do Identity: não há uso funcional do seu key ring nesta etapa. JWT usa a chave HS256 externa; o hasher de senhas e o certificado TLS são mecanismos separados.

Os avisos descrevem chaves em armazenamento não persistente do contêiner e ausência de criptografia XML em repouso. **Nenhuma configuração foi alterada para silenciá-los**: não se adicionou um volume com chaves em claro, um provider efêmero ou filtros de log. Não há payload protegido atual que necessite sobreviver à recriação do contêiner. Antes de introduzir um consumidor, será necessário [configurar persistência protegida e criptografia das chaves](https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/configuration/overview?view=aspnetcore-10.0). Este registro não equivale a uma configuração de Data Protection pronta para uso público.

### HTTP_PORTS sobrescrito por URLS

A imagem base fornece HTTP_PORTS; o Dockerfile define ASPNETCORE_URLS=https://+:8443. [URLS tem precedência sobre HTTP_PORTS/HTTPS_PORTS](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel/endpoints?view=aspnetcore-10.0#specify-ports-only). O aviso comunica essa precedência esperada e não é falha de TLS. Compose fornece o PFX e publica 127.0.0.1:5443:8443; as verificações HTTPS externas Development/Production passaram. Dockerfile, Compose e configuração de portas foram preservados. Não se habilitou HTTP nem se suprimiu o aviso.

### Verificações executadas pelo Codex após as correções

| Verificação local | Resultado real |
|---|---|
| dotnet restore FinanceFlow.slnx --configfile NuGet.Config | **Saída 0**, todos os projetos atualizados para restauração, usando os pacotes já disponíveis |
| dotnet build FinanceFlow.slnx --configuration Release --no-restore | **Saída 0; sete projetos, 0 avisos, 0 erros**, 13,12s |
| dotnet test FinanceFlow.slnx --configuration Release --no-restore | **Saída 2; 73 casos, 46 aprovados, 27 falhas, 0 ignorados**, 30s 388ms |
| Causa das 27 falhas locais | Fixture SQL não inicializou: DockerUnavailableException → UnauthorizedAccessException no pipe docker_engine; os casos SQL não comprovaram as asserções |
| Executor | Microsoft.Testing.Platform funcionou; o antigo diagnóstico de imports incompletos não se repetiu |
| dotnet nuget why FinanceFlow.slnx System.Security.Cryptography.Pkcs | **Saída 0**, SqlClient 6.1.8 → Pkcs 9.0.21 |
| dotnet list FinanceFlow.slnx package --vulnerable --include-transitive --no-restore | Saída 1, acesso ao índice api.nuget.org bloqueado; não é uma auditoria local limpa |
| Revisão de Git / segredos | git diff --check sem erros; 14 arquivos modificados e 38 novos, todos dentro do escopo da Etapa 3; nenhum caminho sensível/temporário, valor dos cinco segredos locais ou bloco de chave privada nos arquivos visíveis ao Git |

Logs ignorados em .local/stage-3-final-review/: restore.log, build.log, test.log, audit.log e why-pkcs.log. Os 46 casos aprovados são o resultado do Codex nesta revisão; os 73 aprovados pertencem à rodada externa anterior. Os avisos de Data Protection do host de teste não foram suprimidos. Nenhum contêiner de desenvolvimento, volume ou banco foi encerrado, removido ou reprovisionado nesta revisão.

Arquivos alterados nesta revisão: IdentitySqlFixture.cs, HealthController.cs, README.md e este documento. A implementação restante e os ADRs foram preservados.

### Revalidação e preparação do commit

No PowerShell externo, interrompa ao primeiro comando com saída diferente de zero:

```powershell
Set-Location C:\FinanceFlow
function Assert-FinalReviewStep([string]$Step) {
    if ($LASTEXITCODE -ne 0) { throw "$Step falhou; interrompa e preserve o resultado." }
}
dotnet restore FinanceFlow.slnx --configfile NuGet.Config
Assert-FinalReviewStep 'Restore'
dotnet build FinanceFlow.slnx --configuration Release --no-restore
Assert-FinalReviewStep 'Build'
dotnet test FinanceFlow.slnx --configuration Release --no-restore
Assert-FinalReviewStep 'Testes'
dotnet list FinanceFlow.slnx package --vulnerable --include-transitive --no-restore
Assert-FinalReviewStep 'Auditoria'
docker compose build api
Assert-FinalReviewStep 'Imagem API'
docker compose up -d api
Assert-FinalReviewStep 'Inicialização da API'
./scripts/Test-LocalApi.ps1
docker compose logs --tail 80 api
# Ao terminar, encerrar somente a API deste projeto, preservando SQL e o volume:
docker compose stop api
```

Exigir 0 avisos de compilação, 73 testes aprovados e as 22 verificações HTTPS novamente concluídas. Conferir que o aviso EF sobre limitação sem ordenação desapareceu, inclusive em readiness. Avisos documentados de Data Protection e precedência de portas podem permanecer. Esses comandos não precisam reaplicar migrations nem provisionar um banco que já foi validado. Os testes SQL criam seu próprio ambiente descartável e não devem usar fallback para o banco de desenvolvimento.

Antes do commit autorizado pelo usuário, repetir git diff --check, conferir arquivos novos com git ls-files --others --exclude-standard e manter .env, certificados, caches, logs e artefatos fora do Git. A CI nova ainda não foi executada remotamente. Não há requisito de publicação para validar localmente e não foi feito commit/push. A Etapa 4 não foi iniciada.

## Escopo e arquitetura

- Domain permanece sem EF Core, Identity, ASP.NET ou entidades financeiras.
- Application define IUserAccounts, IUserTokenState, entradas/saídas dos casos de uso e regras de nome, e-mail e senha.
- Infrastructure implementa o adaptador de casos de identidade com UserManager, transações SQL, verificação de credenciais do framework, EF Core e emissor JWT. O acoplamento a Identity fica nessa implementação, sem uma camada de serviços que apenas repasse chamadas.
- Api recebe DTOs explícitos, aplica autorização, validação HTTP, limites, documentação e composição. Controllers de negócio não acessam DbContext; o health controller utiliza o contexto somente para readiness técnico.
- Sem Repository genérico, MediatR, AutoMapper, entidades financeiras, refresh token, OAuth/OIDC ou Etapa 4.

O estado inicial do workspace foi conferido em C:\FinanceFlow, branch main sincronizada com origin/main e sem alterações locais. Os documentos da Etapa 1 foram consultados; suas decisões não foram substituídas.

## Arquivos preparados

| Grupo | Arquivos/componentes |
|---|---|
| Dependências | Directory.Packages.props; projetos Api, Infrastructure e IntegrationTests |
| Application | Users/IUserAccounts.cs e AccountInputRules.cs |
| Infraestrutura | DependencyInjection, ApplicationUser, UserAccounts, UserTokenState, UnknownCredentialVerifier, PasswordLengthValidator |
| Configuração/JWT | DatabaseOptions/validator, JwtOptions/validator e AccessTokenIssuer |
| Persistência | FinanceFlowDbContext, InitialIdentity, metadata congelada e ModelSnapshot, DatabaseTasks, ReadinessProbe |
| API | AuthController, UsersController, DTOs, AccountControllerBase, configuração JWT/rate limits, Program, health e erros |
| Ambiente | Dockerfile, .dockerignore, compose.yaml, .env.example e scripts locais de configuração/validação |
| Testes | Regras da Application; configuração/JWT/rate limiting; fixture SQL; identidade/concorrência; ajustes do host da fundação |
| Operação/docs | Workflow, README principal e este registro |

Os arquivos de secrets locais gerados não pertencem ao Git. Não foram criados commits, feito push, alteradas permissões NTFS ou configurações de segurança do Codex/Windows.

## Identidade e regras implementadas no código

ApplicationUser usa Guid, DisplayName até 100 caracteres, IsActive, TokenVersion bigint, CreatedAtUtc, UpdatedAtUtc e DeactivatedAtUtc. E-mail é requerido, normalizado pelo Identity e único no banco; há CHECKs de nome não vazio e versão não negativa. A senha é validada em 12–128 caracteres, inclusive por validator do Identity, sem truncamento ou hash manual.

FinanceFlowDbContext é Scoped e mantém o schema relacional Identity v1 suportado, sem passkeys ou funcionalidade de roles exposta. O modelo convencional inclui as tabelas internas do Identity. Tamanhos de chaves de login/token são fixados em 128 para consistência entre runtime e ferramenta de migrations.

Login incorreto usa mensagem genérica e AccessFailed do Identity. Após cinco falhas, bloqueio de 15 minutos. E-mail desconhecido passa por verificação de hash do framework contra hash fictício gerado aleatoriamente para reduzir diferença de custo. Isso não constitui garantia de tempo idêntico em todos os caminhos.

### JWT e concorrência

HS256, segredo Base64 externo com no mínimo 32 bytes decodificados, emissor e audiência obrigatórios. A validação de tamanho não prova entropia: o script e as fixtures usam RandomNumberGenerator. Claims sub, jti, iat, nbf, exp e tokenVersion; validade fixa de 900 segundos e clock skew de 30 segundos. Algoritmo, assinatura, issuer, audience, exp e nbf são validados explicitamente.

IUserTokenState consulta SQL sem tracking/cache em toda requisição autenticada. IsActive falso, versão divergente ou falha na consulta negam acesso. 401 mantém WWW-Authenticate: Bearer, sem detalhes internos do validador. 403 e 429 usam Problem Details; 429 inclui Retry-After.

Login e mutações de segurança leem o usuário sob UPDLOCK/ROWLOCK dentro de transação. O emissor usa a versão associada à senha efetivamente validada. Se login vencer a corrida, o token antigo será revogado pela alteração seguinte; se a senha vencer, o login antigo falha. Não se relê uma versão nova depois de validar uma senha antiga. Senha, logout e desativação incrementam TokenVersion atomicamente. O cenário concorrente usa SQL real, bloqueio externo e interceptor observador, sem resultados de banco simulados ou sleeps arbitrários.

Revogação afeta validações subsequentes; requisições já em execução não são abortadas retroativamente. A suíte SQL, incluindo concorrência, integra os 73 testes externos aprovados informados pelo usuário; a repetição após os ajustes da revisão final permanece necessária.

O login resolve inicialmente o Id pelo e-mail imutável e adquire o lock pela chave primária, na mesma ordem dos demais comandos. A consulta inicial não valida a senha nem captura uma versão para emissão. Isso evita ordenar locks de índice de e-mail e chave primária de formas opostas durante a corrida.

## Contratos e segurança HTTP

| Método | Endpoint | Resultado previsto |
|---|---|---|
| POST | /api/v1/auth/register | 201 com Location; 400/409/429 |
| POST | /api/v1/auth/login | 200 com accessToken/tokenType/expiresIn; 400/401/429 |
| POST | /api/v1/auth/logout | 204, revogação global |
| GET | /api/v1/users/me | 200, perfil do subject |
| PATCH | /api/v1/users/me | 204, alteração somente de DisplayName |
| POST | /api/v1/users/me/change-password | 204, nova senha e revogação |
| DELETE | /api/v1/users/me | 204, confirmação da senha e desativação lógica |

Somente register/login e endpoints técnicos são anônimos. Controllers têm autorização padrão; proprietário nunca vem do corpo. Campos desconhecidos são rejeitados pela serialização para impedir mass assignment. Não se retorna PasswordHash, SecurityStamp, TokenVersion ou flags do Identity nos perfis. Não há exclusão física.

O contrato DELETE com JSON foi preservado com FromBody explícito. O teste usa HttpClient.SendAsync com método DELETE e JsonContent; a suíte SQL e o fluxo Kestrel foram aprovados na rodada externa informada pelo usuário. Não houve alteração silenciosa de rota; proxies/clientes futuros precisam de verificação adicional.

Credenciais sob /api exigem HTTPS, sem redirecionamento/replay de corpo. O perfil HTTP antigo mantém utilidade apenas para endpoints técnicos. CORS não libera origens e não são confiados X-Forwarded-* sem configuração. Corpos têm limite de 16 KiB. Rate limits: login 5/min/IP, cadastro 3/min/IP, senha 5/15 minutos/usuário (compartilhado entre alteração e confirmação para desativação) e global autenticado 120/min/usuário. A resposta 429 também pode ocorrer na desativação, preservando seu contrato de corpo e sucesso 204.

Logs da API usam console, sem dependência de Event Log do Windows. Erros inesperados registram apenas tipo e traceId; mensagens brutas do provider SQL são suprimidas para evitar valores sensíveis. Não há logging de corpos ou tokens. O handler mantém resposta JSON sanitizada mesmo quando Accept não é compatível, sem recorrer à página de exceção de desenvolvimento; há testes preparados para isso. Asserções que verificam ausência de senha/token usam condições booleanas para não imprimir esses valores se falharem. A documentação mantém Bearer e schemas dos DTOs, disponível apenas em Development.

## Migration e separação de credenciais

Migration versionada: **20261113000100_InitialIdentity**, sete tabelas internas do Identity, índices únicos e CHECKs. Foi escrita com metadata congelada/ModelSnapshot, aplicada explicitamente e validada no SQL na rodada externa informada pelo usuário. O teste exige que Database.HasPendingModelChanges() seja falso; não se desabilitou esse controle para mascarar divergência. O Codex não aplicou migrations ao banco de desenvolvimento nesta revisão.

Aplicação explícita: comando --migrate do executável, com Migration:ConnectionString. Startup normal nunca chama Migrate. --provision-database cria somente banco de nome FinanceFlow*, logins e usuários necessários, com parâmetros e identificadores delimitados. Não apaga banco/volume, não troca senha existente e rejeita mapeamento conflitante.

Contas distintas:

- Administrativa: somente provisionamento, não entregue ao serviço API.
- FinanceFlowMigrator: CREATE TABLE e DML/ALTER/REFERENCES no schema dbo para migrations.
- FinanceFlowApi: SELECT/INSERT/UPDATE em AspNetUsers; SELECT em __EFMigrationsHistory; sem DELETE ou DDL. As permissões de objeto entram no segundo provisionamento, após a migration.

O provisionamento conecta como API para rejeitar privilégios excessivos. Os testes verificam acesso necessário e ausência de CREATE TABLE, DELETE de usuários e UPDATE da history. O usuário confirmou o provisionamento e a verificação das permissões na rodada externa.

A conferência também rejeita participação em roles, propriedade de schemas, grants diretos fora da lista de objetos/operações necessários e WITH GRANT OPTION. Há caso SQL que adiciona DELETE somente em banco descartável de teste e exige sua rejeição, sem alterar o banco de desenvolvimento ou remover permissões automaticamente. Esse caso faz parte da suíte externa aprovada.

## Docker, TLS e health

Compose usa SQL Server 2022 Developer com volume nomeado, porta SQL restrita a loopback e API por HTTPS/loopback. A comunicação interna usa sqlserver. Provisionamento e migrations são serviços de ferramenta acionados explicitamente. API não herda segredos administrativos. Health do SQL usa consulta real; API usa probe HTTPS com certificado local fixado, nome/validade conferidos e readiness, sem aceitar certificados arbitrários.

Tag oficial consultada: [2022-CU22-GDR1-ubuntu-22.04](https://mcr.microsoft.com/en-us/artifact/mar/mssql/server/tag/2022-CU22-GDR1-ubuntu-22.04). A existência foi verificada em fonte oficial. Pull/execução inicialmente bloqueados no sandbox foram posteriormente validados externamente, com SQL healthy e imagem da API compilada. A versão mais recente de segurança precisa ser revisada antes de publicação; não se afirma que esse pin seja a atualização mais recente.

Initialize-LocalEnvironment.ps1 foi executado com sucesso após corrigir uma incompatibilidade de geração de chave no PowerShell 5.1. Gerou secrets aleatórios, PFX e certificado público localhost de 30 dias em arquivos ignorados. Não alterou certificate store/trust. A primeira tentativa falhou antes de gerar .env; não houve sobrescrita de secrets existentes.

Na revisão inicial da implementação, o probe passou a usar CreateFromPem com o conteúdo do certificado público. CreateFromPemFile também procura uma chave privada e era inadequado para api.crt. A leitura do PEM público e sua correspondência com o certificado do servidor foram comprovadas em ferramenta temporária ignorada pelo Git. Essa ferramenta compilou apenas o probe e um host técnico isolado usando os frameworks já instalados, sem as dependências novas da API.

A primeira tentativa dessa ferramenta usou um diretório de trabalho incorreto; após corrigi-lo, a importação padrão do PFX falhou com CryptographicException. A importação efêmera em memória funcionou, sem alterar repositório de certificados. O handshake HTTPS ainda falhou com HttpRequestException → AuthenticationException → Win32Exception: "Credenciais não disponíveis no pacote de segurança". O probe retornou 1; portanto, seu funcionamento HTTPS não foi aprovado. O host temporário foi encerrado no finally, sem encerrar outros processos. Não se confunde essa tentativa com uma validação da API da Etapa 3.

GET /health e /health/live permanecem liveness. GET /health/ready verifica conexão, migrations e colunas de usuário; retorna 503 genérico se indisponível. O teste cobre banco acessível sem schema e falha de conexão sem afetar o liveness.

## Histórico: evidências e bloqueios iniciais do sandbox

Os resultados abaixo pertencem às primeiras tentativas de implementação e não representam o estado atual de restore/build. As evidências externas e a rodada local da revisão final estão no início deste documento.

| Verificação | Resultado real |
|---|---|
| Workspace/Git/documentação | Conferidos antes das alterações |
| Restore da solução com novos pacotes | Falhou com NU1301: soquete proibido para api.nuget.org:443 |
| Consulta NuGet pelo PowerShell | Falha de conexão |
| Auditoria de dependências | NU1900, fonte indisponível; não desabilitada |
| Application.Tests em Release, --no-restore | **16 executados, 16 aprovados, 0 falhas, 0 ignorados**, saída 0; última execução em 1s 183ms |
| Build completo Release, --no-restore | Saída 1, NU1301 nos projetos que dependem dos pacotes novos; compilação da API/Infrastructure não comprovada |
| Teste da solução completa, --no-restore | Saída 1 antes de executar os módulos; imports MTP de IntegrationTests ausentes após restore incompleto |
| Análise sintática C# | 38 arquivos, zero erros de sintaxe; não valida tipos, pacotes ou execução |
| XML, JSON, scripts PowerShell e links locais | Verificação estática aprovada; git diff --check sem erros |
| Geração de secrets/certificado local | Executada; arquivos ignorados por Git, sem valores impressos |
| Probe isolado: leitura do certificado público | Comprovada; certificado público corresponde ao PFX gerado |
| Probe isolado: HTTPS em Kestrel temporário | Falhou no handshake TLS do Windows; probe retornou 1, sem aprovação de HTTPS |
| docker compose config --quiet | **Saída 0**, com aviso de acesso ao config pessoal Docker |
| docker version / API Docker | Acesso negado ao pipe npipe:////./pipe/docker_engine |
| docker compose up -d sqlserver | **Saída 1**, acesso negado ao Docker Engine; nenhum SQL iniciado por essa tentativa |
| Docker no PowerShell externo | Usuário informou Client/Engine 29.9.3, Desktop 4.99.0, contexto desktop-linux e version/info/ps aprovados, sem contêineres em execução; não repetido por este processo |
| Migration aplicada / privilégio SQL / banco vazio | Não executados: Docker e dependências bloqueados |
| Kestrel/auth/HTTPS/JWT/readiness da Etapa 3 | Não validados: build completo ainda bloqueado |
| CI da Etapa 3 | Workflow atualizado localmente; sem push/execução remota nesta etapa |

Não confundir as 14 aprovações históricas da fundação com uma regressão aprovada contra o novo código. Na primeira execução do Codex, os 16 testes da Application eram a única suíte nova com aprovação comprovada. Na primeira rodada isolada eram 15; o caso adicional garante consistência entre a validação da aplicação e Required: senha só de espaços é rejeitada, sem remover espaços de frases válidas. As rodadas posteriores estão discriminadas no início deste documento.

A rodada final executou os três comandos obrigatórios: restore, build e test, todos com saída 1 para a solução completa. O comando de teste também informou IntegrationTests como VSTest: a inspeção do .csproj.nuget.g.props confirmou ausência dos imports do xUnit/MTP após o restore falho, enquanto os imports de Application.Tests estão presentes. O global.json e as referências xUnit/MTP foram preservados; não se trocou de executor nem se editaram os arquivos gerados para mascarar a falha. É necessário restaurar o grafo completo e reavaliar esse diagnóstico.

Logs locais e códigos de saída: .local/stage-3-evidence/restore.log, build.log, test.log e commands.json, todos ignorados pelo Git. O verificador sintático foi uma ferramenta temporária em .local usando o Roslyn do SDK instalado, sem pacote ou ferramenta de sistema; não substitui dotnet build.

A confirmação externa do usuário distingue um Docker Desktop funcional de acesso negado dentro do sandbox. Não é necessário inferir defeito no Engine ou alterar ACLs/pipes. A leitura das cinco credenciais geradas em memória e comparação com arquivos visíveis ao Git encontrou zero ocorrências; nenhum valor foi impresso. O script Test-LocalApi.ps1 passou pelo parser do Windows PowerShell, mas seus fluxos HTTP ainda não foram executados.

## Cobertura da suíte implementada

- Fundação preservada; JWT inválido/adulterado/expirado, algoritmo/issuer/audience/claims, falta de autenticação e banco indisponível.
- Options inválidas, conta SQL sa rejeitada, rate limits/Retry-After, transporte HTTP rejeitado, readiness/liveness.
- SQL real: migration em banco vazio, modelo/snapshot, permissões restritas, cadastro, normalização e duplicação concorrente, persistência/hash, perfil/mass assignment, limites de senha.
- Revogação por senha/logout/desativação, usuário persistido inativo, bloqueio por tentativas, política 403 e corpo DELETE.
- Corrida real entre login e alteração de senha, com locks coordenados e senha antiga incapaz de obter versão nova.

Os casos de JWT inválido usam usuário ativo persistido e controle positivo 200 antes de exigir 401; não usam indisponibilidade do banco como evidência de validação criptográfica. O caso HS384 é assinado com a mesma chave de teste de 64 bytes configurada no host, para que a rejeição comprove a restrição de algoritmo, sem confundi-la com uma chave diferente. Os demais casos incluem assinatura adulterada, expiração/nbf, emissor, audiência e ausência de claims mínimas. Esses casos integram a aprovação externa de 73 testes e continuam obrigatórios na revalidação após mudanças.

Domain.Tests permanece sem casos por não haver domínio financeiro. A tolerância ao código 8 foi removida de Application.Tests e não existe em IntegrationTests. Fixtures SQL não têm skip nem fallback; falha no Docker deve reprovar a suíte.

## Procedimento completo de validação como referência

A sequência completa abaixo foi validada externamente pelo usuário. É mantida para reprodução a partir de um ambiente preparado, sem reduzir proteções. Para a revisão final, use a sequência do início deste documento, sem reprovisionar o SQL já existente.

```powershell
dotnet restore FinanceFlow.slnx
dotnet build FinanceFlow.slnx --configuration Release --no-restore
dotnet test FinanceFlow.slnx --configuration Release --no-restore
docker compose config --quiet
docker compose up -d sqlserver
docker compose build
docker compose run --rm provision
docker compose run --rm migrations
docker compose run --rm provision
docker compose up -d api
curl.exe --cacert .local/certs/api.crt https://localhost:5443/health/ready
```

Corrigir qualquer divergência de compilação/modelo/migration/contrato revelada, sem ignorar os controles. Fazer requisições HTTPS reais com dados fictícios para cadastro/login/perfil/revogação e confirmar SQL restrito e healths. Não registrar os tokens/credenciais nos documentos. Revalidar docs em Production sem expô-las e registrar cada nova rodada separadamente da aprovação anterior.

## Procedimento no PowerShell externo

Pré-requisitos: SDK .NET 10.0.4xx, Docker Desktop com Linux containers/SQL Server x64 e curl.exe com suporte a --cacert. Não é necessário dotnet-ef, sqlcmd instalado no host ou uma conta SQL preexistente. Mantenha o Docker Desktop em execução e o contexto desktop-linux já confirmado. É necessário acesso autorizado a nuget.org e mcr.microsoft.com. Execute a partir do workspace; nenhuma instrução abaixo modifica permissões, instala ferramentas de sistema ou remove volumes.

### 1. Dependências e testes obrigatórios

```powershell
Set-Location C:\FinanceFlow
$ErrorActionPreference = 'Stop'
function Assert-NativeSuccess([string]$Step) {
    if ($LASTEXITCODE -ne 0) { throw "$Step falhou; interrompa a sequência e preserve o resultado." }
}
dotnet --version
Assert-NativeSuccess 'SDK'
docker context show
Assert-NativeSuccess 'Contexto Docker'
docker info
Assert-NativeSuccess 'Docker Engine'
Get-Command curl.exe -ErrorAction Stop | Select-Object Name, Source

dotnet restore FinanceFlow.slnx --configfile NuGet.Config
Assert-NativeSuccess 'Restore'
dotnet build FinanceFlow.slnx --configuration Release --no-restore
Assert-NativeSuccess 'Build'
dotnet test FinanceFlow.slnx --configuration Release --no-restore
Assert-NativeSuccess 'Testes, incluindo SQL Server real'
```

Testcontainers obtém a imagem SQL configurada e cria banco/credenciais aleatórios em memória; não lê .env nem requer secrets manuais. Não remova o erro ou pule a suíte caso falhe. Preserve contagem de testes e mensagens sanitizadas; não publique dumps de configuração. O restore completo precisa repor os imports MTP antes de reavaliar o erro atual de executor.

### 2. Ambiente local, provisionamento e aplicação explícita

Na mesma janela, após a etapa anterior:

```powershell
if (-not (Test-Path -LiteralPath .env)) {
    ./scripts/Initialize-LocalEnvironment.ps1
}
# .env e certificados já gerados devem ser preservados. Não exiba seu conteúdo.
docker compose config --quiet
Assert-NativeSuccess 'Validação Compose'
docker compose up -d sqlserver
Assert-NativeSuccess 'SQL Server'
docker compose build
Assert-NativeSuccess 'Imagem API'
docker compose run --rm provision
Assert-NativeSuccess 'Provisionamento inicial'
docker compose run --rm migrations
Assert-NativeSuccess 'Migration'
docker compose run --rm provision
Assert-NativeSuccess 'Grants de objetos e validação da conta restrita'
docker compose up -d api
Assert-NativeSuccess 'API'
docker compose ps
Assert-NativeSuccess 'Estado dos serviços'
```

Compose carrega .env automaticamente. A API recebe somente a credencial restrita; segredos administrativos ficam no serviço de provisionamento. Se houver senha preexistente diferente, o processo deve falhar; não reinicialize volumes ou senhas automaticamente. Aguarde o health da API estar saudável. Consulte `docker compose logs --tail 80 api` para diagnóstico, sem divulgar dados sensíveis. Não use `docker compose config` sem --quiet.

### 3. Requisições HTTPS reais em Development

```powershell
./scripts/Test-LocalApi.ps1
```

O script exige HTTPS/loopback, valida o certificado público local com curl --cacert e não ignora TLS. Confere 200 em liveness/readiness, OpenAPI e Swagger; cadastro 201; login/perfil 200; edição, senha, logout e DELETE com JSON 204; token adulterado, versões revogadas, senha antiga e usuário desativado 401. Usa um e-mail aleatório example.invalid e senhas criptográficas fictícias; o usuário permanece inativo no banco. Credenciais/tokens trafegam pelo stdin do curl e não são impressos. Se uma chamada falhar, o script interrompe sem apresentar sucesso nem imprimir corpos sensíveis. Os testes automatizados complementam este fluxo com lockout, issuer/audience/algoritmo, 403, 429, banco indisponível e concorrência.

### 4. Documentação ausente em Production

Após concluir Development, encerre somente a API deste Compose e rode uma instância Production na mesma porta:

```powershell
docker compose stop api
Assert-NativeSuccess 'Parada da API deste projeto'
docker compose run --rm --service-ports -e ASPNETCORE_ENVIRONMENT=Production api
```

O último comando fica em primeiro plano. Em **outra janela**, execute:

```powershell
Set-Location C:\FinanceFlow
curl.exe --disable --silent --show-error --cacert .local/certs/api.crt --output NUL --write-out '%{http_code}\n' https://localhost:5443/health/ready
curl.exe --disable --silent --show-error --cacert .local/certs/api.crt --output NUL --write-out '%{http_code}\n' https://localhost:5443/openapi/v1.json
curl.exe --disable --silent --show-error --cacert .local/certs/api.crt --output NUL --write-out '%{http_code}\n' https://localhost:5443/swagger/index.html
```

Resultados exigidos e confirmados na rodada externa: 200, 404 e 404. Encerre a instância Production com Ctrl+C em sua janela. A opção --rm remove somente esse contêiner temporário, sem remover o volume SQL. Se desejar manter o ambiente Development, `docker compose up -d api`. Para encerrar os dois serviços locais sem excluir dados, `docker compose stop api sqlserver`. Não use down -v.

### Alternativa: Kestrel no host

Use esta alternativa em vez do serviço API do Compose, preservando SQL na porta 14333 e sem duas APIs na porta 5443:

```powershell
./scripts/Use-LocalEnvironment.ps1 -Mode Api
dotnet run --project src/FinanceFlow.Api --configuration Release --no-build --launch-profile https
```

Use-LocalEnvironment define variáveis somente no processo atual, não imprime valores, carrega o PFX e limpa credenciais administrativas/de migrations no modo Api. Para ferramentas no host, use outra janela, modo DatabaseTools e os argumentos --provision-database/--migrate documentados no README; não inicie o host nesse modo. Em uma segunda janela, execute Test-LocalApi.ps1. Ao terminar, encerre somente o processo iniciado com Ctrl+C. Se o Windows repetir a falha de TLS observada no sandbox, preserve a mensagem completa e valide primeiro a alternativa Compose/Linux; não desative a validação do certificado nem altere permissões para contornar a falha.

## Histórico e decisão da correção NuGet de Pkcs

A decisão abaixo foi mantida. O bloqueio de restore descrito nesta seção pertence à rodada de correção original: o usuário posteriormente aprovou restore/auditoria externamente e o Codex agora confirmou o grafo corrigido localmente, conforme a revisão final no início deste documento.

### Origem confirmada e escolha

O usuário informou restore externo bem-sucedido e seis avisos NU1902/NU1903. Nesta revisão foi executado `dotnet nuget why FinanceFlow.slnx System.Security.Cryptography.Pkcs`, com saída 0. O grafo então restaurado mostrou:

```text
FinanceFlow.Infrastructure
  Microsoft.EntityFrameworkCore.SqlServer 10.0.13
    Microsoft.Data.SqlClient 6.1.7
      System.Security.Cryptography.Pkcs 9.0.11
```

Api e IntegrationTests recebem a mesma dependência pela referência a Infrastructure. Domain, Application e seus testes não recebem Pkcs. Os seis avisos correspondem às duas vulnerabilidades repetidas nos três projetos afetados, sem indicar seis bibliotecas diferentes. O nuspec local do SqlClient confirmou a exigência de Pkcs 9.0.11 nos assets net9.0, selecionados pelo projeto net10.0; o provider EF exige SqlClient >= 6.1.7. Os sete .csproj e o gerenciamento central foram inspecionados: Pkcs era transitiva, sem referência direta nos projetos.

Foi priorizada a correção no pacote responsável: **Microsoft.Data.SqlClient 6.1.7 → 6.1.8**, patch estável. A [página oficial do SqlClient 6.1.8](https://www.nuget.org/packages/Microsoft.Data.SqlClient/6.1.8#dependencies-body-tab) informa Pkcs >= **9.0.21** no grupo net9.0, compatível com o target net10.0. Essa versão de Pkcs corrige tanto [GHSA-846f-f764-76mx, alta](https://github.com/advisories/GHSA-846f-f764-76mx) quanto [GHSA-2fmv-rv2c-59x7, moderada](https://github.com/advisories/GHSA-2fmv-rv2c-59x7). Não é necessário mudar de versão principal nem fixar Pkcs diretamente.

Alterações exclusivas desta correção:

- Directory.Packages.props: versão central Microsoft.Data.SqlClient 6.1.8 e comentário sobre o motivo de segurança.
- FinanceFlow.Infrastructure.csproj: PackageReference sem versão para Microsoft.Data.SqlClient. A dependência direta também corresponde ao uso já existente de SqlConnection, SqlConnectionStringBuilder e SqlException nessa camada.
- Este documento: origem, decisão, evidências e validação pendente.

EF Core, Identity, JWT, APIs, migration e código C# não foram alterados nesta revisão. O patch 6.1.8 satisfaz o limite >= 6.1.7 do provider já utilizado. Uma atualização mais ampla de EF não é necessária; atualizar para SqlClient 7.x seria uma mudança de versão principal desnecessária. Pkcs permanece transitiva e sua versão mínima corrigida vem do driver atualizado. Nenhum NuGetAudit, NoWarn, auditoria ou política de executor foi desabilitado ou alterado.

### Resultados efetivamente obtidos nesta revisão

| Comando/verificação | Resultado |
|---|---|
| dotnet nuget why FinanceFlow.slnx System.Security.Cryptography.Pkcs, antes da alteração | Saída 0, cadeia SqlClient 6.1.7 → Pkcs 9.0.11 confirmada |
| dotnet restore FinanceFlow.slnx --configfile NuGet.Config, após a alteração | Saída 1, NU1301 ao obter Microsoft.Data.SqlClient; soquete proibido para api.nuget.org:443 |
| dotnet build FinanceFlow.slnx --configuration Release --no-restore | Saída 1, quatro erros NU1301; compilação completa não comprovada |
| dotnet test FinanceFlow.slnx --configuration Release --no-restore | Saída 1, imports MTP de IntegrationTests incompletos após restore falho; suíte não executada |
| dotnet test tests/FinanceFlow.Application.Tests/FinanceFlow.Application.Tests.csproj --configuration Release --no-restore | Saída 0; **16 executados, 16 aprovados, 0 falhas, 0 ignorados**, duração 721ms |
| dotnet list FinanceFlow.slnx package --vulnerable --include-transitive --no-restore | Saída 1; auditoria online bloqueada pelo mesmo acesso ao NuGet |
| XML dos arquivos de configuração/projeto e git diff --check | Verificação estática sem erros |

Logs locais ignorados: .local/pkcs-security-evidence/restore.log, build.log, test.log, application-tests.log e audit.log. SqlClient 6.1.8 e Pkcs 9.0.21 não estavam no cache local quando a tentativa de restore falhou. Portanto, **a correção está configurada, mas o grafo pós-restore e a ausência dos avisos ainda não foram comprovados**. O resultado dos 16 testes de Application não comprova integração com o driver atualizado, pois esse projeto não depende de SQL/Pkcs.

### Comandos de validação no PowerShell externo

Não são necessários segredos, migrations, variáveis SQL de desenvolvimento ou alteração de permissões para restaurar e auditar as dependências. Para a suíte completa, mantenha o Docker Desktop/Engine Linux acessível; as fixtures criam credenciais SQL/JWT efêmeras em memória. A arquitetura, os serviços existentes e o executor Microsoft.Testing.Platform devem ser preservados.

```powershell
Set-Location C:\FinanceFlow
$ErrorActionPreference = 'Stop'
function Assert-NuGetValidation([string]$Step) {
    if ($LASTEXITCODE -ne 0) { throw "$Step falhou; interrompa e preserve o resultado." }
}

dotnet restore FinanceFlow.slnx --configfile NuGet.Config
Assert-NuGetValidation 'Restore'
dotnet nuget why FinanceFlow.slnx System.Security.Cryptography.Pkcs
Assert-NuGetValidation 'Grafo Pkcs'
dotnet list FinanceFlow.slnx package --vulnerable --include-transitive --no-restore
Assert-NuGetValidation 'Consulta de vulnerabilidades'
dotnet build FinanceFlow.slnx --configuration Release --no-restore
Assert-NuGetValidation 'Build Release'
dotnet test FinanceFlow.slnx --configuration Release --no-restore
Assert-NuGetValidation 'Testes obrigatórios'
```

Exigir que why mostre SqlClient 6.1.8 e Pkcs 9.0.21 ou uma versão superior corrigida compatível, sem Pkcs 9.0.11; conferir que os dois avisos desapareceram no restore e na auditoria. Uma consulta de vulnerabilidades pode sair 0 mesmo encontrando avisos: examine a saída, não apenas o código. Registrar separadamente quaisquer vulnerabilidades adicionais, falhas de build ou de testes reveladas. Não considerar esta correção validada nem concluir a Etapa 3 antes dos resultados reais. Nenhum commit/push ou trabalho da Etapa 4 foi realizado.

## Inventário de arquivos

14 arquivos existentes modificados:

```text
.github/workflows/build.yml
Directory.Packages.props
README.md
src/FinanceFlow.Api/Controllers/HealthController.cs
src/FinanceFlow.Api/Errors/ApiProblemDetails.cs
src/FinanceFlow.Api/Errors/GlobalExceptionHandler.cs
src/FinanceFlow.Api/Program.cs
src/FinanceFlow.Api/Properties/launchSettings.json
src/FinanceFlow.Infrastructure/FinanceFlow.Infrastructure.csproj
tests/FinanceFlow.Application.Tests/FinanceFlow.Application.Tests.csproj
tests/FinanceFlow.Application.Tests/README.md
tests/FinanceFlow.IntegrationTests/ApiFactory.cs
tests/FinanceFlow.IntegrationTests/FinanceFlow.IntegrationTests.csproj
tests/FinanceFlow.IntegrationTests/TestOnlyController.cs
```

38 arquivos novos preparados:

```text
.dockerignore
.env.example
Dockerfile
compose.yaml
docs/etapa-3/validacao.md
scripts/Initialize-LocalEnvironment.ps1
scripts/Use-LocalEnvironment.ps1
scripts/Test-LocalApi.ps1
src/FinanceFlow.Api/Authentication/AuthenticationConfiguration.cs
src/FinanceFlow.Api/Configuration/RateLimitConfiguration.cs
src/FinanceFlow.Api/Contracts/Users/AccountRequests.cs
src/FinanceFlow.Api/Controllers/AccountControllerBase.cs
src/FinanceFlow.Api/Controllers/AuthController.cs
src/FinanceFlow.Api/Controllers/UsersController.cs
src/FinanceFlow.Application/Users/AccountInputRules.cs
src/FinanceFlow.Application/Users/IUserAccounts.cs
src/FinanceFlow.Infrastructure/Authentication/AccessTokenIssuer.cs
src/FinanceFlow.Infrastructure/Authentication/JwtOptions.cs
src/FinanceFlow.Infrastructure/Configuration/DatabaseOptions.cs
src/FinanceFlow.Infrastructure/DependencyInjection.cs
src/FinanceFlow.Infrastructure/Identity/ApplicationUser.cs
src/FinanceFlow.Infrastructure/Identity/PasswordLengthValidator.cs
src/FinanceFlow.Infrastructure/Identity/UnknownCredentialVerifier.cs
src/FinanceFlow.Infrastructure/Identity/UserAccounts.cs
src/FinanceFlow.Infrastructure/Identity/UserTokenState.cs
src/FinanceFlow.Infrastructure/Persistence/DatabaseTasks.cs
src/FinanceFlow.Infrastructure/Persistence/FinanceFlowDbContext.cs
src/FinanceFlow.Infrastructure/Persistence/Migrations/20261113000100_InitialIdentity.cs
src/FinanceFlow.Infrastructure/Persistence/Migrations/FinanceFlowDbContextModelSnapshot.cs
src/FinanceFlow.Infrastructure/Persistence/Migrations/InitialIdentityModel.cs
src/FinanceFlow.Infrastructure/Persistence/ReadinessProbe.cs
tests/FinanceFlow.Application.Tests/AccountInputRulesTests.cs
tests/FinanceFlow.IntegrationTests/AuthenticationSecurityTests.cs
tests/FinanceFlow.IntegrationTests/IdentityConfigurationTests.cs
tests/FinanceFlow.IntegrationTests/IdentitySqlFixture.cs
tests/FinanceFlow.IntegrationTests/IdentitySqlTests.cs
tests/FinanceFlow.IntegrationTests/LoginConcurrencyTests.cs
tests/FinanceFlow.IntegrationTests/TestAuthorizationController.cs
```

Os arquivos locais de secrets, certificados, caches e evidências ignorados não integram esse inventário do Git.

## Referências e limites

[EF Core SQL Server](https://learn.microsoft.com/en-us/ef/core/providers/sql-server/), [Identity 10.0.13](https://www.nuget.org/packages/Microsoft.AspNetCore.Identity.EntityFrameworkCore/10.0.13), [JWT bearer](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/configure-jwt-bearer-authentication?view=aspnetcore-10.0) e [Testcontainers.MsSql](https://dotnet.testcontainers.org/modules/mssql/). Identity/JWT/EF Core permanecem em 10.0.13; SqlClient está configurado em 6.1.8 pela correção de segurança acima; Testcontainers.MsSql permanece em 4.16.0. A restauração final é necessária para confirmar todo o grafo.

Pendências atuais: repetir a suíte SQL e as verificações HTTPS após os dois ajustes da revisão final; confirmar ausência do warning EF em execução; revisar o patch da imagem SQL antes de exposição pública; executar a nova CI somente após publicação autorizada. Não há novas decisões financeiras, alteração de ADR aprovado, contas, categorias, lançamentos, saldos ou relatórios. Os bloqueios antigos preservados como histórico não anulam os resultados externos fornecidos pelo usuário.
