# ADR-005 — Transferências entre contas próprias

Status: aprovado na revisão arquitetural.

## Contexto e decisão

Representar cada transferência em uma linha de Transfer, com UserId, SourceAccountId, DestinationAccountId, Amount, OccurredOn, Description e estado de cancelamento.

Não criar duas FinancialTransaction persistidas. O extrato projeta saída na origem e entrada no destino a partir da mesma transferência.

## Invariantes

1. Origem diferente do destino.
2. Ambas as contas pertencem ao usuário autenticado.
3. Ambas estão ativas na criação e usam BRL.
4. Valor estritamente positivo, dentro do limite e sem fração efetiva de centavo.
5. Data não futura e não anterior à abertura de nenhuma conta.
6. Sem categoria de receita ou despesa.
7. Os dois efeitos têm mesmo valor e sinais opostos.
8. Transferência entre contas do conjunto consolidado não altera seu patrimônio.
9. Cancelamento remove ambos os efeitos, uma única vez.

## Persistência e correção

Inserção e registro idempotente são confirmados juntos. A validação das contas participa da transação curta descrita no ADR-007. Não há débito/crédito de uma coluna de saldo, nem chamada externa.

Não oferecer PUT de transferência. Corrigir por cancelamento lógico e nova criação, com nova chave idempotente. Cancelamento exige a versão esperada e permanece permitido em contas inativas. Não há reativação nem exclusão física.

No extrato, identificar natureza transferIn/transferOut, referência TransferId e conta contraparte. Essas projeções não são entidades adicionais nem receitas/despesas. Consulta paginada de transfers retorna cada operação uma vez.

## Alternativas

- Duas transações vinculadas: permitiria reutilização parcial das consultas, mas exigiria proteger existência, simetria, alteração e cancelamento de dois registros.
- Partidas contábeis: apropriadas a outro escopo, sem necessidade atual.

## Consequências e testes

A forma de armazenamento impede uma perna persistida isoladamente. Ainda é obrigatório provar correção das projeções e atomicidade da criação com idempotência.

Testar falha de gravação, chave repetida simultânea, contas de usuários distintos, origem=destino, conta inativa, data inválida, cancelamento repetido e preservação do patrimônio. Sem teste de saldo insuficiente, pois saldos negativos são permitidos.

Relacionados: [saldos](004-calculo-de-saldos.md) e [concorrência](007-concorrencia-e-idempotencia.md).
