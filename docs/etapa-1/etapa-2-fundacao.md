# Proposta da Etapa 2 — Fundação da solução

Status: proposta detalhada aprovada como planejamento, aguardando autorização explícita para execução. Este arquivo não inicia a etapa.

## Objetivo

Criar uma estrutura mínima compilável e testável, com pipeline HTTP técnico e limites de dependência. Sem autenticação implementada, finanças ou banco nesta etapa.

## Pré-requisitos

1. Etapa 1 documental concluída e arquitetura aprovada.
2. Autorização explícita para iniciar a Etapa 2.
3. Acesso de leitura/escrita no workspace e instruções locais verificadas novamente.
4. Verificar SDK .NET 10 e versões compatíveis dos pacotes antes de qualquer instalação. Se faltar ferramenta, informar o impedimento e tratar a instalação separadamente.

## Componentes previstos

| Local futuro | Finalidade |
|---|---|
| Solução na raiz, formato suportado pelo SDK | Agregar projetos; confirmar formato .sln/.slnx na implementação |
| src/FinanceFlow.Api | Controllers, Program, erros, configuração e OpenAPI |
| src/FinanceFlow.Application | Projeto com referência ao Domain, sem serviços vazios |
| src/FinanceFlow.Domain | Projeto sem dependência de infraestrutura |
| src/FinanceFlow.Infrastructure | Projeto referenciando Application/Domain; sem DbContext ainda |
| tests/FinanceFlow.Domain.Tests | Estrutura de testes unitários; regras chegam nas fases financeiras |
| tests/FinanceFlow.Application.Tests | Estrutura de testes de casos de uso; sem testes artificiais |
| tests/FinanceFlow.IntegrationTests | WebApplicationFactory para pipeline técnico |

Prever Nullable, analisadores básicos e referências centralizadas de forma compatível com o SDK, sem adicionar ferramentas pela aparência. xUnit e testes HTTP exigirão dependências apenas quando a execução desta etapa estiver autorizada.

## Sequência de implementação futura

1. Inspecionar SDK, workspace, instruções e eventual repositório existente.
2. Criar solução/projetos e referências conforme ADR-001.
3. Configurar controllers e serialização. Não criar controller financeiro fictício.
4. Configurar tratamento de erro global e Problem Details sem informações internas.
5. Adicionar /health/live, geração OpenAPI e Swagger UI em Development.
6. Adicionar Options tipadas apenas para configuração utilizada; não exigir segredo JWT antes de existir autenticação.
7. Criar testes reais de host, health, OpenAPI, erros e configuração.
8. Executar build/testes, registrar resultados e atualizar instruções de execução.
9. Preparar workflow de build/testes somente se o repositório local estiver disponível e a criação correspondente autorizada. Não criar/pushar repositório remoto ou commits implicitamente.

## Fora desta etapa

DbContext, migrations, Identity/JWT, banco, Docker em execução, entidades financeiras, repositórios, idempotência, filas, cache e seed. Não criar readiness falso para dependência inexistente, nem teste vazio apenas para elevar contagem.

Arquivos fora de docs/etapa-1 serão necessários na Etapa 2, mas somente após a autorização específica. A autorização da Etapa 1 não se estende a eles.

## Aceitação

- Build da solução com dependências corretas.
- Domain sem referências EF/Identity/ASP.NET; Application sem Infrastructure/Api.
- API inicia com configuração válida; health/live responde 200.
- OpenAPI é gerado e UI funciona no ambiente previsto.
- Erro inesperado vira Problem Details seguro. Induzir erro pelo host de teste, sem endpoint público permanente de falha.
- Configuração utilizada inválida falha de modo claro, sem exigir configurações de funcionalidades futuras.
- WebApplicationFactory funciona sem SQL Server ou Docker.
- Documentação corresponde às verificações reais; nenhum requisito financeiro é declarado implementado.

## Riscos e saída

Riscos: SDK ausente, incompatibilidade de versão, configuração do host, mudanças do template e disponibilidade do repositório para CI. Resolver com a menor mudança compatível, sem antecipar abstrações.

Entrega final da etapa futura: arquivos alterados, responsabilidades, comandos/checks realmente executados, resultados e pendências. Não avançar automaticamente para banco ou autenticação.
