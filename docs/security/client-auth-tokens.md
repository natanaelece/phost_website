# Hardening de tokens de clientes

## Matriz de evolução do schema

| Coluna legada | Coluna nova | Backfill | Limpeza do texto puro | Índice novo | Consulta após a migração | Impacto no rollback |
| --- | --- | --- | --- | --- | --- | --- |
| `user_sessions.token` | `user_sessions.token_hash CHAR(64)` | SHA-256 hexadecimal minúsculo de cada token existente | Na mesma transação, depois da verificação de que todas as sessões possuem hash | `UNIQUE (token_hash)` | Somente `token_hash = @TokenHash` | A coluna legada permanece anulável e vazia. Rollback do binário exige restauração de backup; não é possível reconstruir tokens brutos a partir dos hashes. |
| `users.email_confirmation_token` e eventual `users.email_confirmation_token_hash` intermediária | `email_confirmation_tokens.token_hash CHAR(64)` | SHA-256 do token bruto legado e cópia validada de qualquer hash intermediário, sem duplicar | Na mesma transação, depois de confirmar que cada hash pertence ao usuário correto | `UNIQUE (token_hash)` e índice parcial por usuário/validade | Somente a tabela dedicada | Todos os links existentes continuam válidos. Rollback do binário exige restauração de backup ou novo envio de confirmação. |
| `users.password_reset_token` | `users.password_reset_token_hash CHAR(64)` | SHA-256 hexadecimal minúsculo de cada token existente | Na mesma transação, depois da verificação de que todo token pendente possui hash | Índice único parcial para hashes não nulos | Somente `password_reset_token_hash = @TokenHash` | Links existentes continuam válidos no código novo. Rollback do binário exige restauração de backup ou nova solicitação de recuperação. |

As colunas legadas de sessão e recuperação permanecem anuláveis para inspeção
e rollback. `users.email_confirmation_token_hash` não faz parte do schema novo;
se existir por execução parcial, seu conteúdo é migrado e a coluna fica nula e
depreciada. `users.email_confirmation_token` também termina nula. Nenhuma das
duas é consultada depois da migração.

## Contrato e arquitetura

O contrato temporário do cliente não mudou:

- o navegador recebe o token bruto de sessão e o guarda somente em
  `localStorage` sob `premier_token`;
- chamadas autenticadas continuam enviando `X-Session-Token`;
- cada sessão continua válida por sete dias;
- múltiplas sessões simultâneas continuam permitidas;
- a migração para cookie `HttpOnly` é uma etapa futura e está fora deste
  hardening.

`SecurityTokenService` gera 32 bytes com `RandomNumberGenerator`, codifica em
Base64 URL-safe sem padding, valida formato/tamanho e calcula SHA-256
hexadecimal minúsculo. Tokens novos nunca usam GUID, BCrypt, criptografia
reversível ou logging.

`ClientSessionService` é a única camada de produção que consulta
`user_sessions`. Ele cria, valida, localiza, encerra, revoga, rotaciona e limpa
sessões. Antes de consultar o banco, valida o token bruto e calcula
`token_hash`. Toda validação também exige `expires_at` futuro e
`users.is_active = true`.

Tokens brutos de confirmação e recuperação existem apenas na requisição que os
gera e no link enviado por e-mail. O banco recebe somente o SHA-256.

`EmailConfirmationTokenService` é a única fonte ativa de SQL de confirmação.
`email_confirmation_tokens` permite vários links válidos por usuário e contém
somente `user_id`, hash, datas técnicas, origem do envio, claim e código de
falha sanitizado. Não guarda destinatário, URL, corpo, resposta SMTP ou token
bruto.

## Fluxos de senha

Na troca autenticada, a sessão atual e a senha atual são verificadas antes da
mudança. A atualização BCrypt, a atualização de `pending_ad_credentials` quando
aplicável, a revogação de todas as sessões e a criação da nova sessão de sete
dias acontecem na mesma transação PostgreSQL. A resposta devolve o novo token
bruto e a expiração; o painel substitui `premier_token`. Ausência ou falha ao
persistir o novo token limpa o estado local e exige novo login.

Na redefinição por link, a mesma transação atualiza o BCrypt, consome o hash do
link, atualiza a credencial AD pendente quando aplicável e revoga todas as
sessões. Nenhuma sessão é criada automaticamente. Replay do link falha.

A sincronização posterior com o AD continua best-effort e fora da transação
PostgreSQL, preservando o comportamento operacional existente.

## Confirmação, recuperação e lembretes

Cadastro e recuperação geram um token novo de 32 bytes, persistem apenas o hash
e enviam somente o valor bruto. O cadastro confirma usuário, hash e demais
dados em uma transação curta, fecha a conexão e só então chama SMTP. Falha ou
timeout nunca apaga usuário ou hash; portanto, mesmo que o servidor tenha
aceitado a mensagem antes da falha percebida pelo cliente, o link permanece
válido.

