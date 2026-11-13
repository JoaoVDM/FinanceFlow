# ADR-002 — Autenticação, autorização e proteção de dados

Status: aprovado na revisão arquitetural. Escopo: API demonstrativa única.

## Contexto e alternativas

Identity resolve armazenamento de credenciais e ciclo de vida do usuário. A API precisa de tokens e de autorização por proprietário. Os tokens dos endpoints simplificados de Identity não devem ser confundidos com JWT padronizado.

| Alternativa | Avaliação |
|---|---|
| HS256 | Adequado à mesma aplicação emitindo e validando; menor operação de chaves |
| RS256/ES256 | Separa autoridade de emissão e validação; útil com outros serviços/clientes de confiança |
| Provedor OAuth/OIDC | Melhor para produto com SSO, múltiplos clientes ou identidade mais completa; adiado no portfólio |
| Somente expiração, sem revogação | Menor custo, mas não bloqueia imediatamente após senha alterada ou desativação |

## Decisão

Usar ASP.NET Core Identity para hash, validação de senha, normalização de e-mail e bloqueio por tentativas. Emissão explícita de JWT e validação pelo middleware JwtBearer. Sem implementação própria de hash ou criptografia.

- HS256, algoritmo permitido configurado explicitamente.
- Segredo aleatório criptográfico de pelo menos 32 bytes após decodificação; não usar senha humana. Exclusivo por ambiente, emissor, audiência e finalidade.
- Sem segredo no código, arquivo versionado, imagem, log ou exemplo utilizável. Inicialização falha se a configuração necessária estiver ausente ou inválida.
- Access token de 15 minutos; tolerância de relógio proposta de 30 segundos.
- Validar assinatura, emissor, audiência, expiração e `nbf` quando presente.
- Claims mínimas: `sub`, `jti`, `iat`, `exp`, `iss`, `aud` e `tokenVersion`. Sem dados financeiros ou segredos.
- Sem refresh token, sessão por dispositivo ou endpoint de renovação no MVP.

`TokenVersion` e `IsActive` serão consultados no banco em cada requisição autenticada. Alteração de senha, logout global e desativação incrementam a versão ou bloqueiam o usuário. Falha na consulta não libera acesso. Não depender apenas do Security Stamp do Identity para invalidar JWT.

A emissão deve usar o estado associado à credencial validada; não atualizar cegamente a versão de um login em disputa com alteração de senha. Esse cenário exige teste de integração.

## Autorização e credenciais

- Política padrão autenticada; somente cadastro, login e endpoints técnicos explicitamente previstos serão anônimos.
- Proprietário vem de `sub`, nunca do corpo ou de parâmetro livre.
- Buscar por Id e UserId, incluindo referências de conta, categoria, origem e destino.
- Recurso alheio e inexistente retornam 404. GUID não substitui autorização.
- Aplicar isolamento em listas, totais, relatórios e registros idempotentes.
- Filtros globais, se usados, são defesa complementar.
- DTOs impedem mass assignment de proprietário, saldo calculado e flags internas.

Senha proposta: mínimo 12, máximo 128 caracteres, frases-senha aceitas, sem truncamento. Usar hasher do Identity com custo calibrado futuramente. E-mail normalizado será único também no banco. Mensagens de login genéricas para senha inválida, bloqueio e usuário inativo.

## Local e público

Nos dois ambientes: autorização completa, segredos externos, entradas limitadas, erros seguros e proteção de tokens. Em local, usar dados fictícios, serviços em loopback e HTTPS de desenvolvimento para credenciais.

Na publicação: HTTPS público, HSTS, proxy confiável, banco sem exposição pública, identidade SQL de privilégio mínimo e procedimento de substituição do segredo. CORS fica desabilitado sem cliente web; depois, origens explícitas. CORS não é autorização. Não registrar senhas, tokens, connection strings ou corpos financeiros.

Rate limiting integra a autenticação e é obrigatório antes de exposição pública. Parâmetros iniciais sujeitos a medição: login 5/minuto/IP com bloqueio por conta, cadastro 3/minuto/IP, senha 5/15 minutos/usuário e API 120/minuto/usuário. Retornar 429 e Retry-After; confiar apenas nos proxies configurados ao identificar IP.

## Consequências e revisão

Uma consulta de usuário por requisição é aceita; sem cache inicial. Logout é global. Troca direta do segredo invalida tokens anteriores; rotação com sobreposição fica adiada.

JWT próprio não equivale a OAuth/OIDC. A Microsoft recomenda padrões e chaves assimétricas para cenários de produção; esta é uma escolha delimitada ao portfólio. Antes de uso contínuo com dados reais, revisar provedor, assinatura, confirmação/recuperação de e-mail, retenção e operação. Cadastro duplicado com 409 pode permitir inferência de existência e deve ser reavaliado com confirmação de e-mail.

Fontes: [Identity](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/identity-api-authorization?view=aspnetcore-10.0), [JWT bearer](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/configure-jwt-bearer-authentication?view=aspnetcore-10.0) e [OWASP JWT](https://cheatsheetseries.owasp.org/cheatsheets/JSON_Web_Token_Cheat_Sheet).
