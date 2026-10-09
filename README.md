# Expenses Tests — laboratório local

Projeto separado para integração, APIs pela rede, navegador, performance e segurança. O backend mantém 46 testes unitários; o frontend mantém testes isolados de componentes. Nenhuma aplicação referencia este projeto.

Os 98 casos originais foram preservados: **46 unitários + 49 integrações locais + 3 Cognito opt-in**. `inventory.json` registra a origem; `python3 scripts/verify_inventory.py` confere as contagens. As telas de autenticação estão implementadas: seis jornadas UI cobrem cadastro, login, recarga/renovação, bloqueio, preferência de e-mail, múltiplas abas, duplicidade e viewport móvel, além dos dois testes de cookies e nove HTTP. Os 11 unitários da interface ficam em expenses-ui.

## Preparação

Manter este diretório como irmão de `expenses-service`, `expenses-ui` e `expenses-liquibase`. Requisitos: .NET 10 SDK, Node **24**, Python 3, OpenSSL, Java/Liquibase 4.33 e Docker Desktop Linux em execução. A conexão ao Docker usa o contexto selecionado pelo usuário. Não requer AWS, credenciais do desenvolvimento ou instalação de certificados no sistema.

```sh
# expenses-tests
npm ci
npx playwright install chromium
# expenses-ui
npm ci
```

Ferramentas fixadas: Playwright 1.64.0, Vitest 5.0.3, Gitleaks 8.30.1; imagens PostgreSQL 17.11, k6 2.3.0 e ZAP 2.17.0. k6 e ZAP executam em Docker. Gitleaks deve estar instalado para a auditoria completa. O primeiro uso pode baixar pacotes/imagens; a execução da aplicação usa somente dependências locais. Versões efetivamente usadas aparecem nos relatórios.

## Comandos

Executar em `expenses-tests`, exceto quando indicado:

| Finalidade | Comando |
| --- | --- |
| Backend unitário, em expenses-service | `dotnet test Expenses.sln -c Release` |
| Frontend unitário, em expenses-ui | `npm test` |
| Frontend lint/build, em expenses-ui | `npm run lint && npm run build` |
| Ciclo local consolidado, sem carga/ZAP | `python3 scripts/check.py` |
| Ciclo local + ZAP passivo | `python3 scripts/check.py --zap` |
| Inventário original | `python3 scripts/verify_inventory.py` |
| 49 integrações HTTP/PG | `python3 scripts/integration.py` |
| Upgrade/integridade/rollback Liquibase | `python3 scripts/schema.py` |
| API pela rede | `python3 scripts/lab.py api` |
| Navegador real | `python3 scripts/lab.py e2e` |
| API + navegador | `python3 scripts/lab.py all` |
| Tipagem dos testes | `npm run typecheck` |
| Baseline por operação | `python3 scripts/lab.py performance --profile baseline` |
| Sanidade de carga, 2 min | `python3 scripts/lab.py performance --profile smoke` |
| Carga gradual, 15 min | `python3 scripts/lab.py performance --profile load` |
| Jornadas, 10 min | `python3 scripts/lab.py performance --profile journey` |
| Pico exploratório | `python3 scripts/lab.py performance --profile spike` |
| Duração prolongada, 1 hora | `python3 scripts/lab.py performance --profile soak` |
| Dependências/segredos | `python3 security/audit.py` |
| ZAP passivo | `python3 scripts/lab.py security` |
| ZAP ativo restrito, após passivo | `python3 scripts/lab.py security --active` |
| Cognito real, fora do ciclo local | Ver [perfil externo](external/README.md) |

Os comandos de compatibilidade `expenses-infrastructure/scripts/test-postgres.py` e `expenses-liquibase/tests/validate_schema.py` encaminham para os novos runners. O primeiro agora executa apenas integrações locais, mesmo com opt-in Cognito herdado. Não executar performance simultaneamente com builds, scanners ou outras suítes.

Cada comando de laboratório cria seus próprios recursos, migra com Liquibase, verifica prontidão, executa e limpa no `finally`. Falhas geram saída diferente de zero; o runner de integração exige exatamente 49 aprovações, sem ignorados. ZAP retorna falha quando encontra avisos: isso significa pendência de triagem, não aprovação silenciosa.

## Topologia e limites

```text
Playwright / k6 / ZAP → proxy HTTPS descartável → Kestrel 1 ou 2
                                                  ↓        ↓
                                         PostgreSQL privado
                                         Identidade local compartilhada
```

