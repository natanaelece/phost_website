# Política de execução

Antes de executar comandos, identifique o ambiente pelo `hostname` e pelo
diretório atual.

## Execução direta no host de runtime

Quando o `hostname` for `website` e o projeto estiver em
`/var/www/premierhost/PremierAPI`, execute diretamente nesse diretório todos os
comandos do projeto, incluindo leitura, pesquisa, edição, Git, build, testes,
scripts, logs, banco de dados, Docker, serviços e diagnósticos.

Nesse cenário, não abra uma nova conexão com `website`: o Codex já está no host
correto.

## Execução a partir do llm-server

Quando o `hostname` for `llm-server` e o projeto estiver montado em
`/opt/remotes/PremierAPI`, use esse diretório somente para ler, pesquisar e
editar arquivos.

Não crie nem mantenha arquivos específicos da tarefa fora desse mount no
filesystem local do `llm-server`. Código, documentação e outros artefatos que
pertencem ao repositório devem ficar em `/opt/remotes/PremierAPI`. Artefatos não
versionados devem ser criados diretamente no host `website`; backups devem
ficar preferencialmente em `/var/backups` nesse host.

Todos os comandos relacionados ao projeto devem ser executados no host
`website`, incluindo Git, build, testes, scripts, logs, banco de dados, Docker,
serviços, diagnósticos e geração de artefatos. Use:

`ssh website 'cd /var/www/premierhost/PremierAPI && COMANDO'`

Se a conexão falhar, pare e informe o erro. Não execute comandos do projeto
localmente no `llm-server` como fallback.

Se o ambiente não corresponder a nenhum dos dois cenários, pare e informe os
valores reais de `hostname`, diretório atual e raiz Git antes de prosseguir.

Migrations, deploys, reinicializações, alterações de banco e comandos
destrutivos precisam de autorização explícita do usuário.

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
