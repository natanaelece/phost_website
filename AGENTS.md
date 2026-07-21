# Política de execução remota

Este repositório está montado no llm-server através de SSHFS.

- Caminho local: `/opt/remotes/PremierAPI`
- Host de runtime: `website`
- Caminho remoto: `/var/www/premierhost/PremierAPI`

O SSHFS deve ser usado apenas para ler, pesquisar e editar arquivos.

Todos os comandos relacionados ao projeto devem ser executados no servidor
`website`, incluindo Git, build, testes, scripts, logs, banco de dados, Docker,
serviços e diagnóstico de runtime.

Use sempre:

`ssh website 'cd /var/www/premierhost/PremierAPI && COMANDO'`

Nunca execute localmente no llm-server:

- `dotnet`, builds ou testes
- migrations ou comandos de banco
- scripts do projeto
- Docker ou Docker Compose
- inicialização ou reinicialização de serviços
- comandos Git que alterem estado

Se o SSH falhar, pare e informe o erro. Nunca use execução local como fallback.

Migrations, deploys, reinicializações, alterações de banco e comandos
destrutivos precisam de autorização explícita do usuário.

## Isolamento do llm-server

Este projeto está montado no `llm-server` em
`/opt/remotes/PremierAPI`, mas pertence ao host `website` e à raiz remota
`/var/www/premierhost/PremierAPI`.

Nenhum arquivo específico da tarefa pode ser criado ou mantido no filesystem
local do `llm-server`.

Código, documentação, relatórios, handoffs, planos, checkpoints, backups,
dumps, logs, temporários, resultados de testes e quaisquer outros artefatos
devem ficar:

- dentro de `/opt/remotes/PremierAPI`, quando pertencem ao repositório; ou
- diretamente no host `website`, quando não devem ser versionados.

Backups não versionados devem ser criados remotamente, preferencialmente em
`/var/backups`, por meio de SSH.

Todos os comandos Git, build, teste, runtime, banco, serviço e geração de
artefatos devem executar com:

`ssh website 'cd /var/www/premierhost/PremierAPI && COMANDO'`

Se o SSH falhar, pare. Nunca execute localmente como fallback.

## Proteção de variáveis sensíveis

O serviço `premierapi` carrega suas variáveis pelos arquivos protegidos
`/etc/premierapi/premierapi.env` e
`/etc/premierapi/telegram-alerts.env`. Eles ficam fora do repositório,
pertencem a `root` e devem manter modo `0600`.

O drop-in `/etc/systemd/system/premierapi.service.d/override.conf` deve conter
somente as referências `EnvironmentFile=` e nunca valores inline em
`Environment=`. Nunca exiba ou registre `systemctl show premierapi -p
Environment`. Para diagnóstico, consulte apenas propriedades não sensíveis,
como `FragmentPath`, `DropInPaths` e `EnvironmentFiles`, e use
`dotnet PremierAPI.dll --validate-configuration`, que informa apenas nomes de
chaves ausentes ou inválidas.

Qualquer rotação de credenciais, alteração dos arquivos protegidos, mudança da
unit ou reinicialização exige autorização explícita e janela operacional.

O estado do segundo fator administrativo fica em
`/var/lib/premierapi/admin-totp.protected`, pertencente a `root` e com modo
`0600`. Nunca exiba ou registre a chave TOTP, a URI `otpauth` nem os códigos de
recuperação. O arquivo só é recuperável junto com o key ring e o certificado do
Data Protection; redefini-lo ou removê-lo exige autorização explícita e janela
operacional.
