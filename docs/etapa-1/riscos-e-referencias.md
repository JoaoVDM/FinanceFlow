# Riscos, pendências e referências

## Status e premissas

Arquitetura revisada aprovada; implementação futura. Demonstração de uma API única, dados fictícios, BRL, uso individual e lançamentos manuais. Não presumir requisitos de serviço bancário ou uso real contínuo.

## Substituições e pontos de atenção

| Tema | Consolidação e tratamento |
|---|---|
| Assinatura JWT | HS256 substitui assimétrica obrigatória no MVP demonstrativo; provedor/assimétrica serão revistos para outro cenário |
| Isolamento de escrita | RepeatableRead localizado substitui Serializable abrangente; não remover proteção de atividade |
| Isolamento de leitura | Sem RCSI obrigatório inicial; Snapshot quando resposta multiquery precisar de visão pontual |
| Persistência | Sem executor transacional genérico antecipado; DbContext coordena alterações |
| Roadmap | Oito etapas substituem as dez originais |
| Fundamentos e testes | Fundação sem banco; integração SQL começa quando a persistência for introduzida |

Essas diferenças são refinamentos aprovados na conversa, não inconsistências a desfazer. Se a implementação exigir mudá-las, registrar novo ADR ou revisão explícita.

## Pendências registradas para revisão

| Ponto | Decisão atual | Quando validar |
|---|---|---|
| Vocabulário de tipos de conta | Campo Type previsto; valores ainda não enumerados na aprovação | Antes dos contratos definitivos de contas, etapa 4 |
| Corpo em DELETE /users/me | Confirmação de senha conforme contrato aprovado | Etapa 3, compatibilidade dos clientes/proxy; alternativa de rota exige revisão explícita |
| Normalização/collation | Nome normalizado único por usuário/tipo; chave idempotente com comparação exata | Mapeamentos das etapas 3/4 |
| Formato da solução | .sln/.slnx ainda não criado | Etapa 2, conforme SDK sem mudar arquitetura |
| Pacotes e SQL CU | Stack fixa; patches/tags ainda não escolhidos | Etapas 2/3, documentação oficial e compatibilidade |
| Snapshot multiquery | Garantia prevista quando necessária | Primeira resposta que precisar, possivelmente extrato na etapa 6 |
| Hospedagem | Não definida | Antes da etapa 8; afeta TLS, proxy, segredos, custos e backup |
| Recursos locais | Windows x64/WSL 2/Docker Linux como premissa | Antes do ambiente SQL em contêiner |
| Uso com dados reais | Não é a premissa inicial | Antes de divulgar uso contínuo: identidade, recuperação, retenção e operação |
| Vitrine.dev | Apresentação profissional prevista | Requisitos atuais no momento da publicação, sem presumir integração pronta |

Essas pendências não bloqueiam a documentação nem autorizam outra etapa. Questões de segurança não resolvidas devem bloquear a funcionalidade correspondente, e não receber solução silenciosa.

## Riscos técnicos e mitigação

- HS256: qualquer componente com o segredo pode emitir tokens. Não distribuir para terceiros; não usar segredo humano, repetido entre ambientes ou versionado. Substituição invalida tokens anteriores; rotação sobreposta adiada.
- Revogação: depende do banco por requisição. Falhar de modo fechado, sem cache inicial. Testar disputa de login versus senha/desativação.
- IDOR: proteger também filtros, vínculos, totais e idempotência; GUID e filtro global não bastam.
- Dinheiro: rejeitar fração de centavo antes de o SQL arredondar; validar limites e overflow de agregação.
- Correções históricas: exclusão lógica/edição mudam o passado. Não apresentar posição auditável imutável ou estorno contábil inexistente.
- Concorrência: leituras protegidas podem causar deadlock. Transações curtas, índices, ordem consistente e testes coordenados; rollback sempre seguro.
- Idempotência: crescimento de registros e replay de representação antiga são aceitos. TTL curto não será introduzido sem definir nova garantia.
- Relatórios: uma consulta comum não oferece automaticamente snapshot global. Testar consistência e introduzir Snapshot localizado quando necessário.
- Docker/SQL: recursos de memória, x64, WSL 2 e licenciamento precisam de verificação. SQL Developer para desenvolvimento/testes; não presumir licença produtiva. Docker Desktop tem condições próprias.
- Dados: seed apenas fictício e ambiente Development; segredos de demonstração externos. Não usar material proprietário de empresas.

