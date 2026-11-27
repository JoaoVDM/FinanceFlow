# Etapa 7 — Relatórios e consultas

## Baseline publicada e evidências anteriores

A Etapa 6 foi concluída e a 6C publicada em **d303fc220c3ea02df86c71355c80112101bb4407** (`feat: implement transfer listing and reconciled account statements`), main, com **GitHub Actions #12 / 45751367056 — Success**, conforme confirmação do usuário.

A execução externa `.local/6c-docker-results/20261126-002527-480-4b4fce` comprovou **741/741 aprovados: 116 Domain, 149 Application e 476 Integration**, zero falhas, ignorados e erros do runner. O P2 de isolamento residual foi encerrado com restauração explícita de ReadCommitted e testes SQL reais. Essas evidências pertencem à baseline 6C, não aprovam a 7A.

Após publicar, o usuário confirmou SQL Server persistente iniciado por Compose, imagem da API atualizada, migrations/provisionamento explícitos, ambos Healthy e Swagger HTTPS acessível. Cadastro/login/JWT e os fluxos financeiros até extrato foram validados manualmente com dados fictícios. A 7A **não acessou, reiniciou ou modificou esse ambiente**.

## Entregas

| Entrega | Escopo | Estado |
|---|---|---|
| 7A | GET /api/v1/reports/summary | Publicada em c6369fc; 798/798 externos; CI #13 / 45919727200 Success |
| 7B | GET /api/v1/reports/by-category | 858/858 externos e auditoria aprovada para commit; local, sem publicação/CI própria |
| 7C | Fechamento de consultas/filtros e análise de índices | Pendente; não iniciada |

O resumo representa **fluxo financeiro**: receitas e despesas válidas no período inclusivo, resultado = income - expense. Não inclui saldo inicial, patrimônio ou transferências. Filtro opcional accountId seleciona uma conta própria, inclusive inativa; ausente considera todas as contas do proprietário autenticado. Categorias inativas preservam o histórico.

As consultas usam SELECTs e os índices existentes. A agregação condicional é executada no SQL Server, e a propriedade da conta opcional participa da mesma visão Snapshot. Reutiliza SnapshotReadSession, incluindo limpeza e proteção do pool, sem migration, grants adicionais ou alteração da infraestrutura compartilhada.

## Validação e pendências

Foram acrescentados **57 testes**: 17 Application e 40 Integration. Total **798** (116 Domain, 166 Application, 516 Integration). Restore/build Release passaram no sandbox com cinco NU1900; zero erros/warnings C#/analisadores. A suíte disponível teve **404 aprovados, 394 falhas de fixture Docker, zero ignorados**. Os testes SQL não foram aprovados pelo sandbox; não há skips ou substituição por SQLite/InMemory. A auditoria NuGet permanece inconclusiva.

A primeira execução externa do usuário, **20261126-023548-931-084230**, teve **795/798**: overflow induzido falhou com 8155 porque a regex removia o nome Amount de uma projeção interna; os dois casos de pooling falharam ao ler @@SPID smallint com GetInt32. A correção limitou a regex aos operandos THEN das somas e converteu @@SPID explicitamente para int, como na sondagem P2 da 6C. Somente os dois testes e entrega-7a-validacao.md foram alterados nessa correção; produção e os demais 15 arquivos locais foram preservados por hashes. Nenhuma asserção foi removida ou flexibilizada para aceitar erros equivalentes.

A segunda execução externa, **20261126-214221-889-68195a**, comprovou **798/798**: Domain **116/116**, Application **166/166**, Integration **516/516**, zero falhas/ignorados/erros do runner. O Codex leu os três XMLs completos e os códigos zero de restore/build/módulos/Docker; comparou nomes e multiplicidades dos **741 casos anteriores**, sem perdas, com exatamente **57 novos**. As três falhas anteriores foram encerradas. O overflow exige SQL **8115**, HTTP **503**, ausência de totais parciais e nova consulta válida; os dois casos de pooling conservaram SPID/ClientConnectionId e retornaram isolamento **2**, @@TRANCOUNT=0, alcançando também conexão fechada antes do descarte do DbContext, identidade JWT e revogação. São resultados do usuário em Docker Linux/SQL Server/Testcontainers, não testes SQL executados pelo sandbox.

**7A APROVADA PARA COMMIT**, sem impedimento técnico bloqueante identificado na revisão dos 18 arquivos. Contrato de seis campos, regras financeiras, isolamento, Snapshot e proteção do pool foram preservados. Nenhuma migration, dependência ou ampliação de privilégios. Nesta auditoria final foram atualizados apenas README.md, este documento e entrega-7a-validacao.md; código/testes/script preservados por hashes, staging vazio e git diff --check aprovado. A execução externa não apresentou NU1900, mas não houve evidência de uma nova auditoria de vulnerabilidades conclusiva.

Executar externamente, com Docker Linux/SQL Server/Testcontainers:

```powershell
& C:\FinanceFlow\scripts\Test-DockerSuite.ps1
```

