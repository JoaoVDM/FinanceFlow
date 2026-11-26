# Etapa 6 — Transferências e extratos

## Histórico da 6A

Conforme confirmação do usuário, a Etapa 5 foi concluída/publicada em **f0969b8**, com **477 testes locais aprovados, zero falhas/ignorados e CI #9 aprovada**. 5A foi publicada em f9e7d12/CI #7; 5B em b548f4f/CI #8. São evidências externas anteriores, não validação de 6A pelo Codex.

**Entrega 6A validada externamente e aprovada para commit.** A execução Docker Linux de 20/11/2026 comprovou **557 aprovados, zero falhas, zero ignorados, zero erros de runner e código de saída zero**, com os três XMLs conferidos pelo Codex. Inclui somente criação e consulta individual de transferências entre contas próprias e seus efeitos nos saldos calculados. A migration incremental é `20261120000100_TransfersDelivery6A`. Publicação e CI da 6A ainda não ocorreram.

| Método | Rota | Contrato |
|---|---|---|
| POST | /api/v1/transfers | JWT e Idempotency-Key; 201 com snapshot, Location e ETag |
| GET | /api/v1/transfers/{id} | JWT; 200/ETag somente para recurso próprio; 404 genérico para alheio/inexistente |

Uma linha Transfer representa os dois efeitos simétricos, sem FinancialTransaction adicional. O saldo individual usa abertura + receitas - despesas + entradas de transferências - saídas, por corte inclusivo. SQL faz os agregados em um comando; a soma final em decimal verificado no C# preserva centavos. Contas inativas permanecem nas posições; canceladas não participam dos cálculos.

Criação e snapshot idempotente são atômicos. A operação é `transfers.create.v1`, separada por usuário/operação/chave. Contas são lidas em ordem por Id sob RepeatableRead até o commit; replay recupera o snapshot original, inclusive depois de desativação. Não há retry automático nem saldo disponível mínimo.

A API recebe **SELECT/INSERT somente** em Transfers. Migration e provisionamento são explícitos; nada foi aplicado ao banco persistente. Não há UPDATE/DELETE de Transfers nesta entrega.

## Validações e próximos passos

O histórico do Codex permanece separado: restore/build com cinco NU1900, uma execução com **327 aprovados e 230 falhas na fixture Docker**, e tentativa posterior com **217 aprovados (Domain/Application)** antes de o Controle de Aplicativo bloquear o módulo de integração (`0x800711C7`). Não foram alteradas proteções para contornar esses bloqueios.

Externamente, o usuário executou a suíte SQL em Docker Linux: primeiro **555 aprovados/duas falhas**, depois **556 aprovados/uma falha**. Foram corrigidas somente a identidade usada no DENY da preparação de readiness e a captura interna da exceção de colisão de PK. A execução final **`.local/6a-docker-results/20261120-224229-183-17da7b`** comprovou **557/557**, incluindo os **477 casos anteriores e 80 novos**. Restore, build e módulos retornaram zero; o build desse log teve zero erros/warnings. A auditoria técnica dos 33 arquivos não encontrou impedimentos ao commit. A auditoria NuGet anterior permanece inconclusiva; a ausência de NU1900 nesse log não equivale a uma nova auditoria de vulnerabilidades conclusiva. Não há publicação ou CI de 6A aprovada.

Consulte [modelo, testes, garantias, limitações e comandos](entrega-6a-validacao.md). A Etapa 6 não está concluída: cancelamento pertence à **6B**; extratos e listagem de transferências à **6C**. Não há PUT/PATCH, listagem, cancelamento, extrato, relatório, câmbio ou integração bancária nesta implementação. Os campos de cancelamento apenas preparam o modelo aprovado.

Sem stage, commit, push, alteração de credenciais ou operações em banco/volume persistente.

## Publicação da 6A e implementação local da 6B

Posteriormente, o usuário confirmou **6A publicada em 6050035**, com **557 testes aprovados** e GitHub Actions **44857771237 — success**. Essa é a baseline da **Entrega 6B — Cancelamento lógico**, agora **aprovada para commit** após validação externa **613/613** e auditoria final da correção P2. A 6B permanece local, sem publicação ou CI própria aprovada; a auditoria NuGet conclusiva continua pendente.

