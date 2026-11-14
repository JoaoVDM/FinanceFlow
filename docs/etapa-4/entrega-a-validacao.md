# Entrega A — Evidências e critérios de validação

Status: **Entrega A concluída e validada externamente pelo usuário no PowerShell do Windows**, aguardando revisão e futuro commit. A Etapa 4 completa não está concluída: as Entregas B/C não foram iniciadas. O GitHub Actions da Entrega A ainda não foi executado.

## Origem das evidências

Base externa fornecida pelo usuário, anterior a esta alteração: commit 29fe1c0 publicado, build sem warnings, 73 testes aprovados após correções finais, 22 verificações Development, três Production, Docker/SQL/migrations/permissões aprovados e Actions 43439337019 Success. Esses resultados não comprovam o código novo da Entrega A.

## Fechamento externo da Entrega A

Após a implementação e as verificações locais registradas abaixo, o usuário executou a validação fora do sandbox e confirmou os resultados a seguir. São **evidências externas fornecidas pelo usuário**, não comandos ou testes executados pelo Codex nesta atualização documental.

| Verificação externa | Resultado confirmado pelo usuário |
|---|---|
| Docker Engine | Acessível e funcionando no PowerShell do Windows |
| Build Release | **Sete projetos, 0 erros e 0 warnings** |
| Testes automatizados | **151 aprovados, 0 falhas e 0 ignorados** |
| SQL Server | Healthy |
| Imagem Docker | Reconstruída com sucesso |
| Provisionamento inicial | Aprovado |
| Migration | **20261114000100_AccountsDeliveryA aplicada com sucesso** |
| Provisionamento após a migration | Repetido com permissões verificadas |
| API Docker | Healthy |
| Test-LocalApi.ps1 | **22 verificações HTTPS concluídas com sucesso** |
| Test-LocalAccounts.ps1 | Criação, consulta, listagem, paginação, idempotência, validações e desativação do usuário fictício concluídas com sucesso |
| HTTPS Production | /health/ready **200**; /openapi/v1.json **404**; /swagger/index.html **404** |
| Encerramento | Contêiner temporário de Production encerrado corretamente; SQL Server permanece saudável |
| Commit e publicação | Nenhum commit ou push da Entrega A realizado |
| GitHub Actions da Entrega A | **Ainda não executado**; o Success da execução 43439337019 pertence à Etapa 3 |

A aprovação externa dos 151 testes inclui os 49 casos SQL que não puderam executar suas asserções no sandbox. Ela comprova a suíte no ambiente externo utilizado, incluindo migrations, constraints, permissões, concorrência, rollback e replay. Não implica execução de nova CI nem elimina as limitações históricas de acesso do Codex ao Docker.

## Histórico das verificações do Codex no sandbox

Resultados executados pelo Codex durante a implementação, **anteriores ao fechamento externo**:

| Verificação | Resultado real |
|---|---|
| Workspace e Git inicial | C:\FinanceFlow; main no commit 29fe1c0; limpo |
| Domain.Tests, primeira rodada | 17 executados e aprovados |
| Application.Tests | 32 aprovados (16 novos e 16 anteriores) |
| Parsing/modelo/contratos sem SQL | 23 novos aprovados; inclui snapshot/modelo sem diferenças, precisão original JSON, autenticação e OpenAPI |
| Restore da solução | Saída 0, cache disponível |
| Build Release da solução | Saída 0, sete projetos, 0 erros e 0 warnings |
| Suíte completa final no sandbox | **151 executados: 102 aprovados, 49 falhas, 0 ignorados**; saída 2 |
| Causa das 49 falhas | DockerUnavailableException; acesso negado ao pipe npipe://./pipe/docker_engine. Os casos SQL não chegaram a comprovar suas asserções |
| Migration/constraints/grants/replay concorrente com SQL | Não comprovados pelo Codex nessa rodada: fixture Testcontainers bloqueada; posteriormente aprovados externamente conforme fechamento acima |
| Kestrel HTTP técnico, Development | health 200; live 200; ready 503; OpenAPI 200 com accounts; Swagger 200; inexistente 404 |
| Kestrel HTTP técnico, Production | health/live 200; ready 503; OpenAPI/Swagger/inexistente 404 |
| Motivo do readiness 503 no smoke | Banco fictício indisponível propositalmente, sem usar credenciais ou banco do usuário |
| Encerramento do smoke | Dois processos Kestrel pertencentes à validação encerrados; nenhum processo/contêiner existente interrompido |
| HTTPS de contas | Script preparado; não executado/aprovado no sandbox; posteriormente concluído externamente pelo usuário |