Nesta primeira implementação, API, proxy e identidade são processos locais, com portas dinâmicas em loopback; PostgreSQL e geradores/scanner são containers isolados. A opção .NET 10 `WebApplicationFactory.UseKestrel` executa o `Program` real em Release. Isso evita refatorar o pipeline produtivo apenas para permitir testes. A topologia difere da proposta inicial de Compose completo: a API não tem limites rígidos de CPU/memória e o proxy de laboratório é Node, **não é uma validação da configuração Nginx de produção**. Transportar tudo para containers e revalidar o proxy final continua pendente.

Somente a composição de teste substitui `IIdentityProvider`. O simulador mantém dados em memória compartilhados pelas duas instâncias, emite JWT RS256 e usa o validador criptográfico real com issuer/client/chave explícitos de teste. Não há flag ou fallback na API de produção. Ele não reproduz quotas, políticas, latência ou todos os erros Cognito.

Chaves TLS/JWT/HMAC, key ring e configuração autenticada do scanner existem apenas em `.local/`, com permissões restritas, e são removidos ao terminar. Node/Python/k6 validam a CA privada. Chromium usa pin da chave pública daquele certificado apenas no processo descartável; nenhum chaveiro do sistema é alterado. ZAP é utilizado para comportamento HTTP, não como prova de validação TLS do cliente. Nenhum cookie Secure ou controle CSRF é desligado.

O perfil local remove variáveis AWS/Cognito dos processos e nunca usa o volume/container `expenses-postgres`. PostgreSQL tem 1 CPU/512 MB; k6 1 CPU/512 MB; ZAP 1 CPU/2 GB. Todas as identidades e bancos são descartáveis. Limpeza usa identificadores/labels da execução; não há prune global. Em interrupção forçada do sistema (`kill -9`, desligamento), conferir containers com labels `expenses.tests.*` antes de remover recursos daquela execução; não excluir volumes de desenvolvimento.

## Performance

Cada execução prepara 1.000 perfis pela API, faz aquecimento e mede separadamente cadastro, login, consulta, renovação e logout. Baseline exige 1.000 amostras de cada operação e p95 < 150 ms. Repetir a baseline três vezes em condições equivalentes. `operation_*_ms` mede o tempo completo do cliente; métricas HTTP agregadas incluem preparação e não devem substituir as métricas por operação.

Carga fixa de consultas usa 1→5→10 requisições/s e falha se houver iterações não iniciadas. Jornadas usam pausas de 1–3 s, sem fazer login a cada leitura; pico até 100 usuários é hipótese exploratória, não demanda validada. O perfil de uma hora mantém prazos reais e permite renovação após 15 minutos. Uma consulta que renova deve ser interpretada junto do cenário específico de refresh; ainda falta separar automaticamente as renovações nessa série longa.

Resultados abaixo de 150 ms com identidade simulada não comprovam o requisito com Cognito nem capacidade para 1.000 usuários simultâneos. O único número de negócio confirmado é 1.000 **cadastrados**. Perfis longos preparados não equivalem a perfis já executados/aprovados; consultar a entrega e os relatórios.

## Evidência e segurança

`reports/<run-id>/` contém resumo, resultados do runner e logs locais. Performance inclui `k6.json` e amostras de CPU/RSS dos processos/recursos PostgreSQL. Não há ainda métricas internas de pool SQL, tracing distribuído ou dashboards. Artefatos ficam fora do Git; relatórios novos são privados. Não enviar logs brutos a serviços externos.

Playwright não grava traces/HAR/vídeo. ZAP mantém relatório bruto e cookies apenas na pasta temporária e exporta achados redigidos. A prova do scanner registra quantidades de respostas 200 na rota protegida, sem headers/tokens. A varredura limita o OpenAPI do mesmo build a health, cadastro e probe protegido de teste. Login/renovação/logout são cobertos por cenários dedicados e excluídos do scan para não revogar a própria sessão. Isso não é cobertura dinâmica completa de autenticação.

Gitleaks varre snapshot dos fontes respeitando arquivos ignorados; não envia conteúdo ao serviço de auditoria. A única exceção explícita é o UUID de Idempotency-Key de um exemplo documental, restrita àquele valor e arquivo. A auditoria npm/NuGet consulta avisos públicos de dependências.

Ver [matriz e pendências](../expenses-docs/testing/2026-10-08_12-02-50/2-execucao.md) e [plano original](../expenses-docs/testing/2026-10-08_12-02-50/1-plano.md). Não existe certificação de segurança ou liberação de produção implícita em uma suíte verde.

## Referências das ferramentas

- [.NET 10 — Kestrel em WebApplicationFactory](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.mvc.testing.webapplicationfactory-1.usekestrel?view=aspnetcore-10.0).
- [k6 — opções e critérios](https://grafana.com/docs/k6/latest/using-k6/k6-options/reference/).
- [ZAP — API scan, modos e códigos de saída](https://www.zaproxy.org/docs/docker/api-scan/).