DELETE `/api/v1/transfers/{id}` exige JWT e If-Match forte, não aceita corpo e retorna 204 vazio com novo ETag/no-store. Cancela a única linha original; contas inativas e saldo negativo não impedem a operação. Não altera dados financeiros nem snapshots de criação. Recursos alheios/inexistentes/cancelados retornam 404; versão antiga em recurso ativo, 412. A versão do cliente participa do UPDATE SQL e somente o commit permite sucesso.

Sem nova migration: schema/snapshot já atendem. O provisionamento explícito adiciona UPDATE somente em IsCanceled/CanceledAtUtc/UpdatedAtUtc; continuam negados UPDATE financeiro, DELETE físico, DDL e privilégios excessivos. Nada foi aplicado a banco persistente.

Foram acrescentados **46 casos**, preservando os 557 anteriores. O histórico do sandbox permanece: restore/build com cinco NU1900 e regressão com **487 casos: 228 aprovados, 259 falhas na fixture Docker e zero ignorados**; os 116 Domain foram bloqueados antes de iniciar pelo Smart App Control. Nenhuma proteção foi alterada para contornar os bloqueios.

Posteriormente, o usuário concluiu a execução externa **20261121-001714-582-bfda5d**, em Docker Linux/SQL Server/Testcontainers: **603 executados/aprovados, zero falhas, zero ignorados e zero erros de runner**, distribuídos em **116 Domain, 115 Application e 372 Integration**. O Codex conferiu os três XMLs e todos os códigos de saída zero, incluindo Docker; restore/build também passaram, com zero warnings no log dessa execução. Naquela auditoria, os 21 arquivos apresentaram um achado de gravidade média: deadlock SQL 1205 nas consultas de cancelamento fora do filtro poderia resultar em 500, em vez de 503. Na auditoria a correção foi apenas recomendada, preservando produção/testes. A ausência de NU1900 nesse log não substitui uma auditoria de vulnerabilidades conclusiva; a consulta anterior permanece registrada como inconclusiva.

**Correção P2 implementada posteriormente:** o filtro SQL restrito agora abrange a operação de cancelamento inteira, mantendo leitura otimista fora da transação, RowVersion e 404/412/204. Foram adicionados dez testes para 1205/-2 nas duas consultas, falhas inesperadas e propagação de cancelamento. Restore/build locais passaram com cinco NU1900. A regressão disponível executou **497 casos: 228 aprovados, 269 falhas de fixture Docker e zero ignorados**, com 116 Domain bloqueados no carregamento. Naquele momento, a nova suíte de **613** ainda dependia de validação externa; os 603 resultados anteriores não comprovavam a correção P2.

A primeira execução externa do P2, **20261121-005532-270-9f8c53**, apresentou **611 aprovados e duas falhas**: a instrumentação exigia InvalidOperationException, mas observou SqlException direta com código 1205. A asserção foi corrigida para aceitar somente as duas formas justificadas, mantendo o número SQL exato e todas as verificações HTTP/financeiras. A tentativa seguinte no sandbox aprovou 115 Application; Domain e Integration ficaram bloqueados pelo Smart App Control 0x800711C7, sem alteração das proteções.

**Validação externa final pelo usuário:** a execução Docker Linux/SQL Server/Testcontainers **20261121-020304-418-94cdd6** comprovou **613 executados/aprovados: 116 Domain, 115 Application e 382 Integration**, zero falhas, ignorados e erros do runner. O Codex conferiu os três XMLs e as saídas zero de restore, build, módulos e Docker. Os dez casos P2 passaram, incluindo os dois deadlocks anteriormente falhos e suas asserções posteriores. A auditoria final dos **22 arquivos (14 modificados e oito novos)** não identificou impedimentos técnicos; **6B aprovada para commit**. Somente documentação foi atualizada nesta auditoria, com staging vazio e git diff --check aprovado. A auditoria NuGet continua inconclusiva; aprovação funcional não equivale à consulta de vulnerabilidades concluída.

Para reproduzir, execute `& C:\FinanceFlow\scripts\Test-DockerSuite.ps1`, que preserva logs/XMLs em `.local/6b-docker-tests.log` e `.local/6b-docker-results/`. Consulte [garantias, testes, resultados e auditoria final da 6B](entrega-6b-validacao.md). A 6B permanece não publicada e sem CI própria aprovada. A 6C (listagem/extratos) não foi iniciada; a Etapa 6 não está concluída. Sem stage, commit ou push pelo Codex.

