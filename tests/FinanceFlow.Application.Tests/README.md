# Testes de aplicação

Testes xUnit das entradas de identidade e dos casos de uso de contas. Incluem proprietário autenticado, calendário de São Paulo, TimeProvider determinístico, validações, hash canônico e encaminhamento para a porta de persistência. Os doubles pequenos desse projeto não comprovam transações SQL; essa garantia pertence aos testes com Testcontainers em FinanceFlow.IntegrationTests.
