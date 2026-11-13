# ADR-004 — Saldos atuais e históricos

Status: aprovado na revisão arquitetural.

## Contexto e decisão

Persistir um saldo atual criaria duas representações da mesma informação e exigiria sincronização em cada edição, exclusão e transferência. O MVP calculará os saldos a partir de registros válidos, sem CurrentBalance persistido.

Para uma conta aberta e uma data de corte:

```text
saldo = saldo inicial
      + receitas válidas até o corte
      - despesas válidas até o corte
      + transferências recebidas válidas até o corte
      - transferências enviadas válidas até o corte
```

Saldo inicial é a posição imediatamente anterior aos movimentos da OpeningDate. A conta não compõe a posição antes dessa data. A consulta individual anterior à abertura pode representar essa posição como zero, informando a abertura; isso não autoriza lançamentos anteriores.

## Semântica de consultas

- Sem asOf: hoje no calendário financeiro configurado.
- Corte inclui a data informada e rejeita futuro.
- IsDeleted e IsCanceled excluem o efeito, inclusive em saldos históricos.
- Contas desativadas compõem patrimônio e histórico por padrão. Filtros explícitos podem selecionar subconjuntos, sem mudar o padrão financeiro.
- OpeningBalance e OpeningDate não podem ser editados depois da criação.
- Lançamentos retroativos válidos recalculam o passado.
- Não existe posição 'como era conhecida naquele dia'; exigiria auditoria temporal.

Em um extrato de from a to, saldo de abertura considera o saldo inicial se OpeningDate <= from e os movimentos válidos estritamente anteriores a from. Quando from antecede a abertura, esse saldo é zero e o saldo inicial passa a compor a posição na abertura, sem ser tratado como receita. Movimentos em from entram no período, não duas vezes. Saldo final equivale ao saldo em to. Totais e saldos não dependem da página retornada.

Resultado do período = receitas menos despesas. Não inclui saldo inicial nem transferências. Patrimônio consolidado inclui todas as contas selecionadas abertas no corte; transferências internas ao conjunto se anulam. Transferência para fora de um subconjunto de contas altera a posição desse subconjunto.

## Consultas e consistência

Somar e filtrar no SQL, com projeções e índices; não carregar histórico inteiro em memória. Transferências devem derivar do registro único. Não somar respostas HTTP independentes presumindo mesmo instante.

Preferir consulta consolidada. Uma resposta financeira que requeira múltiplos comandos com visão pontual usará transação de leitura Snapshot, com opção do banco habilitada e testada na etapa que a necessitar. Não presumir que um SELECT comum sob ReadCommitted oferece snapshot pontual de todo o banco. Não habilitar RCSI global por antecipação.

Sem snapshot entre páginas de requisições diferentes. Documentar possível mudança de paginação com alterações concorrentes.

## Alternativas e consequências

Saldo persistido e snapshots periódicos ficam adiados. O cálculo reduz risco de divergência, mas seu custo cresce com os dados. Otimizar somente após medição e reconciliação automática com os registros-fonte.

Aceitação: conta vazia, saldo inicial negativo, retroativos, limites do período, exclusões, cancelamentos e contas inativas. Ver [critérios](../criterios-de-aceitacao.md).
