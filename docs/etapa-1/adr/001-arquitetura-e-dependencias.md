# ADR-001 — Arquitetura e dependências

Status: aprovado na revisão arquitetural. Escopo: MVP. Implementação: futura.

## Contexto

O projeto precisa demonstrar engenharia de software sem carregar infraestrutura de uma aplicação distribuída. A separação aprovada em quatro projetos deve proteger as regras financeiras e permitir testes sem HTTP ou banco quando apropriado.

## Decisão

Adotar monólito modular organizado por funcionalidades dentro das camadas:

| Projeto | Responsabilidade | Referências de projeto |
|---|---|---|
| `FinanceFlow.Domain` | Entidades, invariantes e operações financeiras | Nenhuma |
| `FinanceFlow.Application` | Casos de uso, proprietário atual e coordenação | Domain |
| `FinanceFlow.Infrastructure` | EF Core, SQL Server, Identity e tokens | Application e Domain |
| `FinanceFlow.Api` | HTTP, contratos, autenticação e composição | Application e Infrastructure |

A referência Api → Infrastructure serve à composição. Controllers chamam serviços da Application; não consultam DbContext diretamente. Domain não conhece ASP.NET Core, EF Core, Identity ou configuração.

Organizar Application por Users, Accounts, Categories, Transactions, Transfers e Reports. Os nomes de interfaces de persistência serão definidos junto à necessidade concreta; consultas podem retornar modelos de leitura da Application sem expor `IQueryable`.

Separar DTO HTTP e entrada de caso de uso apenas quando isso representar responsabilidades diferentes. Evitar copiar mecanicamente toda estrutura entre camadas. Validação estrutural fica na API; invariantes não dependem dela e devem ser protegidos no domínio.

## Bibliotecas e abstrações

- Princípios de Clean Architecture, sem impor todo um catálogo de padrões.
- Sem CQRS completo, MediatR, repositório genérico, AutoMapper ou FluentValidation obrigatório.
- Separar consultas e alterações em métodos claros, sem barramento.
- Mapeamentos manuais e projeções explícitas.
- DbContext atua como unidade de trabalho. Não construir outro rastreador ou executor transacional genérico.
- Interfaces específicas na Application para acesso externo necessário, com implementação na Infrastructure.
- Sem classe-base genérica ou interface por classe apenas por convenção.

## Composição e configuração

DbContext, repositórios e serviços de aplicação serão Scoped. Nunca compartilhar contexto entre tarefas paralelas. Usar `TimeProvider` para tempo testável e uma abstração de usuário atual sem dependência de HTTP na Application.

Options tipadas, validadas na inicialização quando utilizadas. `appsettings` contém somente configuração não sensível. User Secrets no desenvolvimento fora do contêiner; segredos fornecidos pelo ambiente na hospedagem. Métodos de registro por projeto serão criados conforme houver serviços a registrar, sem métodos vazios antecipados.

## Alternativas consideradas

- Projeto único: menor configuração, mas mistura responsabilidades e enfraquece a demonstração dos limites.
- Clean Architecture com comando/handler/repositório para toda operação: organização maior que a necessidade do MVP.
- Microsserviços: transações e operação distribuídas sem benefício para o escopo.

## Consequências e revisão

Há custo de quatro projetos e de algumas portas de persistência. Em troca, domínio e aplicação permanecem testáveis e os contratos de infraestrutura ficam explícitos. As camadas não prometem troca gratuita de banco.

Reavaliar bibliotecas somente diante de repetição ou problema demonstrável. A fundação não deve criar abstrações financeiras vazias.

Relacionados: [modelo](../modelo-de-dominio.md), [roadmap](../backlog-e-roadmap.md) e [Etapa 2](../etapa-2-fundacao.md).
