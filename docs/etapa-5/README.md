# Etapa 5 — Lançamentos e saldos

## Entrega 5A — histórico de criação e saldos

Implementados somente criação e consulta de receitas/despesas e saldo individual atual/por corte. Base: `76e909b`/main limpa ao iniciar. O usuário confirmou 311 testes e CI aprovados para a Etapa 4; são evidências anteriores, não validação de 5A.

- Domain: FinancialTransaction, valores positivos e exatos, tipo, datas, descrição e validação de vínculos.
- Application: FinancialTransactionsService, contratos específicos, proprietário autenticado, calendário/TimeProvider e fingerprint v1.
- Infrastructure: store transacional RepeatableRead, SQL Server/EF, consultas agregadas, configuração e migration incremental.
- Api: POST/GET de transactions, GET de saldo, DTOs, parsing exato, erros seguros e OpenAPI/Bearer em Development.

Contas/categorias são lidas e protegidas até o commit. A criação e o snapshot de resposta são atômicos. Replay não depende do estado atual dos vínculos. O saldo é calculado, inclui retroativos e contas inativas e retorna zero antes da abertura. As somas são feitas no SQL; a soma final do saldo inicial é decimal no C#, sem truncamento de centavos. Nenhum CurrentBalance é armazenado.

Sem PUT/DELETE de transações, transferências, extratos, relatórios ou auditoria de versões. IsDeleted e DeletedAtUtc integram o modelo aprovado; nenhuma operação de exclusão foi antecipada. Estados excluídos preparados somente nos bancos descartáveis dos testes são ignorados nos cálculos e consultas.

**Status: implementação local validada externamente pelo usuário: 386 testes aprovados, 0 falhas e 0 ignorados**, incluindo SQL Server/Testcontainers no PowerShell. A revisão técnica consolidada dos 32 arquivos contra `76e909b`, incluindo os novos ainda não rastreados, foi aprovada sem problemas bloqueantes. As falhas históricas da fixture por Docker inacessível no sandbox permanecem registradas, separadas desse resultado externo; não houve skips/fallbacks.

A auditoria NuGet continua **inconclusiva devido aos avisos NU1900**. A aprovação dos testes não comprova ausência de vulnerabilidades. No fechamento inicial a publicação/CI ainda estavam pendentes; o usuário posteriormente confirmou **f9e7d12 e workflow #7 aprovado**, com 386 testes aprovados. Esses resultados pertencem a 5A, não às alterações locais de 5B. Consulte [validação detalhada e comandos de reprodução](entrega-5a-validacao.md).

## Entrega 5B local — edição e exclusão lógica

Implementados somente PUT e DELETE de /api/v1/transactions/{id}. Exigem JWT e If-Match forte, retornam 204 vazio com novo ETag e no-store. PUT substitui os campos editáveis; DELETE preserva os dados e marca a exclusão. Repetições em recurso excluído retornam 404. Correção descritiva mantém vínculos históricos inativos; alterações financeiras exigem contas ativas, e reassociação exige categoria nova ativa.

A versão do cliente é aplicada ao UPDATE SQL. PUT protege as referências até o commit com RepeatableRead; a leitura inicial da transação ocorre antes desse isolamento para manter a concorrência otimista entre editores. Há rollback e classificação 412/404, sem retry. Saldos e replay do POST permanecem baseados nas regras de 5A. Nenhuma migration nova é necessária. UPDATE é concedido apenas nas nove colunas necessárias; imutáveis, DELETE físico, DDL e registros idempotentes permanecem protegidos.

**Implementada localmente e validada externamente pelo usuário:** no PowerShell com SQL Server/Testcontainers, **461 testes aprovados, zero falhas e zero ignorados**, em aproximadamente 1m39s. Foram acrescentados 75 testes: 16 Domain, 14 Application, nove contratos/modelo e 36 SQL. A revisão técnica consolidada dos 22 arquivos contra `f9e7d12`, incluindo os sete novos, foi aprovada sem problemas bloqueantes.

O histórico do Codex permanece separado: 285 aprovados e 176 falhas na fixture por Docker inacessível no sandbox, sem ignorados; restore/build passaram com cinco NU1900. A confirmação externa encerra a pendência da regressão SQL, sem alterar esse histórico nem comprovar uma nova execução de build/auditoria pelo Codex. A auditoria NuGet continua **inconclusiva devido aos NU1900**. Não há publicação ou CI aprovada de 5B. [Regras, evidências e procedimento de reprodução](entrega-5b-validacao.md).

Migrations/provisionamento são explícitos; nenhuma ação foi executada em banco persistente nesta entrega. Sem stage, commit ou push pelo Codex. Não iniciar 5C/6 sem autorização.

## Publicação de 5B e consolidação 5C

Posteriormente aos registros acima, o usuário confirmou 5B publicada em **b548f4f**, com **461 testes aprovados, 0 falhas/ignorados e CI #8 — Success**. 5A permanece publicada em **f9e7d12**, com CI #7 aprovada. Essas evidências pertencem às entregas anteriores; as pendências históricas acima não representam o estado atual da publicação de 5B.

A **Entrega 5C está implementada, validada externamente e com revisão técnica aprovada**, após auditoria dos critérios da Etapa 5 e 16 casos SQL adicionais para reconciliação independente, calendário financeiro, recuperação de falhas/idempotência e concorrência com desativação da origem. A revisão inicial preservou produção. Depois, o usuário confirmou **477 executados, 475 aprovados, duas falhas e zero ignorados**; foram corrigidos localmente o tratamento do deadlock encapsulado pelo EF no POST e a captura privada do erro de leitura no teste de overflow. Na nova execução externa com Docker e SQL Server/Testcontainers, o usuário confirmou **477 aprovados, zero falhas e zero ignorados**, em **1m50s**. Contratos, privilégios, dependências e schema permanecem; não há nova migration ou endpoint.

O histórico do Codex foi preservado: restore inicial e build final passaram com cinco NU1900 e nenhum erro ou warning C#/analisadores no build final; a suíte no sandbox teve **477 casos: 285 aprovados, 192 falhas na fixture Docker e 0 ignorados**, e os dois testes isolados também ficaram bloqueados na fixture. A aprovação externa de 477/477 após as correções é evidência fornecida pelo usuário, não uma execução SQL do Codex. A revisão consolidada dos **oito arquivos (cinco modificados e três novos)** não encontrou problemas bloqueantes. A auditoria NuGet permanece **inconclusiva pelos NU1900**, inclusive na confirmação externa; não se afirma um novo build sem warnings. Consulte [matriz de cobertura, histórico das falhas e validação externa final](entrega-5c-validacao.md).

A validação externa de 5C está confirmada, mas a Etapa 5 ainda não está oficialmente encerrada: faltam publicação autorizada e CI de 5C aprovada. Sem stage, commit, push ou alterações em banco persistente. Etapa 6 não iniciada.
