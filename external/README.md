# Cognito real — execução explicitamente separada

Os três testes originais foram preservados e compilados. Não são executados pelo laboratório local.

Este perfil cria identidades sintéticas no pool configurado, executa cadastro/login/renovação/revogação e tenta removê-las no encerramento. Exige credenciais AWS privadas e configuração Cognito da API. Nunca colocar ClientSecret, senha ou chave HMAC em comandos/documentos versionados. Antes de usar, confirmar o pool de testes: `EXPENSES_COGNITO_TEST_POOL_ID` deve coincidir com o pool efetivamente configurado; o runner não cria recursos AWS.

Depois de configurar privadamente `EXPENSES_RUN_COGNITO_TESTS=1` e `EXPENSES_COGNITO_TEST_POOL_ID`, executar a partir de `expenses-tests`:

```sh
python3 scripts/external.py
```

O PostgreSQL continua descartável e isolado. A execução local padrão força o opt-in a zero mesmo que o terminal herde essa variável. Não executar carga nem scanners neste perfil. A homologação em Cognito de testes separado permanece uma etapa futura.
