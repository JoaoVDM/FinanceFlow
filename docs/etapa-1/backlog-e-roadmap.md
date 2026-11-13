# Backlog técnico e roadmap revisado

Status: plano aprovado. Somente a Etapa 1 foi autorizada nesta execução. Critérios detalhados em [aceitação](criterios-de-aceitacao.md).

## Sequência

As oito etapas substituem o roadmap inicial de dez. Cada entrega depende das anteriores, mas deve produzir resultado verificável. Segurança, documentação e testes acompanham cada funcionalidade, não ficam concentrados no final.

| Etapa | Dependência | Resultado |
|---|---|---|
| 1 — Documentação | Arquitetura aprovada | Especificação navegável em docs/etapa-1 |
| 2 — Fundação | Etapa 1 e autorização explícita | Solução compilável e pipeline HTTP testável |
| 3 — Identidade e persistência básica | Etapa 2 | Cadastro/login e banco reproduzível |
| 4 — Contas e categorias | Etapa 3 | Cadastros isolados e versionados |
| 5 — Lançamentos e saldos | Etapa 4 | Receitas/despesas e posição histórica |
| 6 — Transferências e extratos | Etapa 5 | Movimento entre contas e explicação do saldo |
| 7 — Relatórios e consultas | Etapa 6 | Visão por período/categoria e consultas completas |
| 8 — Demonstração pública | Etapa 7 | Execução reproduzível, revisão operacional e apresentação |

## Etapa 1 — Consolidar documentação

- Objetivo: registrar as decisões já aprovadas sem reabrir o planejamento.
- Componentes: sete ADRs, modelo, contratos, roadmap, critérios, proposta da fundação e riscos.
- Aceitação: 14 arquivos, links locais válidos e conteúdo coerente; nenhum arquivo fora do escopo.
- Validação: revisão documental; não executar teste de software ou inferir resultado de implementação.
- Risco: divergência entre nomes, políticas financeiras e critérios de fases futuras.

## Etapa 2 — Fundação da solução

- Objetivo: estrutura mínima compilável com HTTP testável, sem finanças ou banco.
- Componentes: quatro projetos, três projetos de teste, referências corretas, controllers, erros, OpenAPI, health/live e configuração não sensível.
- Entregas pequenas: estrutura/referências; pipeline técnico; testes e instruções.
- Aceitação: build e testes HTTP; Domain sem dependências externas de infraestrutura; sem classes financeiras vazias.
- Testes: inicialização, health, OpenAPI, Problem Details e falha de configuração utilizada.
- Riscos: versões de pacotes, host de teste e disponibilidade do SDK.
- Detalhamento: [proposta da Etapa 2](etapa-2-fundacao.md). Esta documentação não a inicia.

## Etapa 3 — Identidade e persistência básica

- Objetivo: autenticar pessoas e estabelecer o ambiente de persistência.
- Componentes: FinanceFlowDbContext, configurações Identity, migration inicial, Dockerfile/Compose, health/ready, opções JWT, usuário atual e endpoints de autenticação/perfil.
- Entregas pequenas: banco/migration/health; cadastro/login; senha/logout/desativação; isolamento e limites.
- Aceitação: ambiente inicializado por procedimento explícito; usuário SQL da API restrito; tokens revogados imediatamente por versão; mensagens seguras.
- Testes: SQL Server em Testcontainers, banco vazio, unicidade de e-mail, senha, assinatura, claims, expiração, revogação, bloqueio e rate limiting. Testar disputa de login com alteração de senha.
- Riscos: certificados, recursos do Docker, configuração de chave e diferenças Windows/Linux.
- Docker: SQL Server 2022 Developer com CU/tag validada, volume nomeado e API multi-stage. API conecta pelo nome do serviço. Banco somente em loopback no desenvolvimento, quando publicado. Health checks, espera por disponibilidade e migrations explícitas, sem execução automática em todo boot. .env.example sem credenciais utilizáveis e .env ignorada. Execução desses componentes somente na etapa autorizada.

## Etapa 4 — Contas e categorias