Logs e evidências temporários ignorados em .local/stage-4-a-evidence. O smoke HTTP não enviou senhas/tokens e não prova autenticação financeira em Kestrel HTTPS. Não confundir os 102 casos aprovados com aprovação dos 151. A primeira rodada completa tinha 150 casos (102 aprovados/48 bloqueados); foi acrescentado um caso SQL negativo para privilégio UPDATE financeiro, levando a 151 na rodada final. Nenhum teste obrigatório foi ignorado ou mascarado; a tolerância ao código 8 de Domain.Tests foi removida.

## Cobertura acrescentada

- Domain: quatro tipos canônicos, nomes, normalização de exibição, BRL, saldos assinados/limites/centavos, abertura, owner/Id e imutabilidade pública.
- Application: owner confiável, São Paulo perto da meia-noite, TimeProvider determinístico, comando inválido, chave ASCII/limites, hash canônico, conflitos/falhas do store, filtros/paginação inválidos e escopo das consultas.
- Contratos sem SQL: dinheiro original com limites/zeros finais/expoentes e precisão excessiva; model/snapshot; rotas autorizadas; ausência de PUT/PATCH/categorias no OpenAPI; Bearer/chave/ETag documentados.
- SQL obrigatório: banco vazio via fixture; upgrade de schema Identity com preservação de usuário/token; conta/rowversion; paginação/filtros/inativas; isolamento com dois usuários; chaves sensíveis à caixa; replay após alteração privilegiada em banco de teste; corrida coordenada para payload igual/diferente; rollback após INSERT real; violação de outro índice sem replay; fronteiras monetárias HTTP; constraints, FK NoAction, metadados e grants restritos.

A injeção de falha observa o INSERT real da conta no SQL e lança erro antes do INSERT do registro; depois consulta o banco real para exigir zero resíduos. Não substitui resultados SQL por mocks. Os testes alteram nome/atividade somente com migrador no banco descartável, sem criar endpoints de B/C ou atualizar o banco persistente.

## Validação externa

O procedimento a seguir é mantido para reprodução futura. Sua execução bem-sucedida foi confirmada pelo usuário no fechamento acima. Nenhum comando desta seção foi executado pelo Codex nesta atualização documental.

Pré-requisitos: SDK .NET 10.0.4xx, acesso ao NuGet/MCR, Docker Desktop com Engine Linux acessível e curl.exe. Não é preciso instalar dotnet-ef ou ferramentas de sistema. A suíte usa credenciais efêmeras geradas pelas fixtures; não requer .env nem senha real do desenvolvedor.

### 1. Restore, build, testes e auditoria

```powershell
Set-Location C:\FinanceFlow
$ErrorActionPreference = 'Stop'
function Assert-DeliveryAStep([string]$Step) {
    if ($LASTEXITCODE -ne 0) { throw "$Step falhou; interrompa e preserve o resultado." }
}
docker info
Assert-DeliveryAStep 'Docker'
dotnet restore FinanceFlow.slnx --configfile NuGet.Config
Assert-DeliveryAStep 'Restore'
dotnet build FinanceFlow.slnx --configuration Release --no-restore
Assert-DeliveryAStep 'Build'
dotnet test FinanceFlow.slnx --configuration Release --no-restore
Assert-DeliveryAStep 'Testes SQL e regressão'
dotnet list FinanceFlow.slnx package --vulnerable --include-transitive --no-restore
Assert-DeliveryAStep 'Auditoria'
```

Exigir 151 aprovados, nenhuma falha/ignorado e build sem warnings. Examine a auditoria: código de saída 0 sozinho não significa ausência de vulnerabilidades. Não pule os casos SQL se Docker falhar. Informe qualquer falha real para correção; os testes não foram declarados aprovados por presunção.

