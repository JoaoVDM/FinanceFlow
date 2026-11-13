# ADR-003 — Modelo financeiro e ciclo de vida

Status: aprovado na revisão arquitetural.

## Contexto

A API registra finanças pessoais manualmente. Precisa permitir correções e consultas confiáveis, sem simular banco, contabilidade empresarial ou movimentação real de dinheiro.

## Decisão

Modelar FinancialAccount, Category, FinancialTransaction e Transfer. Todos os recursos financeiros têm proprietário. ApplicationUser herda de Identity na Infrastructure; Domain armazena somente UserId.

FinancialTransaction representa receita ou despesa com valor positivo; Kind determina o sinal financeiro. Categoria é obrigatória e deve ter o mesmo tipo. Transfer não tem categoria e não é receita/despesa.

Saldo negativo é permitido. Não verificar fundos disponíveis nem introduzir bloqueio de crédito. Contas e categorias pertencem a uma única pessoa; sem categorias globais ou contas compartilhadas.

## Ciclo de vida

- Conta: nome/tipo editáveis; moeda, saldo inicial e abertura imutáveis. Desativação não exige saldo zero e não elimina patrimônio.
- Categoria: nome e atividade editáveis; tipo imutável. Nome normalizado único por usuário e tipo, inclusive entre desativadas.
- Novos vínculos exigem contas/categorias ativas.
- Transação: campos financeiros editáveis com validação integral; pode trocar tipo somente com categoria compatível. Alteração de valor, conta ou data exige conta ativa. Reassociação exige novo recurso ativo.
- Categoria inativa já vinculada não invalida histórico. Alteração somente descritiva não exige reassociação.
- Conta inativa impede novas movimentações e alterações de valor/conta/data, mas não impede correção de descrição ou exclusão lógica.
- Transação excluída logicamente deixa de afetar cálculos.
- Transferência não tem edição financeira; corrigir cancelando e cadastrando novamente.
- Cancelamento/exclusão continua possível com conta ou categoria desativada.
- Usuário pode ser desativado, revogando acesso. Não apagar finanças em cascata.

## Integridade

Validar as regras no domínio e coordenar carregamento, propriedade e persistência na aplicação. O banco reforça invariantes estruturais com FKs compostas, CHECKs e índices únicos. Sem DELETE em cascata nas referências financeiras.

Registrar instantes técnicos de criação, atualização e exclusão/cancelamento. Isso não constitui histórico completo de alterações; não armazenar versões anteriores como se houvesse auditoria imutável.

## Alternativas

Entidade genérica de movimento: reduziria tabelas, mas introduziria campos condicionais para origem, destino e categoria. Partidas dobradas: robustas para contabilidade, excessivas para o escopo. Exclusão física: simples, mas elimina rastreabilidade mínima e dificulta idempotência.

## Consequências e revisão

Correções e exclusões alteram relatórios passados. O sistema apresenta a posição calculada com os dados atualmente válidos, não o que era conhecido em um instante anterior. Estorno vinculado e auditoria completa ficam adiados.

Relacionados: [modelo detalhado](../modelo-de-dominio.md), [saldos](004-calculo-de-saldos.md) e [transferências](005-transferencias.md).