Cada lembrete e reenvio administrativo cria outro registro. Links anteriores
continuam válidos. O envio tem três fases:

1. uma transação curta bloqueia/reivindica o usuário, insere hash e claim com
   validade de dez minutos, confirma e libera conexão/locks;
2. SMTP recebe o token bruto fora de qualquer transação PostgreSQL;
3. sucesso abre nova transação curta para `sent_at`, contador e agenda; erro
   grava apenas `failed_at` e uma categoria sanitizada e agenda retry sem
   consumir a cota.

O token continua válido mesmo quando marcado com falha, porque um timeout pode
ter ocorrido depois da aceitação SMTP. É aceitável haver vários links válidos;
confirmar qualquer um marca todos os tokens do usuário como usados.

O claim persistido impede dois workers ou um worker e um reenvio administrativo
de enviarem simultaneamente. Um crash não exige recuperar o token bruto: após
dez minutos, a limpeza marca o claim abandonado com `claim_expired` e uma nova
tentativa gera outro token. O cliente SMTP tem timeout inferior ao claim.
Tokens usados ou expirados são retidos por sete dias para diagnóstico
estrutural e então removidos.

O fluxo anterior não possuía expiração temporal de confirmação. Para preservar
esse contrato, os registros novos e migrados usam `expires_at = infinity` e
continuam válidos até a confirmação da conta. O schema permite prazos finitos
futuros sem alterar a consulta ou a limpeza.

## Migração idempotente

`DatabaseInitializer` executa a migração em uma única transação:

1. cria hashes de sessão/recuperação, a tabela dedicada e seus índices;
2. calcula SHA-256 em C# para cada valor bruto legado;
3. migra também hashes intermediários de confirmação, com `ON CONFLICT` e
   verificação de pertencimento ao usuário;
4. preserva `user_id`, aproxima `created_at` pela data do cadastro e usa a
   expiração atual sem limite temporal;
5. verifica que toda sessão e recuperação pendente receberam hash e que toda
   confirmação legada existe na tabela;
6. limpa textos puros e anula a coluna hash intermediária, se presente;
7. torna `user_sessions.token_hash` obrigatório e conclui índices/agendas;
8. verifica contagens e formato antes do commit.

A operação é reiniciável: falha em qualquer passo desfaz a transação inteira;
repeti-la não duplica tokens. O código novo não contém fallback para colunas
legadas. Links e sessões anteriores continuam válidos pelo backfill.

Após o deploy, a verificação somente leitura é:

```bash
dotnet bin/Release/net8.0/PremierAPI.dll --validate-client-auth-storage
```

Ela informa exclusivamente:

- sessões sem hash;
- tokens legados não nulos;
- hashes em formato inválido.

Os três números devem ser zero. O comando nunca imprime valores de token ou
hash.

## Plano de deploy

Este plano exige aprovação e janela operacional:

1. validar e integrar o commit primeiro em `development`, sem reescrever
   histórico;
2. criar backup lógico consistente do PostgreSQL e validar sua restauração em
   banco temporário;
3. registrar o hash do artefato Release validado;
4. parar `premierapi` para impedir novas sessões durante o backup final;
5. fazer o backup final e preservar também a versão anterior do binário/assets;
6. publicar código e schema juntos e iniciar uma única instância, permitindo
   que a migração transacional termine;
7. executar `--validate-client-auth-storage` e exigir três zeros;
8. validar cadastro com SMTP controlado, dois links simultâneos,
   confirmação/replay, lembrete/claim, recuperação/reset, dois logins, troca
   autenticada, rotação e logout;
9. somente então liberar tráfego e monitorar erros de banco, autenticação e
   SMTP.

Não execute a aplicação antiga depois que a migração tiver confirmado: ela
consulta as colunas em texto puro, agora vazias.

## Rollback

Antes do commit da migração, basta manter ou restaurar o binário anterior. A
própria transação preserva o schema/dados anteriores em caso de falha.

Depois do commit da migração, não existe rollback de dados por transformação
inversa: SHA-256 não recupera tokens brutos. O rollback seguro é:

1. retirar tráfego e parar o serviço;
2. restaurar o backup final do banco que contém os tokens legados;
3. restaurar o binário e os assets anteriores compatíveis;
4. iniciar o serviço anterior e validar login, confirmação e recuperação;
5. investigar a causa antes de nova tentativa.

Se a restauração do banco não for aceitável por perda de operações posteriores,
o caminho é roll-forward com o código novo corrigido, mantendo o banco
hasheado. Reintroduzir tokens em texto puro ou preencher as colunas legadas não
é um rollback aceitável.

Nenhuma instrução deste documento autoriza aplicação de schema, restart, push
ou deploy em produção.
