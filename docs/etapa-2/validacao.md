# Etapa 2 — Fundação: implementação e validação

Status atualizado em 2026-11-13: **Etapa 2 concluída**. Fundação compilada, 14 testes automatizados aprovados e validação HTTP real em Kestrel concluída em Development e Production. As 16 verificações finais de HTTP passaram. As duas instâncias iniciadas para a validação foram encerradas. A Etapa 3 não foi iniciada.

## Escopo implementado

- FinanceFlow.slnx e quatro projetos net10.0, com dependências conforme ADR-001.
- Três projetos xUnit; casos efetivos em IntegrationTests, sem regras artificiais nos projetos vazios.
- Controllers, saúde em /health e /health/live, OpenAPI nativo e Swagger UI somente em Development.
- Problem Details, tratamento global de exceções, erros de validação e status HTTP, com code/traceId.
- Configuração por ambiente e Options com validação de OpenApi:Title na inicialização.
- .gitignore, .editorconfig, .gitattributes, global.json, versões centralizadas, configuração NuGet e README.
- Workflow local de build/testes; nenhuma execução do GitHub foi solicitada ou comprovada.

Não foram implementados Identity, JWT, EF Core, banco, migrations, Docker ou regras financeiras. Não há rotas públicas que provoquem falhas; o controller de validação/exceção fica apenas no projeto de testes.

## Compatibilidade com a Etapa 1

O endpoint /health foi mantido como alias de /health/live para conciliar o pedido de implementação e o planejamento. Ambos são liveness do processo, sem readiness fictício. Recursos de negócio futuros permanecem sob /api/v1. Não há mudança de direção de dependências ou de regra financeira.

Application, Domain e Infrastructure não receberam classes sem funcionalidade. O formato .slnx é suportado pelo SDK instalado. A Etapa 1 foi preservada como registro da aprovação; este documento registra o andamento da implementação posterior.

## Ambiente verificado

- Workspace: C:\FinanceFlow.
- SDK: 10.0.404.
- Estado inicial do Git: main...origin/main, sem alterações locais.
- Não foram executados commits, push, mudanças de propriedade/ACL ou de configuração do sandbox.

## Resultados atuais — correção do executor .NET 10

Esta seção registra a correção anterior do executor. A validação final de Kestrel está na seção seguinte.

O usuário informou restore bem-sucedido e build Release dos sete projetos no PowerShell externo. Essas execuções externas não foram realizadas pelo agente. Os arquivos project.assets.json foram inspecionados e confirmam Microsoft.Testing.Platform/2.5.1 e Microsoft.Testing.Platform.MSBuild/2.5.1 nos três projetos de teste, através da configuração xUnit existente.

O erro do VSTest ocorre porque MTP 2.5.1 exige a experiência nativa de dotnet test no SDK .NET 10. Foi acrescentado ao global.json somente `test.runner = "Microsoft.Testing.Platform"`, preservando sdk.version 10.0.400, rollForward latestPatch e allowPrerelease false. Directory.Build.props e versões dos pacotes não foram alterados.

Comando executado no workspace, sem restore:

```powershell
dotnet test FinanceFlow.slnx --configuration Release --no-restore
```

| Execução | Resultado comprovado |
|---|---|
| Primeira execução após selecionar MTP | 14 testes: 10 aprovados, 4 falhas, 0 ignorados; saída 1. Os dois módulos vazios também retornaram código 8 |
| Execução após corrigir a configuração de testes | 14 testes: 14 aprovados, 0 falhas, 0 ignorados; saída 0; duração reportada de 2s 920ms |
| IntegrationTests | 14 casos reais, todos aprovados |
| Domain.Tests e Application.Tests | Zero casos, conforme o escopo da fundação; não representam regras de domínio/aplicação validadas |
| API em TestServer | Saúde nos dois aliases, OpenAPI/UI em Development, documentação ausente em Production, 400/404/405, exceções seguras e configuração inválida verificados |
| API em Kestrel | Não executada nesta correção, restrita ao executor e aos testes |

Correções adicionais necessárias, somente na configuração dos testes:

- Os quatro testes falharam inicialmente porque o logger do host tentou escrever no Windows Event Log e recebeu acesso negado. ApiFactory configura os providers do host de teste para console, sem dependência desse recurso do Windows. O logger da aplicação e as permissões do sistema não foram alterados; as asserções continuam exigindo os erros e formatos corretos.
- MTP retorna código 8 quando um módulo executa zero testes. Somente Domain.Tests e Application.Tests receberam `TestingPlatformCommandLineArguments` com `--ignore-exit-code 8`, pois ainda não possuem funcionalidades. Remover essa exceção ao adicionar os primeiros testes em cada projeto. Não se ignora código de falha de teste; IntegrationTests mantém o tratamento estrito, inclusive para zero casos.
- Nenhum teste artificial foi criado. Nenhuma dependência foi instalada/atualizada nesta correção e nenhum caso foi marcado como ignorado.

