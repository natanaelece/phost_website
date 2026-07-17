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
