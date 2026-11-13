# FinanceFlow API — Etapa 1: documentação e planejamento

Status: Etapa 1 concluída, com arquitetura aprovada na conversa e documentação gravada e verificada. A Etapa 2 não está autorizada por esta entrega.

## Objetivo e limites

O FinanceFlow API é uma API REST de gestão financeira pessoal, open source e destinada a portfólio profissional no GitHub e na Vitrine.dev. A implementação será incremental, priorizando regras financeiras confiáveis, segurança e manutenção.

Esta entrega contém somente documentação em `docs/etapa-1/`. Não cria solução .NET, projetos, código C#, dependências, migrations, banco, contêineres, serviços, repositório Git ou publicação. Testes de software descritos aqui são requisitos futuros, não resultados obtidos.

## Navegação

| Documento | Conteúdo |
|---|---|
| [ADR-001](adr/001-arquitetura-e-dependencias.md) | Arquitetura, projetos, dependências e abstrações |
| [ADR-002](adr/002-autenticacao-e-autorizacao.md) | Identity, JWT, revogação, isolamento e segurança |
| [ADR-003](adr/003-modelagem-financeira.md) | Entidades e ciclo de vida financeiro |
| [ADR-004](adr/004-calculo-de-saldos.md) | Saldo atual, histórico e patrimônio |
| [ADR-005](adr/005-transferencias.md) | Representação e atomicidade das transferências |
| [ADR-006](adr/006-datas-e-valores.md) | BRL, precisão monetária e datas |
| [ADR-007](adr/007-concorrencia-e-idempotencia.md) | Concorrência, transações e repetição de operações |
| [Modelo de domínio](modelo-de-dominio.md) | Atributos, relacionamentos, constraints e invariantes |
| [Contratos REST](contratos-rest.md) | Rotas, DTOs, filtros, cabeçalhos e erros |
| [Backlog e roadmap](backlog-e-roadmap.md) | Oito etapas, entregáveis, dependências e riscos |
| [Critérios de aceitação](criterios-de-aceitacao.md) | Matriz de validação por etapa |
| [Etapa 2 — Fundação](etapa-2-fundacao.md) | Proposta detalhada, sem execução |
| [Riscos e referências](riscos-e-referencias.md) | Pendências, limites e fontes técnicas |

## Stack e premissas

C#, .NET 10 / ASP.NET Core Web API, EF Core 10, SQL Server, ASP.NET Core Identity, JWT, Docker Compose, xUnit, WebApplicationFactory, Testcontainers e OpenAPI / Swagger. Desenvolvimento em Windows e VS Code; execução em contêineres Linux planejada.

Monólito modular com `FinanceFlow.Api`, `FinanceFlow.Application`, `FinanceFlow.Domain` e `FinanceFlow.Infrastructure`. Uso individual, lançamentos manuais, BRL, calendário financeiro de São Paulo, sem movimentação bancária real.

## Escopo aprovado

Obrigatório no MVP:

- Cadastro, login, perfil próprio, alteração de senha, logout global e desativação do usuário.
- Contas financeiras e categorias particulares de receita/despesa, com desativação.
- Cadastro, consulta, edição e exclusão lógica de receitas e despesas.
- Transferências entre contas próprias, consulta e cancelamento.
- Saldos atuais e históricos, extratos e relatórios por período/categoria.
- Paginação, filtros, ordenação, Problem Details, OpenAPI e testes automatizados.

Adiado: refresh tokens, sessões por dispositivo, MFA, login externo, envio de e-mail, confirmação e recuperação de acesso, agendamentos, recorrência, parcelamento, metas, exportação/importação, estornos vinculados, auditoria completa e exclusão definitiva. Confirmação e recuperação de acesso devem ser reavaliadas antes de incentivar uso contínuo com dados reais.

Fora do escopo inicial: Open Finance, pagamentos/Pix, conciliação, faturas de cartão, investimentos, tributação, contabilidade empresarial, contas compartilhadas, multimoeda, microsserviços, event sourcing, filas e frontend completo.

## Refinamentos que substituem o planejamento inicial

- HS256 é a escolha para uma única API demonstrativa, com segredo aleatório externo; emissão própria não constitui OAuth/OIDC completo.
- Manter tokens de 15 minutos e revogação global por versão do usuário, sem sessões por dispositivo.
- Criar interfaces específicas conforme os casos de uso; não antecipar repositório genérico ou executor transacional genérico.
- Usar transação automática do EF para gravação simples e transação explícita curta para validações relacionadas à gravação. `RepeatableRead` será localizado, não global.
- Não exigir RCSI global. Snapshot de leitura entra apenas quando uma resposta financeira com múltiplos comandos exigir consistência pontual.
- `rowversion` entra com a primeira alteração persistida; idempotência com a primeira criação que tenha efeito financeiro, inclusive saldo inicial de conta.

## Como evoluir esta documentação

Os ADRs registram decisões aprovadas, não funcionalidades entregues. Mudanças de decisão devem explicitar o ADR substituído e o motivo. Questões não resolvidas constam em [riscos e referências](riscos-e-referencias.md); não devem ser resolvidas silenciosamente na implementação.

A conclusão documental da Etapa 1 não autoriza iniciar a Etapa 2.

## Verificação desta entrega

Foram verificados os 14 arquivos esperados, todos não vazios, sem links Markdown locais quebrados e sem arquivos criados fora de docs/etapa-1. A revisão documental conferiu dependências, autenticação, políticas financeiras, contratos e sequência de implementação. Pendências de implementação permanecem explícitas em riscos-e-referencias.md.

Essa verificação é documental: não foram executados build, testes de software, migrations, banco ou contêineres.
