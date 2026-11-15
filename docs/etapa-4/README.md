# Etapa 4 — Contas e categorias

Status: **Entrega A concluída e validada externamente pelo usuário no PowerShell do Windows**, publicada no commit `5a13762`. Foram confirmados 151 testes aprovados, build de sete projetos sem erros/warnings, migration aplicada, permissões verificadas, API Docker healthy e fluxos HTTPS Development/Production aprovados. O usuário confirmou GitHub Actions `43559125753` com **Success** nesse commit; essa evidência externa não cobre alterações posteriores. Esse é o fechamento histórico da Entrega A. B foi posteriormente publicada e C está implementada e validada externamente com 311 testes aprovados, ainda sem publicação ou nova CI aprovada, conforme os registros abaixo. Evidências externas, histórico do sandbox e comandos de reprodução em [validação](entrega-a-validacao.md).

## Decisões e escopo histórico da Entrega A

- Tipos fechados e códigos persistidos: checking, savings, cash, other. São classificações, sem juros, rendimentos, crédito ou faturas.
- BRL; OpeningBalance decimal exato entre -999999999999.99 e 999999999999.99, inclusive zero/negativos.
- OpeningDate DateOnly, não futura no calendário America/Sao_Paulo. Timestamps DateTimeOffset UTC.
- Id/owner gerados/obtidos no servidor. Nome obrigatório até 100, trim/NFC, sem unicidade exigida.
- Proprietário, moeda, abertura e saldo inicial não têm edição. Não há saldo atual persistido.
- Contas inicialmente ativas. Listas sem filtro incluem inativas; não há endpoint de alteração/inativação nesta entrega.
- A política de categorias aprovada para B é Trim + NFC + ToUpperInvariant + NFC, acentos significativos e comparação binária na coluna normalizada. Na Entrega A não foram criados Category, tabelas ou rotas de categorias. A Entrega B mantém essa política sem modificar Identity ou a collation global.

## Arquitetura implementada

Domain/Accounts contém FinancialAccount e invariantes sem dependências de EF/Identity/ASP.NET. Application/Accounts contém FinancialAccountsService, calendário, contratos de leitura e IFinancialAccountStore. ICurrentUser fica em Application/Common, compartilhando apenas o contrato do usuário autenticado. Infrastructure implementa consultas EF, configurações Fluent API, IdempotencyRecord e criação atômica. Api recebe DTOs HTTP, extrai a identidade por HttpCurrentUser e compõe respostas, sem DbContext nos controllers de contas.

TimeProvider existente é reutilizado; a criação captura um instante único e deriva dele o dia financeiro e o timestamp. FinancialCalendar tenta o identificador IANA e sua conversão Windows, falhando se não houver fuso válido. Não usa offset fixo ou relógio estático do domínio.

Sem Repository genérico, MediatR, AutoMapper, biblioteca nova ou executor transacional genérico. As declarações de identidade e seus casos de uso foram preservados.

## Contratos entregues

| Método | Endpoint | Resultado |
|---|---|---|
| POST | /api/v1/accounts | 201; JWT e um Idempotency-Key válido obrigatórios; snapshot JSON, Location e ETag |
| GET | /api/v1/accounts | 200 com items/page/pageSize/totalCount/totalPages; somente proprietário |
| GET | /api/v1/accounts/{id} | 200 com ETag forte; 404 genérico para alheia/inexistente |

POST aceita somente name, type, currency, openingBalance e openingDate. Não aceita UserId, Id, IsActive, timestamps ou saldo calculado. Campos desconhecidos são rejeitados pelo pipeline existente. Campo monetário/data ausente não vira zero/data padrão. Falha de validação retorna 400; chave conflitante 409; autenticação 401; indisponibilidade conhecida 503. Limite global autenticado mantém 429/Retry-After; corpo limitado a 16 KiB. Responses financeiras usam Cache-Control: no-store.

Listas: page=1, pageSize=20 (1–100), filtros isActive/type, sort=name ou id, direction=asc ou desc. Ordem por nome desempata por Id. Count e paginação são executados no SQL com UserId; não há paginação em memória nem promessa de snapshot entre comandos/páginas sob alterações concorrentes. Offset que não cabe no limite do provider retorna 400.

