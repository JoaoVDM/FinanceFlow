# Contratos REST planejados

Status: contratos para implementação futura; nenhuma rota está implementada nesta etapa.

## Convenções

Prefixo `/api/v1`; recursos no plural e em inglês; JSON camelCase. Datas financeiras YYYY-MM-DD; instantes ISO 8601 UTC; dinheiro como número JSON decimal sem formatação local. Não serializar entidades EF/Identity diretamente.

Somente register e login são anônimos entre os recursos de negócio. Todas as demais rotas exigem Bearer válido. `/health/live` será técnico e anônimo; readiness será introduzido quando houver dependências e não exporá detalhes internos.

Criações retornam 201 com Location e DTO do recurso. Consultas retornam 200. Alterações sem corpo retornam 204. Listas usam `{ items, page, pageSize, totalCount, totalPages }`. Não criar envelope universal em torno de todos os recursos.

DTOs da API não aceitam UserId, CurrentBalance, TokenVersion, IsDeleted, IsCanceled ou timestamps gerenciados pelo servidor. Metadados de recurso podem expor id, datas técnicas e version. Normalização e limites devem ser explícitos, sem truncamento silencioso.

## Autenticação e usuário

| Método | Rota | Entrada/finalidade | Sucesso | Erros principais |
|---|---|---|---|---|
| POST | /auth/register | RegisterRequest: displayName, email, password | 201, perfil sem senha/hash | 400, 409, 429 |
| POST | /auth/login | LoginRequest: email, password | 200: accessToken, tokenType, expiresIn | 400, 401, 429 |
| POST | /auth/logout | Invalida todos os tokens do usuário | 204 | 401 |
| GET | /users/me | Perfil próprio: id, displayName, email | 200 | 401 |
| PATCH | /users/me | UpdateProfileRequest: displayName | 204 | 400, 401 |
| POST | /users/me/change-password | currentPassword, newPassword | 204; tokens anteriores revogados | 400, 401, 429 |
| DELETE | /users/me | Confirmação currentPassword para desativar | 204; acesso revogado | 400, 401 |

Senha: 12–128 caracteres, sem truncamento. Nome obrigatório até 100. E-mail válido e único normalizado. Mensagem de login genérica para credencial inválida, bloqueio ou inatividade. Cadastro duplicado retorna 409 genérico, com risco de inferência registrado no ADR-002.

DELETE /users/me mantém a confirmação de senha em corpo conforme o planejamento aprovado. Compatibilidade de corpo em DELETE com clientes/proxies deve ser validada antes desse endpoint; não mudar silenciosamente para outra rota. Não há exclusão física nem gestão de outros usuários.

Sem endpoint refresh ou promessa de logout por dispositivo. If-Match desta especificação aplica-se aos recursos financeiros abaixo, não ao perfil Identity.

## Contas

| Método | Rota | Entrada/finalidade | Sucesso | Erros principais |
|---|---|---|---|---|
| POST | /accounts | CreateAccountRequest: name, type, currency, openingBalance, openingDate | 201 | 400, 409 |
| GET | /accounts | Lista, filtros isActive/type | 200 | 400 |
| GET | /accounts/{id} | Recurso individual e ETag | 200 | 404 |
| PUT | /accounts/{id} | UpdateAccountRequest: name, type | 204 | 400, 404, 412, 428 |
| PATCH | /accounts/{id} | SetAccountActivityRequest: isActive | 204 | 400, 404, 412, 428 |
| GET | /accounts/{id}/balance | asOf opcional, padrão hoje | 200: accountId, currency, asOf, balance | 400, 404 |
| GET | /accounts/{id}/statement | from, to e paginação | 200: extrato | 400, 404 |

Nome até 100; moeda BRL; abertura não futura; saldo inicial no intervalo do ADR-006. OpeningBalance, OpeningDate e Currency não são campos editáveis. Desativação não exige saldo zero. Listagem sem filtro de atividade inclui ativas e inativas; o mesmo vale para posições financeiras padrão.

## Categorias

| Método | Rota | Entrada/finalidade | Sucesso | Erros principais |
|---|---|---|---|---|
| POST | /categories | CreateCategoryRequest: name, kind | 201 | 400, 409 |
| GET | /categories | Lista com kind/isActive opcionais | 200 | 400 |
| GET | /categories/{id} | Recurso e ETag | 200 | 404 |
| PUT | /categories/{id} | RenameCategoryRequest: name | 204 | 400, 404, 409, 412, 428 |
| PATCH | /categories/{id} | SetCategoryActivityRequest: isActive | 204 | 400, 404, 412, 428 |

kind: income ou expense, imutável. Nome até 100, obrigatório e único normalizado por usuário e tipo, inclusive entre inativas. PATCHs usam DTO explícito, sem JSON Patch genérico.

## Transações

| Método | Rota | Entrada/finalidade | Sucesso | Erros principais |
|---|---|---|---|---|
| POST | /transactions | CreateTransactionRequest | 201 | 400, 404, 409 |
| GET | /transactions | Consulta paginada/filtrada | 200 | 400, 404 para referência alheia/inexistente |
| GET | /transactions/{id} | Recurso e ETag | 200 | 404 |
| PUT | /transactions/{id} | UpdateTransactionRequest | 204 | 400, 404, 409, 412, 428 |
| DELETE | /transactions/{id} | Exclusão lógica | 204 | 404, 412, 428 |

