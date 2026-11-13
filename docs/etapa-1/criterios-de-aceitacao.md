# Critérios de aceitação e estratégia de testes

Status: requisitos futuros, exceto verificações documentais da Etapa 1. Nenhum resultado de build, teste, benchmark ou segurança é alegado aqui.

## Estratégia

- Domain.Tests: invariantes, valores, datas e mudanças permitidas, sem banco/HTTP.
- Application.Tests: decisões dos casos de uso com doubles pequenos de relógio, proprietário e portas específicas. Não simular SQL para alegar integridade transacional.
- IntegrationTests: WebApplicationFactory e SQL Server real via Testcontainers.MsSql. Migrations, constraints, consultas, JWT e transações reais.
- Não tratar EF InMemory ou SQLite como evidência suficiente de comportamento do SQL Server.
- Fixtures com dados fictícios e banco isolado do desenvolvimento. Coordenar concorrência com barreiras/eventos, sem sleeps arbitrários como prova.
- Parte relevante da suíte usa token real, sem substituir todo o pipeline de autenticação por um usuário fictício automático.
- Sem Docker no job obrigatório: falha clara, não testes ignorados marcados como sucesso.

## Etapa 1 — Documentos

- [x] Os 14 documentos previstos existem em docs/etapa-1 e contêm texto.
- [x] Links Markdown locais resolvem para arquivos existentes.
- [x] Sete ADRs apresentam contexto, decisão, alternativas e consequências.
- [x] Nomes de projetos, políticas e contratos foram revisados quanto à consistência.
- [x] Mudanças aprovadas substituem explicitamente o planejamento inicial.
- [x] Pendências reais estão registradas; implementação futura não é apresentada como concluída.
- [x] Nenhum arquivo foi criado/alterado fora de docs/etapa-1.

Os itens são checklist de revisão. O relatório da execução deve informar o resultado real; não marcar checks de software nesta fase.

## Etapa 2 — Fundação

- Solução e quatro projetos compilam com referências previstas.
- Domain não depende de EF, Identity ou ASP.NET; Application não depende de Infrastructure/Api.
- API inicializa; /health/live responde 200; OpenAPI é gerado e Swagger UI funciona em Development.
- Erro inesperado resulta em Problem Details sem stack trace; não adicionar endpoint público de falha para testar isso.
- Options utilizadas inválidas falham na inicialização de forma segura.
- Testes via WebApplicationFactory passam sem Docker/SQL Server.
- Não há modelos financeiros, DbContext ou interfaces vazias antecipadas.

## Etapa 3 — Identidade e banco

- Migration inicial funciona em banco vazio; prontidão reflete acesso/schema sem vazar detalhes.
- Cadastro válido, e-mail normalizado duplicado, senha fora dos limites, login incorreto e bloqueio testados.
- Token adulterado, expirado, com algoritmo/emissor/audiência incorretos ou versão revogada é rejeitado.
- Senha alterada, logout global e desativação invalidam tokens anteriores.
- Falha na leitura do estado de autenticação não libera acesso; login concorrente com senha alterada não emite token novo indevido.
- Rate limiting retorna 429/Retry-After; erros preservam WWW-Authenticate quando aplicável.
- API não usa sa; segredos ausentes/inválidos impedem início da funcionalidade correspondente.
- Dados de teste fictícios; sem senha fixa versionada, token real em logs ou migrations automáticas em todo boot.

## Etapa 4 — Contas e categorias

- Saldo inicial zero/negativo/positivo e limite aceitos; excesso e fração de centavo rejeitados antes do SQL.
- Abertura futura rejeitada; abertura/saldo inicial/moeda não editáveis.
- Categoria duplicada normalizada retorna 409; tipo imutável; nome reservado também em categoria inativa.
- Desativar conta com saldo não zero não elimina seu patrimônio.
- Usuário A não lista, consulta, altera ou desativa recursos de B; totais não vazam informações.
- ETag/If-Match funcionam; sem precondição 428; formato inválido 400; versão antiga 412.
- Dois editores não se sobrescrevem silenciosamente.
- POST /accounts repetido e simultâneo com mesma chave cria uma conta e um registro idempotente; payload diferente retorna 409.

## Etapa 5 — Lançamentos e saldos

- Receita/despesa válida altera saldo com sinal correto; saldo negativo permitido.
- Categoria incompatível, conta alheia, categoria alheia, vínculo inativo, data anterior e futuro são rejeitados.
- Alteração de valor/data/conta respeita conta ativa; descrição e exclusão mantêm regras de histórico inativo.
- Saldo histórico respeita corte, abertura, retroativos e exclusões; nenhuma coerção de 10.001 para centavos é aceita.
- Operação e idempotência são atômicas; falha induzida não deixa criação parcial.
- Timeout/repetição com mesma chave não duplica; replay continua original após edição/exclusão.
- Criação versus desativação tem ordem válida, sem aceitar leitura antiga de atividade.
- Edição/exclusão concorrentes respeitam versão e não repetem efeito.

## Etapa 6 — Transferências e extratos

- Mesma conta, outra propriedade, conta inativa e abertura incompatível são rejeitadas.
- Origem diminui e destino aumenta pelo mesmo valor; patrimônio de ambas permanece igual.
- Cancelamento remove os dois efeitos; não é receita/despesa e não tem categoria.
- Repetição simultânea cria uma transferência; rollback e cancelamentos concorrentes não deixam metade do efeito.
- Extrato identifica contraparte e referência; abertura e final reconciliam com cortes do saldo.
- Movimentos em from entram somente no período; totais são independentes da página.
- Intervalo que atravessa a abertura da conta explica o saldo inicial sem classificá-lo como receita.
- Se resposta usar múltiplos comandos, testar visão consistente e configuração Snapshot correspondente.

## Etapa 7 — Consultas e relatórios

- Receitas menos despesas coincide com result; saldo inicial não vira receita.
- Transferências não aparecem como receitas/despesas; grupo por categoria reconcilia com resumo.
- Contas inativas são incluídas por padrão; filtro de conta não altera o significado do patrimônio global.
- from <= to, máximo de 366 datas incluídas, futuro e limites monetários testados.
- Paginação vazia, página final/além do fim, totalCount e ordenação com desempate são corretos.
- Campo de sort inválido e pageSize > 100 retornam 400.
- SQL executa agregação/paginação; sem lazy loading, N+1 conhecido ou histórico inteiro em memória.
- Isolamento entre usuários cobre listas, filtros, totais, saldo, extrato e relatórios.

## Etapa 8 — Operação e apresentação

- HTTPS, CORS explícito quando necessário, proxy confiável, banco privado e SQL restrito revisados.
- Segredos, JWT, senhas, connection strings e descrições financeiras não vazam em log/erro.
- Indisponibilidade e limites produzem respostas seguras e não efeitos parciais.
- Execução a partir de clone limpo é reproduzida e documentada com resultados reais.
- README, OpenAPI, exemplos fictícios, arquitetura, licença e limitações refletem o entregue.
- Suíte obrigatória e build de imagem passam antes da release; publicação não é implícita.

## Definição de conclusão por etapa implementada

Build e testes relevantes passam, documentação acompanha o comportamento, invariantes anteriores permanecem válidos e não há segredos versionados. Cobertura mede lacunas, sem porcentagem artificial como substituto de cenários críticos.

CI futura: restore, build, testes unitários/aplicação, integração com Docker, verificação OpenAPI, formatação/analisadores e dependências vulneráveis. Ampliar/repetir testes quando mudança ou falha justificar, sem inventar resultados.