## Execução local e publicação planejadas

Compose com API, SQL Server e tarefa explícita de migrations; volume nomeado, health checks e portas limitadas ao host. API utiliza nome do serviço SQL e usuário restrito. Conta administrativa somente em provisionamento. Migrations com credencial própria; sem atualização automática de schema a cada inicialização.

.env.example deve ter placeholders sem segredo utilizável; .env, chaves, certificados, logs, dumps, bin e obj ficam ignorados. Não iniciar serviços nesta etapa.

Em publicação: TLS, proxy conhecido, segredos gerenciados, rate limiting, banco privado e política de recuperação. CORS só para origens aprovadas. Não incentivar armazenamento de JWT em localStorage como padrão de cliente web.

## Documentação profissional futura

README principal com objetivo, funcionalidades entregues, stack, diagramas, requisitos, execução, ambiente, migrations, autenticação, exemplos, testes e limitações. ADRs e OpenAPI devem permanecer coerentes. CONTRIBUTING, SECURITY, templates, changelog e licença MIT proposta serão preparados na evolução correspondente.

Badges devem refletir checks reais. Material para GitHub/Vitrine.dev deve distinguir o planejado do entregue, com dados fictícios. Nenhum resultado de teste ou publicação é alegado nesta documentação.

## Referências primárias

As referências fundamentam comportamentos das ferramentas; as decisões de adequação ao MVP são do projeto. Versões concretas devem ser verificadas ao implementar.

- [Identity e endpoints de autenticação](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/identity-api-authorization?view=aspnetcore-10.0): distinguir tokens do Identity e JWT.
- [JWT bearer no ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/configure-jwt-bearer-authentication?view=aspnetcore-10.0): validação e recomendações de produção.
- [OWASP JWT](https://cheatsheetseries.owasp.org/cheatsheets/JSON_Web_Token_Cheat_Sheet): algoritmos e segredos HMAC.
- [OWASP REST Security](https://cheatsheetseries.owasp.org/cheatsheets/REST_Security_Cheat_Sheet.html): transporte, autorização e erros.
- [EF Core: concorrência](https://learn.microsoft.com/en-us/ef/core/saving/concurrency): tokens de versão e conflitos.
- [EF Core: transações](https://learn.microsoft.com/en-us/ef/core/saving/transactions): atomicidade e retentativas transacionais.
- [ASP.NET Core: testes de integração](https://learn.microsoft.com/en-us/aspnet/core/test/integration-tests?view=aspnetcore-10.0): WebApplicationFactory.
- [Testcontainers.MsSql](https://dotnet.testcontainers.org/modules/mssql/): banco real descartável para testes.
- [ASP.NET Core: OpenAPI e Swagger](https://learn.microsoft.com/en-us/aspnet/core/tutorials/web-api-help-pages-using-swagger/): geração e UI são componentes separados.
- [Docker Desktop com WSL 2](https://docs.docker.com/desktop/features/wsl/): ambiente Windows/Linux.
- [SQL Server no WSL 2](https://learn.microsoft.com/en-us/sql/linux/quickstart-install-connect-wsl-2?view=sql-server-ver16): escopo de desenvolvimento.
- [RFC 9457 — Problem Details](https://www.rfc-editor.org/rfc/rfc9457): formato de erros HTTP.

## Limite da validação da Etapa 1

A validação desta entrega verifica existência, conteúdo e links locais dos documentos. Não prova migrations, isolamento SQL, JWT, criptografia, desempenho ou execução no Docker. Essas evidências deverão ser produzidas nas etapas correspondentes.