Read models da Application expõem id, name, type, currency, openingBalance, openingDate, isActive, timestamps e version opaco. Não expõem UserId ou entidades EF. ETag é a versão SQL de oito bytes em Base64, entre aspas. Nesta entrega a versão é disponibilizada, sem implementar If-Match, PUT ou PATCH.

OpenAPI inclui Bearer nas três operações, cabeçalho Idempotency-Key obrigatório, limites/enum de criação, ETag e Location. Swagger/OpenAPI permanecem somente Development.

## Precisão monetária

ExactMoneyJsonConverter inspeciona o token numérico original antes de decimal parsing. Reduz coeficiente/expoente a centavos inteiros exatos, com tamanho limitado, sem double/float. Aceita zeros finais e notação exponencial se o valor respeitar centavos e limites. Rejeita strings monetárias, frações residuais, excesso de magnitude e expoentes não representáveis para valores não zero. Nunca constrói potências gigantes para processar expoentes hostis.

Exemplos: 10.000 e 1e1 são exatos; 10.001 e 10.0000000000000000000000000000000000000001 são inválidos. O domínio repete a validação de limite/centavos para chamadas que não vieram de HTTP. Depois de validar, fixa escala de duas casas sem mudar o valor. SQL usa decimal(18,2) e CHECK de intervalo; esses mecanismos não substituem a validação do token original.

## Persistência e migration

FinancialAccounts: PK Guid, chave alternativa (Id, UserId), FK UserId para AspNetUsers com NoAction, índice (UserId, IsActive), rowversion real e CHECKs de nome/tipo/BRL/intervalo. IdempotencyRecords: PK Guid, FK UserId NoAction e índice único (UserId, Operation, Key). Operation/Key usam varchar com Latin1_General_100_BIN2; chave é sensível a caixa, sem espaços. Hash binary(32), JSON validado e status 201.

Migration incremental: **20261114000100_AccountsDeliveryA**, aplicada com sucesso externamente pelo usuário. Adiciona somente duas tabelas e seus índices/constraints. A migration inicial e InitialIdentityModel.Build não foram modificados. AccountsDeliveryAModel é o novo target congelado e o snapshot cumulativo atual; não reutiliza configurações mutáveis de runtime. Foi escrito seguindo o padrão histórico do repositório, sem instalar ferramentas EF. O teste offline HasPendingModelChanges passou no Codex; os testes SQL e a aplicação do DDL foram posteriormente aprovados na execução externa. O Codex não aplicou migrations ao banco persistente.

Startup não executa migrations. Readiness mantém verificação de migration e passa a exigir SELECT nos novos objetos; liveness continua independente do SQL.

## Idempotência e concorrência de criação

Operation fixa accounts.create.v1; chave de 1–128 caracteres ASCII visíveis (! até ~), sem trim ou mudança de caixa. Namespace sempre inclui o usuário autenticado. SHA-256 usa JSON canônico interno v1, campos em ordem fixa, UTF-8, nome normalizado, valores invariantes F2 e data yyyy-MM-dd; ignora ordem do JSON HTTP, Id gerado e timestamps.

1. Consultar registro confirmado no namespace do usuário.
2. Hash igual: devolver status, JSON, Location, ETag e Content-Type armazenados; hash diferente: 409.
3. Sem registro, abrir transação ReadCommitted curta, inserir conta e obter rowversion pelo primeiro SaveChanges.
4. Serializar o snapshot v1, inserir o registro e executar o segundo SaveChanges.
5. Commit antes de devolver sucesso. Tanto a primeira resposta quanto o replay enviam o mesmo JSON persistido.
6. Colisão do índice idempotente conhecido: rollback, limpar tracking e reler vencedor em contexto novo. Mesmo hash reproduz 201; diferente gera 409. Outras violações não são convertidas em replay.

O reconhecimento da colisão é deliberadamente restrito ao erro 2601 do índice nomeado no objeto IdempotencyRecords e sua entrada EF. Os testes de colisão foram aprovados na suíte externa informada pelo usuário. Mensagens localizadas não reconhecidas continuam uma consideração de portabilidade: falham de modo fechado, sem devolver sucesso/replay indevido. Não se registra a mensagem nem seu valor de chave.

Não há TTL, cache, estado persistido em processamento ou retry automático. Cancelamento/erro antes do commit desfaz a transação; resultado de commit desconhecido exige repetição com a mesma chave. Replay nunca consulta a representação atual da conta, mesmo se testes/futuras entregas alterarem nome/atividade.