Referências: [seleção do executor no .NET 10](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-test?tabs=dotnet-test-with-mtp) e [configuração de códigos por projeto](https://learn.microsoft.com/en-us/dotnet/core/testing/microsoft-testing-platform-run-and-debug).

## Validação final — HTTP real em Kestrel

Executada em 2026-11-13, usando o assembly Release existente, sem restore ou compilação implícita. O diretório de trabalho do processo foi C:\FinanceFlow\src\FinanceFlow.Api, para carregar os appsettings da aplicação. Comando utilizado:

```powershell
# Diretório: C:\FinanceFlow\src\FinanceFlow.Api
$env:ASPNETCORE_ENVIRONMENT = 'Development'
dotnet bin/Release/net10.0/FinanceFlow.Api.dll --urls http://127.0.0.1:0
```

A porta 0 solicitou uma porta disponível ao sistema. O log confirmou `Now listening on: http://127.0.0.1:56502`, ambiente Development e content root do projeto Api. Para verificar a configuração fora de Development, foi iniciada uma segunda instância com o mesmo comando e ASPNETCORE_ENVIRONMENT=Production, que escutou em http://127.0.0.1:63696. Ambas ficaram limitadas ao loopback.

Requisições realizadas por System.Net.Http.HttpClient contra essas portas TCP, sem WebApplicationFactory/TestServer:

| Método e rota | Development | Production | Conteúdo validado |
|---|---|---|---|
| GET /health | 200 | 200 | application/json; `{"status":"healthy"}` |
| GET /health/live | 200 | 200 | application/json; mesmo resultado do alias |
| GET /openapi/v1.json | 200 | 404 | Dev: application/json, OpenAPI 3.1.1, título FinanceFlow API, versão v1, duas rotas de saúde; Prod: Problem Details |
| GET /swagger/index.html | 200 | 404 | Dev: text/html com Swagger UI e referência a index.js; Prod: Problem Details |
| GET /swagger/index.js | 200 | Não solicitado | application/javascript; configuração aponta para /openapi/v1.json |
| GET /swagger/swagger-ui.css | 200 | Não solicitado | text/css, corpo não vazio |
| GET /swagger/swagger-ui-bundle.js | 200 | Não solicitado | text/javascript, corpo não vazio |
| GET /missing-resource | 404 | 404 | application/problem+json; code=not_found |
| POST /health | 405 | 405 | application/problem+json; code=method_not_allowed e Allow: GET |
| GET /api/v1/__tests/failure | 404 | Não solicitado | Endpoint exclusivo dos testes não existe na API real |

Resultado: **10 verificações finais em Development e 6 em Production, todas aprovadas**. Antes da rodada final, uma checagem da validação esperava a URL do documento embutida no HTML do Swagger. A inspeção mostrou que essa versão usa index.js; a checagem foi corrigida para verificar o arquivo real. Isso não foi falha da API e não exigiu alteração de implementação. O HTML, scripts e CSS foram verificados por HTTP; interação visual em navegador não foi exercitada.

### Problem Details e logs

Nas respostas 404/405 foram conferidos Content-Type, type, title, status, instance, code e traceId não vazio. Não houve stackTrace, tipo de exceção interna ou caminho físico da aplicação nos corpos. A rota inexistente de testes também retornou 404, sem criar ou expor endpoint público de falha.

O cenário 500 e a validação 400 continuam comprovados pelos testes de integração anteriores, incluindo exceção sanitizada em Development e Production. Não foi provocado 500 artificial em Kestrel: o critério documentado exige esse cenário no host de testes. A validação real complementa essa evidência com os status 404/405 e endpoints técnicos.

Os logs de início e das requisições foram observados nas sessões supervisionadas. Não houve entradas fail/crit, exceção não tratada ou erro inesperado nas duas instâncias. Os registros informativos de 404/405 correspondem às requisições negativas esperadas.

### Encerramento e limites operacionais

O comando composto inicial de gerenciamento de processos foi rejeitado antes da execução pela política de aprovação. A execução supervisionada direta do assembly funcionou, sem alteração de permissões ou sandbox.

Ctrl+C enviado à sessão não encerrou a primeira instância neste terminal. O listener de Development foi identificado por netstat e associado ao PID 23288; a instância Production foi associada ao PID 15812. Foi aplicado Stop-Process somente aos objetos desses processos, confirmando nome dotnet e listener correspondente. WaitForExit confirmou saída, e netstat confirmou ausência de listeners nas portas 56502 e 63696. Nenhum outro processo foi interrompido.

As sessões de terminal retornaram código 1 após a terminação intencional dos processos; isso não foi falha de inicialização ou de requisição. Não se afirma encerramento gracioso por Ctrl+C. Não restou servidor desta validação em execução.

### Build e testes nesta rodada

Nenhum código da aplicação, projeto ou pacote foi alterado. Por isso, não foram repetidos restore, build ou testes de software. Permanecem as evidências anteriores: restore e build dos sete projetos informados pelo usuário no PowerShell externo; execução efetiva pelo agente de dotnet test em Release com compilação implícita, 14 aprovados, 0 falhas, 0 ignorados, saída 0. As 16 verificações HTTP desta rodada são adicionais, não novos casos xUnit.

Arquivos atualizados nesta rodada: este registro e README.md da raiz, para refletir a conclusão real. Sem commits, push, mudanças arquiteturais ou funcionalidades da Etapa 3.

## Histórico — tentativas anteriores à restauração externa

As falhas abaixo ocorreram na execução anterior da fundação e não descrevem o estado atual dos testes.

| Verificação | Resultado |
|---|---|
| Leitura do workspace e documentos | Executada |
| Consulta de versões pela API NuGet no terminal | Falha de conexão ao servidor remoto |
| dotnet restore FinanceFlow.slnx | Falhou por acesso negado ao NuGet.Config do usuário |
| Restore com --configfile NuGet.Config e caminho absoluto | Mesmo bloqueio de acesso ao diretório NuGet do usuário |
| dotnet build FinanceFlow.slnx --configuration Release --no-restore | Falhou com NETSDK1004: project.assets.json ausente nos sete projetos |
| dotnet test FinanceFlow.slnx --configuration Release --no-restore | Falhou com NETSDK1004 nos projetos de teste; nenhum caso chegou a executar |
| API em Kestrel e respostas HTTP | Não validadas: não há build executável confirmado |
| Referências de projeto | Verificadas por leitura dos XMLs, iguais ao ADR-001, sem ciclos |
| XML, JSON e links Markdown locais | Verificação estática concluída sem erros |
| Documentação da Etapa 1 | git diff confirmou ausência de alterações |

Erro determinante do restore (caminho do usuário anonimizado):

```text
Falha ao ler o NuGet.Config devido ao acesso não autorizado.
Caminho: C:\Users\<usuario>\AppData\Roaming\NuGet\NuGet.Config
Access to the path 'C:\Users\<usuario>\AppData\Roaming\NuGet' is denied.
```

O SDK/cache das tentativas foram direcionados a .local dentro do workspace. Não houve tentativa de ler segredos ou modificar o NuGet.Config pessoal, permissões NTFS ou sandbox. O bloqueio de leitura não permite concluir que a rede será suficiente depois de corrigido; o acesso HTTPS ao NuGet também precisa funcionar.

## Testes preparados

Health nos dois aliases; OpenAPI com título configurável e rotas dos controllers; UI em Development; documentação ausente em Production; erros 404/405; validação de corpo ausente/malformado; exceções sanitizadas em Development/Production; configuração inválida na inicialização. WebApplicationFactory dispensa banco e Docker.

Os dois projetos de testes de domínio/aplicação ainda não possuem casos porque não há funcionalidades nessas camadas. Ausência de testes nesses projetos não equivale a validação de regras futuras.

## Critérios finais da Etapa 2

- [x] Solução com quatro projetos e referências conforme ADR-001, sem dependências circulares.
- [x] Restore e build dos sete projetos aprovados, com origem da evidência discriminada acima.
- [x] Executor MTP configurado e 14 testes efetivamente aprovados.
- [x] API iniciada em Kestrel e endpoints de saúde/OpenAPI/Swagger verificados por HTTP real.
- [x] OpenAPI e Swagger indisponíveis fora de Development, conforme configuração.
- [x] Problem Details 404/405 no servidor real; 400/500 e configuração inválida no host de testes.
- [x] Logs observados sem erros inesperados e processos próprios encerrados.
- [x] README e evidências atualizados; nenhuma implementação da Etapa 3 antecipada.

Não há pendência bloqueante para encerrar a Etapa 2. O acesso de rede para restores futuros e a execução do workflow no GitHub não foram revalidados nesta rodada e não são apresentados como comprovados.

## Pendências não bloqueantes da arquitetura

Tipos de conta, confirmação em DELETE /users/me, collation e hospedagem continuam para as etapas correspondentes. HTTP local nesta fundação não transporta credenciais; HTTPS e configuração de proxy precisam estar resolvidos antes de expor autenticação/dados sensíveis. A licença MIT ainda é proposta para a preparação da release.
