# ADR-006 — Datas, moeda e valores monetários

Status: aprovado na revisão arquitetural.

## Contexto

Registros financeiros precisam de centavos exatos e de datas que representem o calendário do usuário. Precisão implícita do banco ou conversão indevida de data para UTC pode alterar valores e períodos sem intenção. A decisão separa dinheiro, data financeira e instante técnico.

## Decisão sobre dinheiro

Usar decimal no C# e decimal(18,2) no SQL Server. Somente BRL no MVP, com código ISO explícito nos contratos e restrição de banco. Não usar float, double ou SQL money.

- Amount de receita, despesa e transferência: maior que zero e até 999999999999.99.
- OpeningBalance: de -999999999999.99 a 999999999999.99, inclusive zero.
- Rejeitar fração efetiva de centavo antes da persistência: 10.001 inválido; 10.000 equivalente a 10.00.
- Não confiar no arredondamento/coerção do provider ou da coluna para validar entrada.
- Limites centralizados; agregação sem truncamento e com tratamento de overflow, sem retornar saldo parcial.
- Números JSON usam ponto decimal, sem símbolo ou separador de milhar. Clientes devem adotar representação decimal adequada.

Não há operação percentual no MVP. Se surgir, a recomendação já registrada é arredondar explicitamente para duas casas com MidpointRounding.AwayFromZero, com testes e confirmação do contrato da nova funcionalidade.

## Decisão sobre datas

| Conceito | Representação |
|---|---|
| Data financeira | DateOnly / SQL date / YYYY-MM-DD |
| Criação, alteração, exclusão e cancelamento | DateTimeOffset em UTC / datetimeoffset |
| Hoje financeiro | Data atual no calendário America/Sao_Paulo |

Injetar TimeProvider; fornecer a data atual às regras sem relógio estático escondido no domínio. Validar resolução do fuso no Windows e Linux. Data financeira enviada pelo cliente não é um timestamp a converter para UTC.

OpeningDate não pode ser futura. Lançamento pode ser retroativo até a abertura, inclusive. Transferência respeita as duas aberturas. Futuro é rejeitado; não há registro aguardando efetivação automática.

Intervalos financeiros são inclusivos. Extratos e relatórios exigem from <= to, sem futuro e com no máximo 366 datas incluídas. Listagens paginadas podem usar filtros de data opcionais; não recebem o limite de relatório implicitamente.

## Alternativas e consequências

Centavos em inteiro seriam viáveis, mas decimal é adequado à stack aprovada. Timestamp para toda data criaria ambiguidades desnecessárias. Multimoeda exigiria câmbio, precisão e política de consolidação: não basta liberar outro código em Currency.

Testar centavos, limites, valores negativos, abertura, futuro, dias de fronteira, meia-noite financeira e transição entre ambientes. Datas de exemplos são fictícias e não devem ser fixadas como relógio de testes.
