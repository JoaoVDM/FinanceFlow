# Convenções do FinanceFlow

- Preserve o monólito modular: Api compõe HTTP; Application coordena casos de uso; Domain contém entidades e invariantes; Infrastructure implementa persistência e integrações.
- Dependências: Api → Application/Infrastructure; Application → Domain; Infrastructure → Application/Domain. Domain não depende de EF Core, Identity ou ASP.NET Core.
- Prefira código simples, nomes descritivos e responsabilidades claras. Evite padrões, interfaces e bibliotecas sem necessidade demonstrada.
- Use uma instrução por linha e blocos explícitos quando facilitarem a leitura. Separe etapas lógicas sem criar helpers triviais.
- Organize testes por comportamento, com preparação, execução e verificação legíveis. Preserve fixtures, descoberta e testes reais de SQL Server, concorrência e rollback.
- Preserve valores monetários em decimal e a validação exata do JSON antes do parsing; nunca arredonde silenciosamente ou use double/float para dinheiro.
- Preserve autenticação, revogação, isolamento por usuário e idempotência. Obtenha o proprietário da identidade autenticada; controllers não acessam diretamente o DbContext.
- Não execute migrations automaticamente no startup. Alterações em bancos persistentes exigem autorização específica; preserve migrations históricas.
- Não versione segredos, certificados privados ou credenciais; não os exponha em logs ou respostas.
- Respeite o escopo autorizado de cada entrega. Não antecipe funcionalidades futuras nem refatore componentes estáveis sem justificativa.
- Execute as validações apropriadas e relate somente resultados comprovados, distinguindo execuções próprias de evidências fornecidas pelo usuário.
- Não crie commits nem execute push sem autorização explícita.
