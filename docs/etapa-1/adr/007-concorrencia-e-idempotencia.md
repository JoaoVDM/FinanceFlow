# ADR-007 — Concorrência, atomicidade e idempotência

Status: aprovado na revisão arquitetural.

## Contexto

Edições simultâneas, repetição após timeout e desativação durante criação são problemas diferentes. Um token de versão sozinho não resolve todos eles. Como o saldo é calculado e negativos são permitidos, não precisamos bloquear fundos ou sincronizar CurrentBalance.

## Decisão e momento de entrada

| Garantia | Mecanismo | Introdução |
|---|---|---|
| Relações válidas e mesmo usuário | FKs compostas, CHECKs e autorização | Primeira entidade financeira persistida |
| Não sobrescrever alterações | rowversion e If-Match | Primeira alteração persistida |
| Gravação indivisível | Transação do SQL Server | Primeira operação com gravação |
| Não duplicar criação financeira | Registro idempotente com índice único | POST de conta com saldo inicial |
| Atividade estável durante criação | Leitura protegida e gravação na mesma transação | Primeiro fluxo que valida atividade de vínculo |

## Concorrência otimista

Mapear RowVersion como rowversion do SQL Server. Representar versão como valor opaco; ETag forte em consulta individual. PUT, PATCH e DELETE de recursos financeiros exigem If-Match. Ausência: 428; formato inválido: 400; versão desatualizada: 412.

Aplicar a versão do cliente como precondição real da alteração, não comparar somente a versão recém-carregada. Tratar DbUpdateConcurrencyException. Conflito não é repetido automaticamente com dados novos. rowversion não é data nem token de autenticação.

## Transações localizadas

SaveChanges já confirma alterações relacionadas atomicamente. Quando validar atividade de conta/categoria e gravar o vínculo precisar de garantia conjunta, usar transação explícita curta com RepeatableRead. Manter leituras protegidas até o commit; consultar contas em ordem consistente e usar índices. Desativação usa UPDATE normal com versão e disputa pelos mesmos bloqueios.

Se desativação vencer, a criação rejeita o vínculo inativo; se criação vencer, ela é confirmada antes da desativação. Não aceitar a leitura de atividade fora da transação como garantia suficiente.

Não aplicar Serializable globalmente, não segurar transação durante chamada externa e não compartilhar DbContext entre tarefas. Falhas/deadlocks causam rollback; nenhum sucesso é retornado antes do commit. Após falha com resultado desconhecido, repetir com a mesma chave. Retentativas técnicas, se habilitadas, devem envolver a transação inteira e ser limitadas; não introduzir framework próprio de retry.

Snapshot para respostas financeiras de múltiplos comandos segue ADR-004. RCSI e isolamento global não são pré-requisitos da fundação.

## Idempotência persistida

Idempotency-Key obrigatório em POST /accounts, /transactions e /transfers. Chave opaca de 1 a 128 caracteres ASCII visíveis, sem espaços; documentar uso de identificador aleatório pelo cliente. Não deduzir duplicação por valor/data/descrição, pois duas operações iguais podem ser legítimas.

Namespace único: UserId + Operation + Key. Operation é um nome estável do caso de uso. Hash SHA-256 sobre comando validado e normalizado, com representação canônica de valores, datas e campos, sem depender da ordem das propriedades JSON. Preservar distinções semânticas; não normalizar textos de maneira diferente da regra do cadastro.

- Antes de criar, procurar resultado existente no namespace do usuário.
- Mesmo hash: repetir status, corpo de criação e Location originais.
- Hash diferente: 409.
- Sem registro: executar criação e salvar registro idempotente na mesma transação.
- Corrida pela chave: índice único decide; desfazer a transação perdedora e consultar o resultado confirmado em novo contexto/transação. Nunca converter toda violação de unicidade em replay.
- Deadlock não deixa registro de processamento solto nem efeito financeiro parcial. Cliente pode repetir com a mesma chave.

Guardar resultado mínimo necessário de criação, sem tokens, senhas ou cabeçalhos sensíveis. Replay representa o resultado original, mesmo se a entidade tiver sido alterada, excluída ou desativada depois; não reexecutar a operação. Autenticação e validação estrutural da chave continuam obrigatórias.

Manter as chaves enquanto os dados financeiros relacionados forem mantidos. Sem TTL curto, cache, fila ou estado persistido 'em processamento'.

## Alternativas e consequências

Só rowversion não impede POST duplicado. Idempotência em memória falha após reinício e com mais de uma instância. Serializable em tudo adiciona bloqueios sem necessidade. Há custo de tabela e testes reais concorrentes; aceite-o nos fluxos financeiros.

Fontes: [concorrência EF Core](https://learn.microsoft.com/en-us/ef/core/saving/concurrency) e [transações EF Core](https://learn.microsoft.com/en-us/ef/core/saving/transactions).