## Privilégios e segurança

Api: AspNetUsers mantém SELECT/INSERT/UPDATE; history mantém SELECT; FinancialAccounts e IdempotencyRecords recebem somente SELECT/INSERT. Não recebe UPDATE/DELETE financeiro, DDL, roles ou grants com delegação. Provisionamento e lista permitida do verificador foram ampliados em conjunto. Migração e provisionamento mantêm credenciais separadas e externas. Não há redefinição de logins/senhas ou remoção de dados.

Toda busca/lista/count/replay usa UserId confiável. O middleware JWT continua conferindo IsActive/TokenVersion a cada requisição. Erros usam Problem Details sanitizado; snapshots e chaves não são logados. Nenhuma alteração no fluxo de logout/senha/revogação foi necessária.

## Refatoração e Entrega B publicadas

O usuário confirmou a publicação da refatoração em **9c938c8**, com GitHub Actions **43564544104 — Success**. As validações e limitações históricas estão em [refatoração de legibilidade](refatoracao-legibilidade.md); essa CI não comprova alterações posteriores.

A Entrega B implementa Category e somente POST/GET /api/v1/categories e GET /api/v1/categories/{id}. Preserva kind imutável (income/expense), nome Trim/NFC e unicidade por usuário/tipo com Trim/NFC/ToUpperInvariant/NFC e comparação BIN2. Não exige Idempotency-Key. Inclui migration incremental, SELECT/INSERT restritos, readiness e testes SQL obrigatórios. Em **14/11/2026**, o usuário confirmou externamente **216 testes aprovados, 0 falhas e 0 ignorados**, incluindo SQL Server/Testcontainers, e auditoria com restauração bem-sucedida e sete projetos sem vulnerabilidades conhecidas nas fontes consultadas. Os avisos NU1900 dos testes e as falhas históricas de acesso ao Docker no sandbox permanecem registrados; não se afirma novo build totalmente sem warnings. Posteriormente, o usuário confirmou a publicação de B em `64b32d3` e a CI `43642389840 — Success`. Esses resultados não comprovam C nem aplicação de seus grants no banco persistente. Detalhes em [entrega-b-validacao](entrega-b-validacao.md).

## Entrega C local — Edição e concorrência

Implementados somente PUT/PATCH de /api/v1/accounts/{id} e /api/v1/categories/{id}. Os quatro exigem JWT/If-Match e retornam 204 sem corpo, novo ETag e no-store. Conta permite nome/tipo; categoria permite nome normalizado; PATCH aceita somente isActive. Os campos imutáveis e o snapshot idempotente de Accounts são preservados.

A versão recebida é usada como OriginalValue de RowVersion no EF Core e participa do WHERE do UPDATE. UpdatedAtUtc é forçado como coluna modificada, inclusive em comandos sem mudança de valores, para garantir execução da precondição. Não há retry em conflito. O provisionamento concede UPDATE apenas nas quatro colunas editáveis de cada tabela; o verificador continua rejeitando permissões adicionais. Não houve nova migration ou alteração nos targets históricos.

Após a correção das sete falhas iniciais, o usuário executou a suíte completa no PowerShell com Docker/SQL Server/Testcontainers: **311 testes aprovados, 0 falhas e 0 ignorados**. A revisão estática final dos 34 arquivos foi aprovada, sem problemas bloqueantes. O histórico de execução sem acesso ao Docker no sandbox permanece registrado, separado da validação externa. A consulta NuGet apresentou os sete projetos sem pacotes vulneráveis identificados, mas emitiu NU1900 por indisponibilidade do índice de vulnerabilidades; não constitui auditoria de segurança conclusiva. C não foi publicada e não possui nova CI aprovada. Veja [evidências, testes e procedimento de reprodução](entrega-c-validacao.md).

## Próximas entregas

A regressão externa da Entrega C e a revisão estática final estão aprovadas. Repetir a auditoria NuGet quando o índice de vulnerabilidades estiver disponível. Publicação e execução de CI da Entrega C dependem de autorização e validação posteriores. Não iniciar a Etapa 5. FinancialTransaction, saldos, transferências, extratos e relatórios permanecem fora do escopo.