## Publicação da 6B e implementação local da 6C — 22/11/2026

O usuário confirmou posteriormente **6B publicada em 1cfb556**, com **613/613 aprovados** e **CI #11, execução 44870149140 — success**. Essa é a baseline conferida da 6C. O histórico acima registra as auditorias e execuções anteriores à publicação; seus resultados não são atribuídos à nova entrega.

A **implementação inicial local da 6C ficou pronta para validação externa**: GET `/api/v1/transfers` com filtros/paginação/ordenação e GET `/api/v1/accounts/{id}/statement` com as quatro naturezas, abertura, contribuição de abertura no período, totais completos e fechamento. Snapshot mantém conta/agregados/página na mesma visão; ALLOW_SNAPSHOT_ISOLATION é habilitado somente pelo provisionamento administrativo explícito e verificado pela readiness. Sem RCSI, grants adicionais ou nova migration; nenhum banco persistente foi alterado.

Foram acrescentados **102 testes**, total **715**: 116 Domain, 149 Application, 450 Integration. No sandbox, restore/build passaram com cinco NU1900; **383 testes passaram e 332 falharam na fixture Docker, zero ignorados**. Naquela execução, os novos testes SQL não puderam ser aprovados. A auditoria NuGet continua inconclusiva. O script então utilizava logs `6c-docker-tests.log`/`6c-docker-results` e guarda 715/715, preservando registros anteriores.

## Validação final da 6C e encerramento do P2 — 26/11/2026

A execução externa inicial **20261125-222720-071-d1a9d5** comprovou **715/715**. A auditoria posterior não aprovou o commit por isolamento Snapshot residual nas sessões do pool. SnapshotReadSession passou a manter a conexão aberta até descartar a transação e restaurar ReadCommitted na mesma sessão, com limpeza independente do cancelamento, timeout limitado, preservação da exceção original e ClearPool somente para o pool afetado quando necessário. Pooling/Snapshot permanecem habilitados, sem ClearAllPools ou mudanças de autenticação, permissões e regras financeiras.

Foram acrescentados **26 testes**, preservando os 715 anteriores. A execução intermediária **20261125-234414-181-bbd8c2** teve **739/741**: os dois cancelamentos receberam SqlException em vez de OCE. A correção alterou exclusivamente a asserção/instrumentação do teste e a documentação, exigindo a representação SQL específica e mantendo as verificações posteriores. No sandbox, restore/build passaram com cinco NU1900; **383/741 passaram, 358 falharam na fixture Docker e zero foram ignorados**. Essas tentativas locais não comprovam aprovação SQL.

A execução externa final do **usuário**, **20261126-002527-480-4b4fce**, comprovou **741/741: 116 Domain, 149 Application, 476 Integration**, três XMLs completos, zero falhas/ignorados/erros do runner; restore, build, módulos e Docker retornaram 0. O Codex comparou nomes e multiplicidades dos testes: nenhum dos 715 foi perdido e os 26 adicionais pertencem ao P2. Os dois cancelamentos passaram, observando dois SqlErrors por caso, todos **Number=0/State=0/Class=11**, mesma sessão restaurada para isolamento 2 e consulta financeira posterior válida. O controle sem reset reteve isolamento 5 após commit/rollback e retorno ao pool; os stores corrigidos restauraram a mesma sessão antes da reutilização. Falha de restauração comprovou substituição da conexão afetada e preservação de pool não relacionado.

**P2 encerrado; 6C APROVADA PARA COMMIT**, sem impedimento técnico bloqueante identificado. Nesta auditoria foram alterados somente README.md, este documento e entrega-6c-validacao.md; código e testes preservados por hashes. Não há nova migration, mudança de privilégios, operações em banco persistente, stage, commit ou push. A auditoria NuGet permanece inconclusiva.

Antes de utilizar a 6C em banco persistente, é necessário provisionamento administrativo explícito/autorizado de **ALLOW_SNAPSHOT_ISOLATION ON**, detectado pela readiness; não habilitar RCSI nem conceder administração à API. A 6C continua não publicada e sem CI própria aprovada. A Etapa 6 só será integralmente encerrada após publicação/CI autorizadas. Etapa 7 não iniciada. Detalhes, evidências e reprodução: [validação da 6C](entrega-6c-validacao.md); o script atual exige **741/741** e preserva o histórico de logs/XMLs.