O script exige **798/798**, três XMLs e zero falhas/ignorados/erros do runner, com código Docker zero. As execuções 7A usam `.local/7a-docker-tests.log` e `.local/7a-docker-results/<execucao>`; os históricos 6A/6B/6C permanecem preservados. Nenhum banco persistente é utilizado pela suíte.

Naquele encerramento da 7A não houve stage, commit ou push pelo Codex; publicação e CI ainda eram pendentes. **Posteriormente, o usuário confirmou a publicação da 7A em c6369fcf6afcd88b5edb50272a30cc92e7c9c93d, com GitHub Actions #13 / 45919727200 — Success e baseline de 798/798.** O histórico acima e o [registro da 7A](entrega-7a-validacao.md) permanecem preservados; as referências a caminhos/contagem 7a descrevem aquela execução.

## Entrega 7B — agrupamento por categoria

`GET /api/v1/reports/by-category` exige `from`/`to` exatos e inclusivos (até 366 datas, sem futuro financeiro em America/Sao_Paulo). Filtros opcionais: conta própria e `kind=income|expense`. Retorna array de `{categoryId,name,kind,total}`; vazio válido é `[]`. Ordem SQL fixa: kind asc, total desc, categoryId asc. Não há envelope, paginação ou percentuais.

O agrupamento por ID preserva categorias de mesmo nome com tipos distintos, apresenta o nome atual e inclui histórico inativo. Exclui transações apagadas logicamente, saldo inicial e todas as transferências. A soma de cada natureza reconcilia com o summary em dados estáveis; chamadas separadas não representam o mesmo Snapshot durante escritas concorrentes. Summary mantém o contrato publicado e rejeita `kind`.

ReportsController/ReportsService/IReportStore/ReportStore foram estendidos, reutilizando FinancialCalendar e SnapshotReadSession/SnapshotReadFailure. Propriedade da conta, nomes e agregados usam a mesma transação Snapshot; a limpeza do pool permanece intacta. Nenhuma migration, índice, grant, dependência ou infraestrutura compartilhada foi alterada.

Cobertura nova: **23 Application e 37 Integration**, total **858** (116 Domain, 189 Application, 553 Integration). Restore/build passaram no sandbox com cinco NU1900. A suíte completa descobriu 858: **430 aprovados, 428 falhas de fixture Docker, zero ignorados**. Naquela execução local os testes SQL não alcançaram suas asserções e não foram aprovados pelo sandbox. A geração SQL offline confirmou JOIN/GROUP BY/SUM/ORDER BY, sem abrir conexão; não substitui SQL Server real.

A **primeira execução externa do usuário**, **20261126-222947-267-8d877f**, aprovou **858/858: 116 Domain, 189 Application e 553 Integration**, três XMLs completos, zero falhas/ignorados/erros do runner e códigos zero de restore/build/módulos/Docker. A comparação ordinal de nomes e multiplicidades com os XMLs finais da 7A confirmou todos os **798 casos anteriores** e exatamente **60 novos**. Nenhum teste foi removido ou enfraquecido nas duas adaptações de double/guarda de rotas.

Os casos concorrentes confirmaram criação de receita, exclusão de despesa e renomeação de categoria entre as leituras coordenadas: a resposta original mantém nomes/totais anteriores e a seguinte reflete o novo estado. Overflow/timeout exigem **8115/-2**, HTTP **503**, ausência de grupos parciais e nova consulta com total **2**; a falha inesperada permanece **500**. O output do teste de pooling registra **SPID 201**, **ClientConnectionId dbe28884-85ef-48da-a226-67bea56b6d3b**, isolamento **3/5/2/2** e @@TRANCOUNT **0/1/0/0** antes/durante/depois/final. As asserções seguintes alcançam conexão fechada/transação nula antes do descarte do DbContext, identidade real, revogação e consulta posterior válida com total **1,01**. Não são valores inventados ou derivados de uma conexão diferente.

**7B APROVADA PARA COMMIT**, sem achado técnico bloqueante na revisão dos 19 arquivos contra c6369fc. Regras financeiras, filtro de proprietário, contrato do summary, Snapshot, pooling e privilégios foram preservados; não há migration, índice, dependência ou workflow novo. Nesta auditoria foram alterados apenas README.md, este documento e entrega-7b-validacao.md; os demais **16 arquivos** foram preservados por hashes SHA-256, staging vazio e git diff --check aprovado. O console externo teve build com zero warnings e nenhuma ocorrência de NU1900; isso não comprova uma nova auditoria de vulnerabilidades conclusiva.

O script atual usa `.local/7b-docker-tests.log` e `.local/7b-docker-results/<execucao>`, exigindo **858/858**, três XMLs completos, zero falhas/ignorados/erros do runner e Docker exit code zero. Preserva integralmente os históricos anteriores. Executar o mesmo comando PowerShell acima. Consulte [implementação, testes e validação da 7B](entrega-7b-validacao.md).

A 7B continua local, **aprovada para commit**, sem publicação/CI própria. Permanecem publicação autorizada, CI própria e consulta NuGet conclusiva. O ambiente persistente desligado foi preservado, sem inicialização ou acesso. **7C e Etapa 8 não foram iniciadas; a Etapa 7 não está concluída.** Nenhum stage, commit ou push foi executado na implementação ou nesta auditoria.