- Objetivo: cadastros particulares com ciclo de vida seguro.
- Componentes: entidades/configurações/migrations, serviços específicos, controllers, rowversion, DTOs e IdempotencyRecord para criação de contas.
- Entregas pequenas: conta/saldo inicial/idempotência; categorias; alterações/atividade/concorrência.
- Aceitação: saldo inicial e abertura imutáveis; categoria com tipo imutável; desativação preserva histórico; isolamento de todas as rotas.
- Testes: limites monetários, abertura, duplicidade normalizada, FKs, ETag, versões concorrentes e chave repetida simultânea em conta.
- Riscos: collation, política de nome e sobrescrita silenciosa.

## Etapa 5 — Receitas, despesas e saldos

- Objetivo: primeiro fluxo financeiro completo.
- Componentes: FinancialTransaction, validação de vínculos, leitura protegida de atividade, consultas de saldo e operações idempotentes.
- Entregas pequenas: criar/consultar e calcular saldo; editar/excluir; saldo por corte e concorrência.
- Aceitação: cálculo reconciliado; exclusão e retroativos afetam corretamente o histórico; nenhum recurso alheio acessível.
- Testes: categoria incompatível, centavos, limites, futuro/abertura, saldo negativo, rollback, disputa com desativação e repetição após timeout.
- Riscos: validar antes da transação, coerção de decimal e diferenças entre filtros das consultas.

## Etapa 6 — Transferências e extratos

- Objetivo: transferência própria indivisível e extrato por conta.
- Componentes: Transfer, mapeamento, criação/cancelamento, projeção de movimentos e extrato paginado.
- Entregas pequenas: criação com idempotência; cancelamento; extrato e reconciliação.
- Aceitação: dois efeitos derivados de um registro; patrimônio preservado; transferências fora de receitas/despesas.
- Testes: origem=destino, proprietário diferente, inatividade, datas das duas contas, rollback, duplicação simultânea e cancelamento concorrente/repetido. Verificar abertura/final do extrato independentemente da página.
- Riscos: duplicação de projeção e inconsistência entre múltiplas consultas. Se o extrato exigir snapshot multiquery, introduzir sua configuração e testes aqui, sem esperar a etapa 7.

## Etapa 7 — Relatórios e consultas completas

- Objetivo: análise por período/categoria, filtros e ordenação finalizados.
- Componentes: consultas agregadas, DTOs de relatório, índices necessários e consistência de leitura onde aplicável.
- Entregas pequenas: resumo; agrupamento; fechamento dos filtros/ordenação e análise de consultas críticas.
- Aceitação: conjunto fictício conhecido reconcilia relatórios, extratos e saldos; inativas não desaparecem; conjunto de contas selecionado é explícito.
- Testes: intervalos e 366 datas, listas vazias, exclusões, cancelamentos, paginas e ordenação determinística, isolamento de contagens/totais, snapshot quando necessário.
- Riscos: N+1, agregação em memória, índices excessivos e mistura de resultado com saldo.

## Etapa 8 — Preparar demonstração pública

- Objetivo: revisão operacional e apresentação profissional, sem transformar o MVP em produto bancário.
- Componentes: configuração de hospedagem quando definida, segredos/TLS/proxy, logs, limites, README principal, diagramas, exemplos, licença MIT proposta e material da Vitrine.dev.
- Entregas pequenas: revisão de configuração; execução a partir de clone limpo; documentação/release. Publicação exige autorização correspondente, sem presumir autorização nesta etapa documental.
- Aceitação: suíte completa, sem dados reais, sem segredos versionados, execução reproduzível e limitações declaradas.
- Testes: isolamento em todos os recursos, autenticação completa, indisponibilidade do banco, rate limiting e build da imagem.
- Riscos: custo/hospedagem ainda indefinidos, exposição de banco e divergência entre docs e comportamento.

## Backlog posterior

Refresh e sessões por dispositivo, MFA, e-mail, recorrência, parcelamento, importação/exportação, metas, estorno vinculado, auditoria completa, exclusão definitiva e otimização de saldo. Antes de receber dados reais, priorizar recuperação/confirmação, retenção, backups e revisão de identidade.

Não antecipar: CQRS completo, MediatR, Repository genérico, fila, outbox, cache distribuído ou multimoeda. Cada nova dependência deve resolver uma necessidade identificada.
