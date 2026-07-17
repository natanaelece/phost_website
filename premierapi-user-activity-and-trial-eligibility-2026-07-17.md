# PremierAPI — atividade de usuários e elegibilidade do teste grátis

**Data:** 17/07/2026
**Modelo:** GPT-5
**Reasoning effort:** high
**Branch:** `feat/free-trial-requests`
**Estado:** três commits validados; deploy e alteração ativa do banco não executados

## Commits

- `74a20697573409bcdc89d7dc607ab94d659ba275` — completa metadados ausentes após login válido, sem sobrescrever valores existentes.
- `506e5ba87ac0c8a44e74eb7b6494e095d136649c` — registra cadastro, login e logout explícito em trilha persistente e adiciona o checkbox Usuários em Admin > Logs.
- `e0b4cf184e86c6bd9b20407e46422ba8e590111b` — oculta e bloqueia teste grátis para usuários que possuem ou já possuíram pedido pago.

## Regras implementadas

- Metadados recuperados no login recebem `registration_source=login_recovery` somente quando a origem anterior era nula.
- `user_activity_events` não armazena senha nem token e é removida em cascata com o usuário.
- Cadastros existentes recebem evento histórico idempotente com a data original; logins e logouts antigos não são inventados.
- Logout é registrado somente quando o usuário usa a ação explícita de sair; fechamento de aba não é tratado como logout.
- Pedido com `status=pago` ou `canceled_was_paid=true` torna o usuário inelegível ao teste grátis.
- A inelegibilidade é aplicada na consulta, na solicitação, na liberação administrativa e na interface.
- Link direto mostra modal; API retorna HTTP 403 com “O teste grátis é exclusivo para novos usuários.”
- Teste previamente marcado como utilizado continua bloqueado pela regra existente.

## Validação

- `dotnet build --no-restore`: 0 avisos, 0 erros.
- Todos os JavaScript e scripts inline dos HTML: sintaxe válida.
- `git diff --check`: passou.
- Scan das adições por assinaturas de segredo: passou.
- Consultas de elegibilidade: preparadas/validadas em transação PostgreSQL read-only.
- Worktree remoto: limpo.

## Estado operacional posterior

O schema e o código passaram a estar ativos após reinicialização posterior do
serviço. Em uma autorização subsequente, foi corrigida e implantada também a
rota administrativa descrita abaixo.

## Correção posterior da rota administrativa

- Sintoma: o botão **Recusar** devolvia mensagem genérica e mantinha a
  solicitação do Natanael em `solicitado`.
- Causa confirmada: a rota usava `{action}`, nome reservado pelo roteamento
  MVC; as operações retornavam HTTP 404 vazio antes de entrar no controller.
- Correção: parâmetro renomeado para `{operation}`.
- Commit em `development`:
  `c276a334cfc4b67f3e7b12e418a47f344b9e094e`.
- Backup:
  `/var/backups/premierapi-free-trial-route/20260717-095831/AdminController.cs`.
- Deploy autorizado: `premierapi` reiniciado e ativo.
- Validação local e pública: ação inválida na solicitação real retornou JSON
  HTTP 409; UUID inexistente com `reject` retornou JSON HTTP 404; zero logs
  críticos; a solicitação real permaneceu `solicitado` durante as provas.

## Gestão administrativa posterior

- Commit em `development`:
  `edc3a2fd102ccde16d456b32295544c85cca943f` — adiciona liberação manual para
  usuário elegível sem solicitação e exclusão restrita a solicitações recusadas.
- A liberação manual cria o registro diretamente como `liberado`, preenche
  `released_at`/`released_by` e grava evento administrativo `liberado`.
- A regra de inelegibilidade por pedido pago permanece no frontend e no
  backend também para a liberação manual.
- A lixeira usa o padrão visual já existente, aparece somente em `recusado`,
  depois do WhatsApp, e exige confirmação. A API rejeita a exclusão de qualquer
  outro estado; ao excluir, os eventos vinculados são removidos pela cascata já
  existente e o usuário volta a `nao_solicitado`.
- Backup anterior à edição:
  `/var/backups/premierapi-free-trial-admin/20260717-132412`.
- Validações: `dotnet build --no-restore` com 0 avisos/erros, sintaxe de todos os
  JavaScript válida, `git diff --check` válido, varredura do commit por
  assinaturas de segredo aprovada e worktree limpa.
- O serviço não foi reiniciado nesta etapa; o deploy autorizado posterior está
  registrado ao final deste relatório. Nenhuma solicitação real foi criada ou
  excluída durante as provas.

## Bloqueio posterior de solicitação recusada

- Commit em `development`:
  `c6d67d85420303c0d7e8a2d20538996595a6a709` — impede que uma solicitação
  `recusado` seja reaberta ou novamente solicitada enquanto o registro existir.
- O painel exibe “Solicitação não liberada” com botão desabilitado e respeita
  também o `canRequest=false` calculado pelo backend.
- Uma chamada direta ao endpoint de solicitação retorna HTTP 409 e preserva o
  estado recusado; somente a lixeira administrativa devolve `nao_solicitado`.
- Cancelamentos permanecem reenviáveis, sem mudança nessa regra.
- Backup anterior à correção:
  `/var/backups/premierapi-free-trial-rejection/20260717-133454`.
- Validações: build com 0 avisos/erros, JavaScript externo e inline válidos,
  verificação específica de que `recusado` não pertence aos estados reabertos,
  `git diff --check` e scan de segredo aprovados; worktree limpa.
- Nesta etapa o serviço permanecia sem restart; o deploy autorizado posterior
  está registrado abaixo.

## Deploy dos fluxos administrativos e bloqueio de recusados

- Reinicialização de `premierapi` autorizada pelo operador e executada em
  17/07/2026 às 13:39:14 UTC.
- Serviço `active`, aplicação ouvindo em `0.0.0.0:5000`, `NRestarts=0` e uma
  inicialização confirmada no journal após o restart.
- Rotas `DELETE /api/admin/free-trials/{id}` e
  `POST /api/admin/free-trials/users/{userId}/release` responderam HTTP 401 sem
  credencial, confirmando carregamento e proteção administrativa sem alterar
  dados.
- O painel ativo contém “Solicitação não liberada” e respeita
  `canRequest=false`; os assets administrativos ativos contêm a exclusão de
  recusados e a liberação manual.
- Zero linhas críticas, falhas, exceções não tratadas ou erros no journal desde
  a inicialização. Nenhuma solicitação real foi criada, reaberta ou excluída
  durante a validação.