### 2. Aplicação externa da migration ao ambiente local

**O Codex não executou esta seção no banco persistente. O usuário confirmou a aplicação externa da migration e os dois provisionamentos.** Estes comandos permanecem como referência de reprodução, sem necessidade de repeti-los apenas para atualizar a documentação. Preserve .env, senhas e volumes existentes. Se o ambiente ainda não existir, o script de inicialização gera valores aleatórios e recusa sobrescrita; se existir, reutilize-o sem exibir valores.

```powershell
if (-not (Test-Path -LiteralPath .env)) { ./scripts/Initialize-LocalEnvironment.ps1 }
docker compose config --quiet
Assert-DeliveryAStep 'Compose'
docker compose up -d sqlserver
Assert-DeliveryAStep 'SQL'
docker compose build
Assert-DeliveryAStep 'Imagens'
docker compose run --rm provision
Assert-DeliveryAStep 'Provisionamento'
docker compose run --rm migrations
Assert-DeliveryAStep 'Migration incremental'
docker compose run --rm provision
Assert-DeliveryAStep 'Grants das novas tabelas e verificador'
docker compose up -d api
Assert-DeliveryAStep 'API'
```

Compose injeta a credencial restrita no serviço API e mantém a administrativa/migradora nos serviços de ferramentas. A nova migration cria duas tabelas, sem alterar o target histórico Identity. Não use down -v, reset de credenciais ou deleção de dados para contornar erros. Readiness deve responder 200 somente após schema e grants corretos.

### 3. HTTPS e regressão externa

```powershell
./scripts/Test-LocalApi.ps1
# O fluxo anterior utiliza as cinco tentativas de login permitidas na janela de um minuto.
Start-Sleep -Seconds 61
./scripts/Test-LocalAccounts.ps1
curl.exe --disable --cacert .local/certs/api.crt https://localhost:5443/health/ready
docker compose logs --tail 80 api
# Parar somente a API iniciada para esta validação, mantendo SQL e volume:
docker compose stop api
```

Os scripts usam dados fictícios, segredos/tokens em memória e stdin do curl, sem argumentos sensíveis nem desativação de TLS. Preserve a pausa entre scripts para respeitar o rate limit existente; um 429 não deve ser contornado reduzindo a proteção. Test-LocalAccounts verifica cadastro/login, criação/replay/conflito, GET/lista, validação de precisão e ausência de chave; deixa a conta e o registro idempotente preservados e desativa o próprio usuário fictício ao concluir. Não testa todas as garantias SQL; isso cabe à suíte Testcontainers.

Para Kestrel no host, mantenha SQL provisionado e API Compose parada, execute `./scripts/Use-LocalEnvironment.ps1 -Mode Api` e `dotnet run --project src/FinanceFlow.Api --configuration Release --no-build --launch-profile https`. Em outra janela, execute os scripts. O modo Api limpa variáveis administrativas/de migration. Encerre o processo próprio com Ctrl+C. Não use modo DatabaseTools para hospedar a API.

