# Etapa 5 — Lançamentos e saldos

## Entrega 5A local

Implementados somente criação e consulta de receitas/despesas e saldo individual atual/por corte. Base: `76e909b`/main limpa ao iniciar. O usuário confirmou 311 testes e CI aprovados para a Etapa 4; são evidências anteriores, não validação de 5A.

- Domain: FinancialTransaction, valores positivos e exatos, tipo, datas, descrição e validação de vínculos.
- Application: FinancialTransactionsService, contratos específicos, proprietário autenticado, calendário/TimeProvider e fingerprint v1.
- Infrastructure: store transacional RepeatableRead, SQL Server/EF, consultas agregadas, configuração e migration incremental.
- Api: POST/GET de transactions, GET de saldo, DTOs, parsing exato, erros seguros e OpenAPI/Bearer em Development.

Contas/categorias são lidas e protegidas até o commit. A criação e o snapshot de resposta são atômicos. Replay não depende do estado atual dos vínculos. O saldo é calculado, inclui retroativos e contas inativas e retorna zero antes da abertura. As somas são feitas no SQL; a soma final do saldo inicial é decimal no C#, sem truncamento de centavos. Nenhum CurrentBalance é armazenado.

Sem PUT/DELETE de transações, transferências, extratos, relatórios ou auditoria de versões. IsDeleted e DeletedAtUtc integram o modelo aprovado; nenhuma operação de exclusão foi antecipada. Estados excluídos preparados somente nos bancos descartáveis dos testes são ignorados nos cálculos e consultas.

**Status: implementação local validada externamente pelo usuário: 386 testes aprovados, 0 falhas e 0 ignorados**, incluindo SQL Server/Testcontainers no PowerShell. A revisão técnica consolidada dos 32 arquivos contra `76e909b`, incluindo os novos ainda não rastreados, foi aprovada sem problemas bloqueantes. As falhas históricas da fixture por Docker inacessível no sandbox permanecem registradas, separadas desse resultado externo; não houve skips/fallbacks.

A auditoria NuGet continua **inconclusiva devido aos avisos NU1900**. A aprovação dos testes não comprova ausência de vulnerabilidades. Não há aprovação de CI ou publicação de 5A confirmada. Consulte [validação detalhada e comandos de reprodução](entrega-5a-validacao.md).

Migrations/provisionamento são explícitos e não foram executados em banco persistente. Nenhum stage, commit ou push foi realizado. Não iniciar 5B/5C/6 sem autorização.
