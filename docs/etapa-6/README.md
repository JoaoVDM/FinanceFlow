# Etapa 6 — Transferências e extratos

## Baseline e estado

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