Create/UpdateTransactionRequest: accountId, categoryId, kind, amount, occurredOn, description opcional até 500. PUT substitui campos editáveis, sem mudar proprietário. Recursos referenciados devem ser próprios; categoria compatível; valor/data válidos. Vínculo inativo retorna 409; valor/data/tipo incompatível retorna 400. Ver regras de alteração em conta inativa no ADR-003.

Excluídas não aparecem em consultas normais. Nova tentativa de DELETE não altera saldo novamente; com If-Match desatualizado pode retornar 412. Não há restauração.

## Transferências

| Método | Rota | Entrada/finalidade | Sucesso | Erros principais |
|---|---|---|---|---|
| POST | /transfers | CreateTransferRequest | 201 | 400, 404, 409 |
| GET | /transfers | Consulta por accountId/período | 200 | 400, 404 para referência inválida |
| GET | /transfers/{id} | Recurso e ETag | 200 | 404 |
| DELETE | /transfers/{id} | Cancelamento lógico | 204 | 404, 412, 428 |

CreateTransferRequest: sourceAccountId, destinationAccountId, amount, occurredOn, description opcional até 500. Mesma conta/data/valor inválidos: 400. Conta alheia/inexistente: 404. Conta inativa: 409. Sem PUT e sem categoria. Canceladas não aparecem nas consultas financeiras normais. Cancelar novamente não reaplica efeito; versão antiga pode resultar em 412.

## Relatórios e extratos

| Método | Rota | Parâmetros | Retorno |
|---|---|---|---|
| GET | /reports/summary | from, to, accountId opcional | currency, período, income, expense, result |
| GET | /reports/by-category | from, to, accountId/kind opcionais | Totais por categoryId, nome e tipo |

Erros: 400 em período/filtro inválido, 404 em conta alheia/inexistente; além dos erros globais. Período obrigatório, inclusivo e limitado a 366 datas, sem futuro. Transferências não entram em receita/despesa nem por categoria.

Extrato retorna accountId, currency, from, to, accountOpeningDate, accountOpeningBalance, openingBalance do período, closingBalance, totais do período e coleção paginada de movimentos. Os dados da abertura da conta permitem explicar o saldo quando ela ocorre dentro do intervalo: saldo inicial não é receita. Cada movimento informa referência, natureza income/expense/transferIn/transferOut, data, valor com sinal e contraparte quando houver transferência. Totais e saldos consideram todo o período, não somente a página. Não prometer saldo acumulado por linha no MVP.

## Paginação, filtros e ordenação

page padrão 1, pageSize padrão 20 e máximo 100; valores fora do intervalo retornam 400. Página além do fim retorna items vazio e totais corretos. Ordem sempre desempata por id.

- Contas/categorias: name/id e filtros documentados de tipo/atividade.
- Transações: accountId, categoryId, kind, from, to, minAmount, maxAmount; sort por occurredOn, amount ou id.
- Transferências: accountId, from, to; sort por occurredOn, amount ou id.
- Extrato: período e paginação; ordem financeira por data e referência estável.
- Padrão de movimentos: occurredOn decrescente, id como desempate. sort aceita campo e direção documentados; campo/direção desconhecidos retornam 400.

from/to opcionais nas listas; quando ambos presentes validar ordem, e quando presentes rejeitar futuro. minAmount <= maxAmount. Filtros com identificador de recurso alheio não podem revelar sua existência. Sem SQL dinâmico com campo arbitrário do cliente. Sem snapshot entre páginas de requisições diferentes.

## Cabeçalhos, precondições e replay

Idempotency-Key obrigatório em POST /accounts, /transactions e /transfers; regras e comprimento no ADR-007. Ausente/malformado: 400; mesmo comando: 201/Location/corpo originais; conteúdo diferente: 409. Replay não representa necessariamente o estado atual da entidade.

GET individual de recurso financeiro retorna ETag; recursos também podem expor version opaco para clientes de listas. PUT/PATCH/DELETE exigem um If-Match com a versão esperada. Não aceitar wildcard como forma de ignorar concorrência. Ausente: 428; malformado: 400; desatualizado: 412. Criação pode retornar ETag do recurso criado.

## Erros e OpenAPI

Usar application/problem+json conforme RFC 9457: type, title, status, detail, instance, code estável, traceId e errors por campo quando aplicável. Não expor stack trace, SQL, tokens ou dados de outro usuário.

Erros globais: 401 para autenticação ausente/inválida/revogada; 403 para política que proíba usuário autenticado; 429 com Retry-After; 415 para corpo em formato não suportado; 500 genérico e 503 para indisponibilidade conhecida. Preservar WWW-Authenticate quando aplicável e padronizar também desafios de autenticação e rate limiting.

OpenAPI nativo do ASP.NET Core e Swagger UI em Development. Documentar versões, DTOs, enums, exemplos fictícios, Bearer, códigos e cabeçalhos. Disponibilidade pública de UI/spec será decidida no preparo da hospedagem. Não existe contrato implementado nesta entrega.