Repetir as três verificações Production pelo procedimento da [Etapa 3](../etapa-3/validacao.md#4-documentação-ausente-em-production), mantendo certificados e exigindo readiness 200, OpenAPI 404 e Swagger 404. Não confundir esse ambiente Production local com hospedagem pública.

## Critérios de conclusão e pendências

Os critérios de conclusão da Entrega A foram atendidos na validação externa informada pelo usuário: migration aplicada, 151 testes aprovados, permissões verificadas, API healthy e fluxos HTTPS Development/Production aprovados. O teste offline de modelo permanece uma evidência distinta; não foi usado como substituto da execução SQL real.

As pendências anteriores de SQL/Compose/HTTPS foram resolvidas pelo usuário no ambiente externo, incluindo a aprovação dos testes de colisão idempotente. A migration foi escrita manualmente com target congelado, conferida offline e aplicada externamente; não foi gerada por ferramenta EF nem aplicada pelo Codex. A classificação de mensagens SQL em outros idiomas continua uma consideração de portabilidade, sem falha relatada no ambiente validado. A limitação de acesso ao Engine dentro do sandbox é preservada como histórico, sem impedir o reconhecimento dos resultados externos.

Pendências de publicação: revisão do conjunto de alterações, futuro commit/push somente mediante autorização e execução do GitHub Actions da Entrega A após publicação. **Não há resultado de CI aprovado para esta entrega.** Isso não autoriza iniciar B/C ou a Etapa 5.

Entregas B/C e Etapa 5 não foram iniciadas. Não há PUT/PATCH/DELETE de contas, Category, FinancialTransaction, saldo atual, extrato, transferência ou relatório. Durante a atuação do Codex não houve commit, push, reset de credenciais, alteração de ACL/sandbox ou aplicação de migrations no banco persistente. A aplicação da migration foi realizada externamente pelo usuário, conforme o fechamento acima.

## Inventário final e revisão de segurança

11 arquivos existentes modificados:

```text
README.md
docs/etapa-3/validacao.md
src/FinanceFlow.Api/Controllers/HealthController.cs
src/FinanceFlow.Api/Program.cs
src/FinanceFlow.Infrastructure/DependencyInjection.cs
src/FinanceFlow.Infrastructure/Persistence/DatabaseTasks.cs
src/FinanceFlow.Infrastructure/Persistence/FinanceFlowDbContext.cs
src/FinanceFlow.Infrastructure/Persistence/Migrations/FinanceFlowDbContextModelSnapshot.cs
tests/FinanceFlow.Application.Tests/README.md
tests/FinanceFlow.Domain.Tests/FinanceFlow.Domain.Tests.csproj
tests/FinanceFlow.Domain.Tests/README.md
```

22 arquivos criados:

```text
docs/etapa-4/README.md
docs/etapa-4/entrega-a-validacao.md
scripts/Test-LocalAccounts.ps1
src/FinanceFlow.Api/Authentication/HttpCurrentUser.cs
src/FinanceFlow.Api/Contracts/Accounts/AccountRequests.cs
src/FinanceFlow.Api/Contracts/Accounts/ExactMoneyJsonConverter.cs
src/FinanceFlow.Api/Controllers/AccountsController.cs
src/FinanceFlow.Application/Accounts/AccountContracts.cs
src/FinanceFlow.Application/Accounts/AccountCreationFingerprint.cs
src/FinanceFlow.Application/Accounts/FinancialAccountsService.cs
src/FinanceFlow.Application/Accounts/FinancialCalendar.cs
src/FinanceFlow.Domain/Accounts/FinancialAccount.cs
src/FinanceFlow.Infrastructure/Persistence/Accounts/FinancialAccountConfiguration.cs
src/FinanceFlow.Infrastructure/Persistence/Accounts/FinancialAccountStore.cs
src/FinanceFlow.Infrastructure/Persistence/Accounts/IdempotencyRecord.cs
src/FinanceFlow.Infrastructure/Persistence/Accounts/IdempotencyRecordConfiguration.cs
src/FinanceFlow.Infrastructure/Persistence/Migrations/20261114000100_AccountsDeliveryA.cs
src/FinanceFlow.Infrastructure/Persistence/Migrations/AccountsDeliveryAModel.cs
tests/FinanceFlow.Application.Tests/FinancialAccountsServiceTests.cs
tests/FinanceFlow.Domain.Tests/FinancialAccountTests.cs
tests/FinanceFlow.IntegrationTests/AccountInputAndModelTests.cs
tests/FinanceFlow.IntegrationTests/FinancialAccountsSqlTests.cs
```

Verificações estáticas da implementação: git diff --check sem erros; scripts PowerShell analisados; links locais verificados; nenhum arquivo sensível/temporário visível ao Git, nenhuma ocorrência dos cinco segredos locais gerados nem blocos de chave privada nos arquivos visíveis. A migration inicial e InitialIdentityModel tiveram diff vazio. Nenhuma dependência de pacote foi adicionada. Esses checks são independentes da validação SQL/HTTPS externa posterior.

Nesta atualização final foram alterados somente README.md, docs/etapa-4/README.md e este documento. Código, scripts e migrations foram preservados. A revisão documental inclui referências locais, git diff --check e inspeção de arquivos visíveis ao Git; não houve execução de testes, banco, contêineres, commit ou push.
