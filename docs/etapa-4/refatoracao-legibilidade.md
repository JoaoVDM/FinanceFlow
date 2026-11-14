# Manutenção de legibilidade após a Entrega A

## Escopo e referência

Refatoração de clareza e organização, sem nova funcionalidade. O workspace começou limpo em `main`, no commit `5a13762 — feat: implement financial accounts and idempotent creation`.

O usuário informou 151 testes aprovados, build sem erros/warnings, validações HTTPS/SQL/Docker aprovadas e GitHub Actions **43559125753 — Success** para esse commit. São evidências externas do código publicado, anteriores a esta manutenção. Não representam execução pelo Codex nem aprovação das alterações atuais.

## Melhorias realizadas

- Testes SQL de contas divididos em três arquivos da **mesma classe partial FinancialAccountsSqlTests**: contratos/consultas/validação; idempotência/concorrência/rollback; migrations/constraints/permissões. A classe, os nomes dos testes, os dados das theories, a collection `identity-sql`, a fixture e a configuração de paralelismo foram preservados. Os arquivos adicionais não criam novas classes de teste nem fixtures.
- Instruções antes comprimidas foram separadas nos testes de contas e Identity. Blocos, etapas de preparação/execução/verificação e chamadas longas ficaram explícitos. Helpers assíncronos de contas e a coordenação das corridas receberam nomes descritivos.
- FinancialAccountStore mantém visíveis consulta inicial, transação, dois SaveChanges, commit e recuperação. Apenas a montagem do snapshot/registro foi extraída para CreateRecord, depois da obtenção da rowversion. O replay continua devolvendo o JSON persistido, inclusive na primeira resposta.
- Configuração OpenAPI extraída de Program.cs para ApiDocumentationConfiguration, com métodos específicos para schemas, documento, Bearer e metadados das operações de contas. A composição e a habilitação somente em Development continuam no Program.
- ICurrentUser foi movido para Application/Common; assinatura, implementação HTTP e ciclo de vida Scoped foram preservados. Os contratos relacionados a contas continuam agrupados, sem um arquivo por DTO.
- Entidade, serviço, DTOs e controller de contas receberam formatação pontual e condicionais explícitas. A ordem das validações, parâmetros, atributos HTTP, mensagens e resultados foi mantida.
- ExactMoneyJsonConverter recebeu nomes explicativos e comentários sobre coeficiente/expoente, zeros, centavos e limite antes da alocação. O algoritmo, mensagens, tratamento de null, leitura segmentada, sinal, escala e escrita não foram alterados.
- AGENTS.md registra convenções curtas para futuras implementações. README e documentação da Entrega A registram sua publicação/CI posterior, preservando o fechamento histórico anterior ao commit.

## Elementos preservados deliberadamente

Não houve alteração de schema, migrations, configurações EF, índices, grants, verificador SQL, autenticação, rate limiting, Docker, pipeline, pacotes ou configuração do executor de testes. Não foram criados endpoints, entidades ou bibliotecas. Os testes continuam exigindo SQL Server real, sem skip, mocks de integridade ou fallback de banco.

A política de erros SQL permaneceu intacta: apenas a colisão 2601, com entrada EF e prefixo do índice/objeto esperados, permite reler o vencedor. Os tratamentos existentes de timeout/deadlock, RollbackAsync(CancellationToken.None), limpeza do tracker e contexto novo foram mantidos. Não foi introduzido retry ou reconhecimento amplo de violações.

Não foram executados provisionamento, migrations ou scripts de alteração no banco persistente. Nenhum segredo, certificado, .env, credencial, ACL, configuração do sandbox ou volume foi alterado. Entregas B/C e Etapa 5 não foram iniciadas. Não houve commit ou push.

## Validação executada pelo Codex

Resultados desta manutenção, em 14/11/2026, SDK **10.0.404**, dentro do sandbox Windows:

