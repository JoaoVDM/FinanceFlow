# FinanceFlow API

API REST de gestão financeira pessoal desenvolvida como portfólio profissional de backend em C# e ASP.NET Core. A evolução prioriza regras financeiras confiáveis, isolamento entre usuários e testes automatizados.

## Estado do projeto

Etapa 1: arquitetura documentada em [docs/etapa-1](docs/etapa-1/README.md). **Etapa 2 concluída:** solução com sete projetos compilada, 14 testes automatizados aprovados e 16 verificações HTTP finais em Kestrel, nos ambientes Development e Production. A origem e os limites das evidências estão em [docs/etapa-2/validacao.md](docs/etapa-2/validacao.md).

Ainda não há autenticação, banco, entidades financeiras ou endpoints de negócio. Identity, JWT, EF Core, SQL Server e Docker pertencem às próximas etapas. Esta base não deve ser apresentada como serviço financeiro pronto para uso público.

## Requisitos e instalação

- .NET SDK estável da faixa 10.0.4xx; global.json aceita patches dessa faixa, incluindo 10.0.404.
- Git e um editor como VS Code.
- Acesso HTTPS ao NuGet para restaurar dependências. Docker e SQL Server não são necessários nesta etapa.

Instale o SDK pelo [site oficial do .NET](https://dotnet.microsoft.com/download/dotnet/10.0) e confirme `dotnet --version`. No PowerShell, a partir da pasta do projeto:

```powershell
dotnet restore FinanceFlow.slnx --configfile NuGet.Config
dotnet build FinanceFlow.slnx --configuration Release --no-restore
dotnet test FinanceFlow.slnx --configuration Release --no-build --no-restore
```

NuGet.Config usa somente a fonte pública nuget.org e mantém os pacotes em `.local/packages`, ignorado pelo Git. Versões diretas são fixadas em Directory.Packages.props. Não há instalação de ferramentas globais.

## Executar a API

```powershell
dotnet run --project src/FinanceFlow.Api --launch-profile http
```

O perfil inicia Development em `http://localhost:5080`, sem abrir navegador. Para encerrar, use Ctrl+C.

| Endpoint | Resultado esperado |
|---|---|
| GET /health | 200 e `{"status":"healthy"}` |
| GET /health/live | Alias técnico da mesma verificação de processo |
| GET /openapi/v1.json | Documento OpenAPI em Development |
| GET /swagger/index.html | Swagger UI em Development |

```powershell
Invoke-RestMethod http://localhost:5080/health
Invoke-RestMethod http://localhost:5080/health/live
Invoke-RestMethod http://localhost:5080/openapi/v1.json
```

Não há readiness fictício: nenhuma dependência externa existe ainda. Saúde é técnica e não usa o prefixo de negócio `/api/v1`. Não foram criadas rotas financeiras vazias para demonstrar versionamento.

## Ambientes e configuração

appsettings.json contém configurações não sensíveis; appsettings.Development.json ajusta logs. Variáveis de ambiente podem sobrescrever valores, usando `__` como separador: `OpenApi__Title` define o título da documentação e é validado na inicialização. `ASPNETCORE_ENVIRONMENT` define o ambiente; fora de Development, OpenAPI e Swagger não são expostos.

Para executar sem o perfil de desenvolvimento:

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Production'
dotnet run --project src/FinanceFlow.Api --no-launch-profile --urls http://localhost:5080
```

AllowedHosts limita os nomes de host locais; outra hospedagem deverá configurá-lo explicitamente. Não habilitamos CORS amplo nem adicionamos credenciais de exemplo. O perfil HTTP é local e só expõe endpoints técnicos sem credenciais; HTTPS e configuração do proxy são obrigatórios antes de expor autenticação ou dados sensíveis. Não existe terminação TLS produtiva nesta etapa.

Segredos futuros devem ser fornecidos por mecanismo apropriado ao ambiente, nunca pelo Git. `.env`, certificados, chaves, logs e configuração local são ignorados. A fundação não exige um segredo JWT para iniciar.

## Arquitetura

```text
FinanceFlow.slnx
src/
  FinanceFlow.Api/              HTTP, controllers, erros, OpenAPI e composição
  FinanceFlow.Application/      Futuramente, casos de uso e interfaces específicas
  FinanceFlow.Domain/           Futuramente, entidades e invariantes
  FinanceFlow.Infrastructure/   Futuramente, persistência e serviços externos
tests/
  FinanceFlow.Domain.Tests/
  FinanceFlow.Application.Tests/
  FinanceFlow.IntegrationTests/
docs/
  etapa-1/                     Planejamento aprovado e sete ADRs
  etapa-2/                     Implementação e evidências da fundação
```

Referências: Api → Application/Infrastructure; Application → Domain; Infrastructure → Application/Domain; Domain sem referências de projeto ou pacotes de infraestrutura. Os projetos sem funcionalidades permanecem sem classes artificiais. Não há CQRS completo, MediatR, repositório genérico ou mapeamento automático.

## Erros e testes

Tratamento global de exceções e Problem Details em todos os ambientes. Respostas de erro incluem status, title, instance, code e traceId; validação acrescenta errors. Stack traces e mensagens internas de exceção não são retornadas ao cliente. Erros são registrados no servidor; não registrar credenciais/corpos sensíveis quando as próximas funcionalidades forem adicionadas.

Os testes efetivos usam xUnit e WebApplicationFactory, sem banco ou Docker, cobrindo saúde, OpenAPI/UI por ambiente, validação, 404/405, exceções e configuração inválida. O global.json seleciona Microsoft.Testing.Platform para execução com o SDK .NET 10. Controllers que provocam falhas existem somente no assembly de testes e não são publicados com a API. Os projetos Domain.Tests e Application.Tests estão preparados, mas ainda não possuem casos, pois as respectivas regras não existem. Somente esses projetos toleram temporariamente o código 8 de zero testes; remover a configuração ao adicionar seus primeiros casos. IntegrationTests mantém a verificação estrita.

O workflow em .github/workflows/build.yml executará restore, build e testes quando as alterações forem publicadas pelo responsável. A criação local do workflow não comprova uma execução no GitHub.

## Próximos passos

A fundação está validada. A Etapa 3, identidade e persistência básica, depende de autorização específica e ainda não foi iniciada. Veja [roadmap](docs/etapa-1/backlog-e-roadmap.md), [contratos planejados](docs/etapa-1/contratos-rest.md) e [pendências](docs/etapa-1/riscos-e-referencias.md).

Licença MIT é a proposta registrada para a preparação da release; nenhum arquivo de licença foi incluído nesta etapa. Resultados de testes e prontidão devem ser avaliados pelas evidências, sem badges de sucesso não verificados.