| Verificação | Resultado efetivamente observado |
|---|---|
| Restore com NuGet.Config | Saída 0; pacotes disponíveis no cache. Três avisos NU1900 por indisponibilidade de api.nuget.org |
| Build Release final | Sete projetos compilados; saída 0; zero erros e zero warnings C#. Três avisos NU1900 persistiram |
| Suíte completa final, Microsoft.Testing.Platform | **151 casos: 102 aprovados, 49 falhas, 0 ignorados**; saída 2 |
| Motivo das 49 falhas | DockerUnavailableException na fixture; UnauthorizedAccessException ao conectar ao pipe Docker. As asserções SQL não foram executadas |
| Docker info | Saída 1; acesso negado a C:\Users\jvito\.docker\config.json e ao endpoint npipe:////./pipe/docker_engine |
| Inventário estático dos testes | Mesmos 73 métodos Fact/Theory, com nomes e atributos/dados preservados; a descoberta de execução continua totalizando 151 casos |
| OpenAPI antes/depois | GET /openapi/v1.json retornou 200 em TestServer Development; comparação estrutural de todo o JSON confirmou igualdade, incluindo contratos, Bearer, headers e schemas |
| Conversor monetário | Comparação estática confirmou tokens iguais após mapear apenas as renomeações locais e explicitar blocos, desconsiderando comentários; testes existentes de parsing passaram |
| Migrations e componentes protegidos | Diff vazio para migrations, pacotes, propriedades de build, global.json, workflow, scripts e infraestrutura Docker |
| Git diff --check | Saída 0, sem erros |
| Inspeção de segurança | Nenhum nome sensível/temporário visível ao Git nem correspondência aos padrões examinados de chave privada, token GitHub, AWS access key e JWT literal |

A suíte completa foi tentada pelo Codex em duas rodadas, com a mesma contagem e a mesma limitação de infraestrutura. Essas execuções no sandbox não comprovaram a aprovação dos 151 testes. O restore com cache também não comprovou uma auditoria NuGet atual: a consulta de vulnerabilidades ficou indisponível e não foi desativada ou suprimida. A validação externa posterior está registrada na seção seguinte e não altera esses resultados históricos.

O host de teste também registrou falhas de leitura/descriptografia e gravação do keyring Data Protection do perfil Windows. Elas não impediram o GET do OpenAPI nem causaram os 49 erros SQL. O perfil e a configuração de segurança não foram modificados. A comparação OpenAPI usa WebApplicationFactory/TestServer, sem comprovar uma nova rodada de HTTPS real em Kestrel.

Ferramentas auxiliares locais usaram o Roslyn já incluído no SDK para organizar sintaxe e comparar inventários. Uma primeira tentativa de captura OpenAPI precisou do content root explícito, e uma recompilação auxiliar encontrou DLLs bloqueadas por essa tentativa; somente a sessão auxiliar iniciada para a revisão foi encerrada, e a validação foi repetida com sucesso. Fontes auxiliares, logs e snapshots permanecem ignorados em `.local/readability-review/`, fora da solução e do conjunto versionável. Não foram adicionados pacotes ou ferramentas de sistema.

## Validação externa concluída

Em **14/11/2026**, após as verificações do Codex, o usuário executou a validação da refatoração no PowerShell externo do Windows e confirmou os resultados abaixo. São **evidências externas fornecidas pelo usuário**, não execuções realizadas pelo Codex.

| Verificação externa | Resultado confirmado pelo usuário |
|---|---|
| `dotnet test FinanceFlow.slnx --configuration Release --no-restore` | **151 testes aprovados, 0 falhas e 0 ignorados** |
| Projetos de testes | Domain, Application e Integration aprovados, incluindo integração SQL Server real com Testcontainers |
| Avisos durante a execução dos testes | **Três NU1900**, por indisponibilidade temporária da consulta de vulnerabilidades do NuGet |
| `dotnet list FinanceFlow.slnx package --vulnerable --include-transitive` | Executado posteriormente com sucesso; **nenhum pacote vulnerável identificado nos sete projetos**, incluindo dependências transitivas |
| `git diff --check` | Sem saída; aprovado |

A regressão integral e a auditoria NuGet desta manutenção estão concluídas no ambiente externo. A execução posterior da auditoria não significa que os avisos NU1900 desapareceram do build ou dos testes: os avisos históricos e os três observados pelo usuário durante os testes permanecem registrados. Não foi informado um novo resultado de build sem esses avisos.

As 49 falhas de infraestrutura do sandbox continuam preservadas como evidência das execuções do Codex; os testes SQL foram posteriormente aprovados na execução externa do usuário. A aprovação anterior do commit `5a13762` e de sua CI permanece distinta: não foi informado um novo resultado de GitHub Actions para esta refatoração.

## Arquivos da manutenção

Criados:

```text
AGENTS.md
docs/etapa-4/refatoracao-legibilidade.md
src/FinanceFlow.Api/Configuration/ApiDocumentationConfiguration.cs
src/FinanceFlow.Application/Common/ICurrentUser.cs
tests/FinanceFlow.IntegrationTests/FinancialAccountsIdempotencyTests.cs
tests/FinanceFlow.IntegrationTests/FinancialAccountsPersistenceTests.cs
```

Modificados:

```text
README.md
docs/etapa-4/README.md
docs/etapa-4/entrega-a-validacao.md
src/FinanceFlow.Api/Authentication/HttpCurrentUser.cs
src/FinanceFlow.Api/Contracts/Accounts/AccountRequests.cs
src/FinanceFlow.Api/Contracts/Accounts/ExactMoneyJsonConverter.cs
src/FinanceFlow.Api/Controllers/AccountsController.cs
src/FinanceFlow.Api/Program.cs
src/FinanceFlow.Application/Accounts/AccountContracts.cs
src/FinanceFlow.Application/Accounts/FinancialAccountsService.cs
src/FinanceFlow.Domain/Accounts/FinancialAccount.cs
src/FinanceFlow.Infrastructure/Persistence/Accounts/FinancialAccountStore.cs
tests/FinanceFlow.Application.Tests/FinancialAccountsServiceTests.cs
tests/FinanceFlow.Domain.Tests/FinancialAccountTests.cs
tests/FinanceFlow.IntegrationTests/AccountInputAndModelTests.cs
tests/FinanceFlow.IntegrationTests/FinancialAccountsSqlTests.cs
tests/FinanceFlow.IntegrationTests/IdentitySqlTests.cs
```

## Procedimento de reprodução externa

Os comandos abaixo ficam como referência para futuras execuções, sem representar pendência da regressão ou da auditoria já concluídas acima. O procedimento também inclui restore e build; esta atualização não acrescenta evidências externas desses dois comandos além das já registradas. Executar no PowerShell externo, com SDK .NET 10 e Docker Desktop/Engine Linux acessíveis. A suíte gera suas próprias credenciais e bancos descartáveis; não precisa carregar .env, certificados ou credenciais do banco persistente. Não executar scripts de provisionamento ou migrations no banco de desenvolvimento para validar esta manutenção.

```powershell
Set-Location C:\FinanceFlow
$ErrorActionPreference = 'Stop'

docker info --format '{{.ServerVersion}}'
if ($LASTEXITCODE -ne 0) { throw 'Docker indisponível.' }

dotnet restore FinanceFlow.slnx --configfile NuGet.Config
if ($LASTEXITCODE -ne 0) { throw 'Restore falhou.' }

dotnet build FinanceFlow.slnx --configuration Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Build falhou.' }

dotnet test FinanceFlow.slnx --configuration Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Testes falharam; preserve o resultado.' }

dotnet list FinanceFlow.slnx package --vulnerable --include-transitive
if ($LASTEXITCODE -ne 0) { throw 'Auditoria não concluída.' }

git diff --check
if ($LASTEXITCODE -ne 0) { throw 'Diff contém problemas de whitespace.' }
```

Em futuras execuções, conferir **151 aprovados, zero falhas e zero ignorados**, resultado do build e auditoria efetivamente consultada. Registrar eventuais avisos NU1900 separadamente do resultado posterior da consulta de vulnerabilidades; sucesso dos testes não comprova auditoria disponível. A CI atual não foi disparada pelo Codex; sua verificação remota depende de publicação posteriormente autorizada. A configuração do workflow foi preservada.

## Pendências e riscos para mudanças separadas

- Regressão dos 151 testes, incluindo SQL, e auditoria NuGet concluídas externamente pelo usuário em 14/11/2026; não são mais pendências. Revisão das alterações e eventual commit/publicação continuam sujeitos a autorização. A CI de `5a13762` é histórica e não comprova esta manutenção.
- O reconhecimento de colisão depende do prefixo em inglês da mensagem SQL. Essa dependência já existia e continua falhando de modo fechado se a mensagem não corresponder. Uma solução independente de idioma exigiria avaliação funcional e testes próprios, fora desta refatoração.
- O tratamento existente de deadlock executa rollback explícito; avaliar em mudança funcional separada o cenário em que o servidor já encerrou a transação e o rollback possa ocultar a exceção inicial. Esse cenário não foi reproduzido nesta execução e a política não foi modificada.

Status: **refatoração implementada e validada externamente pelo usuário: 151 testes aprovados, incluindo integração SQL, e auditoria NuGet concluída sem pacotes vulneráveis identificados**. O build aprovado pelo Codex mantém o registro dos três avisos NU1900. Pronta para revisão das alterações, preservando as limitações históricas do sandbox e os riscos técnicos para avaliação funcional futura; sem novo resultado de CI informado para esta manutenção.

## Publicação posterior confirmada

Após o fechamento externo acima, o usuário confirmou a publicação da refatoração no commit **9c938c8 — refactor: improve code readability and maintainability** e o GitHub Actions **43564544104 — Success**. As menções anteriores a publicação/CI pendentes registram o estado histórico antes desse fechamento. Essa baseline não comprova a implementação posterior da Entrega B, que possui [validação própria](entrega-b-validacao.md).
