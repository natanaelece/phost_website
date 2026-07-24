# Regras de IA e contexto do repositório

Leitura obrigatória, nesta ordem, antes de alterar o projeto: `README.md`, este `rules.md` e `.agents/AGENTS.md`. Eles registram decisões de produto e invariantes que não devem ser redescobertos a cada chat.

`development` é a branch de trabalho e validação; `main` é a branch canônica
acompanhada por produção. Entregas devem ser commitadas e enviadas primeiro a
`development`, integradas em `main` sem reescrever histórico e então enviadas a
`origin/main`. Nunca configure produção para acompanhar `origin/development`.

A unit de produção deve executar diretamente
`bin/Release/net8.0/PremierAPI.dll`; não use `dotnet run`, pois ele pode carregar
`launchSettings.json` e iniciar o ASP.NET em `Development`. Depois de mudanças
na aplicação, use `restart-completo.sh`; o reinício simples reutiliza o último
build `Release` validado. Preserve `systemd/premierapi.service` como fonte da
unit instalada e não adicione variáveis ou segredos inline.

## Arquitetura que deve ser preservada

- Backend ASP.NET Core/.NET 8, API stateless com JWT, PostgreSQL e Dapper. Não introduza Entity Framework.
- Use aliases explícitos (`AS NomePropriedade`) ao mapear `snake_case` do PostgreSQL para PascalCase do C#.
- Alterações idempotentes de schema pertencem a `Services/DatabaseInitializer.cs`. Nunca recrie ou apague automaticamente o banco para corrigir encoding; migração para UTF-8 exige dump/restore planejado.
- Frontend em HTML estático e Vanilla JavaScript. NPM é permitido somente para os builds fixados do Tailwind, dos assets administrativos com esbuild e das cópias públicas com hash de conteúdo; não introduza bundler em runtime, React ou Vue. Node.js 18 também é usado nas checagens de sintaxe.
- As páginas públicas usam Tailwind 3.4 compilado em `wwwroot/css/tailwind.css`; não reintroduza o Play CDN. O admin fica em `wwwroot/admin/`, usa CSS nativo/Vanilla JS compartilhado e não deve receber Tailwind sem autorização. `wwwroot/admin.html` é só redirecionamento compatível.
- Preserve cores, componentes e linguagem visual existentes. Tabelas responsivas viram cartões sem ocultar dados. Ordenação usa cabeçalho clicável com uma única seta na coluna ativa.

## Regras comerciais e pedidos

- `Services/PricingRules.cs` é a única fonte para limites, preços, descontos e arredondamentos. Frontends e controladores consomem `GET /api/checkout/pricing-rules` e `POST /api/checkout/pricing-quote`; não espalhe constantes comerciais.
- Pedido criado no admin usa `created_manually=true`, começa `pendente` e não é automaticamente `paid_manually`. O cliente pode gerar PIX no próprio pedido; o QR expirado pode ser renovado. Marcar como pago é uma ação administrativa separada.
- No modal de pedido manual, **Dias (Duração)** aparece somente para `diaria`. `personalizado` recebe as datas inicial e final, transforma a diferença em `orders.days` e mantém computadores, slots e valor sob preenchimento manual. As datas não são persistidas separadamente e não mudam a regra global de vencimento baseada em `created_at + days`; a geração posterior do PIX deve aceitar esse período sem aplicar cotação tabelada.
- **Valor total** do pedido manual usa máscara brasileira para todos os planos: vírgula como separador decimal, nenhum ponto e duas casas ao concluir a edição. Converta a vírgula antes de serializar o número e preserve a validação positiva no backend.
- Preserve a validação do servidor WYD no backend e a regra de apenas um pedido pendente por cliente.
- `POST /api/checkout/gerarpix` deve serializar tentativas concorrentes do mesmo usuário, verificar o pedido pendente sob o mesmo bloqueio transacional e responder `409 Conflict` antes de chamar o Asaas quando já houver pendência. A trava imediata e o acionador único do frontend são proteções de experiência, nunca substitutos dessa guarda autoritativa.

## Invariantes do Asaas

- O webhook público canônico é `https://webhook-website.phost.pro/api/webhook/asaas`. Mantenha `webhook-website.phost.pro` nas allowlists de host de `appsettings.json` e `Program.cs`; sem isso, a requisição é rejeitada antes do `WebhookController`.
- O checkout usa `/v3/pix/qrCodes/static`: QR individual, valor fixo e validade curta, sem exigir CPF/CNPJ. Não reverta para cobrança dinâmica sem autorização expressa.
- Preserve a descrição histórica do QR: `Licença ({periodo}) - AnyDesk: {id}`.
- A identidade confiável é `payment.pixQrCodeId` ligado a `orders.asaas_pix_qr_code_id`. `description.StartsWith("Licença")` é apenas compatibilidade com cobranças dinâmicas antigas.
- Não tente reescrever a descrição após `PAYMENT_RECEIVED`. Não faça cobranças, reembolsos ou alterações reais em clientes Asaas durante testes sem autorização.

## Active Directory

- `Services/ActiveDirectoryService.cs` é a única fronteira LDAP e deve usar LDAPS. Buscas partindo do `BaseDn` usam escopo de subárvore.
- Cadastro no site é somente local. Criação automática de usuário AD ocorre exclusivamente quando um pedido passa para `pago`, por meio de `AdAccountProvisioningService`; falhas são conciliadas pelo worker e reportadas via logging/Telegram.
- Pedido pago de cadastro já vinculado por `ad_username` reutiliza a conta existente; nunca crie uma segunda conta AD nesse caso.
- A senha inicial AD é a mesma senha vigente do cadastro local. Como BCrypt não é reversível, a senha em texto claro existe somente durante a requisição e fica transitoriamente protegida pelo ASP.NET Data Protection em `pending_ad_credentials` até a criação no AD; nunca registre ou envie essa senha por e-mail. Após vincular o usuário AD, apague obrigatoriamente a credencial transitória. Login e redefinições anteriores ao provisionamento devem atualizá-la.
- Depois do provisionamento, o e-mail de acesso informa o usuário do computador sem revelar senha, orienta o uso da senha do site, apresenta `https://acesso.phost.pro` como acesso posterior à entrega em computador, celular ou tablet com e-mail/senha do cadastro e avisa sobre o contato para configuração via AnyDesk.
- A data comercial de vencimento é `orders.created_at::date + orders.days`. O `accountExpires` do AD deve expirar à meia-noite imediatamente posterior a essa data. Somente às 01:00, no fuso configurado, a automação desativa a conta e a move para a OU de inativos definida em `ActiveDirectory:ExpiredUsersOu`, exceto se houver outro pedido pago ativo.
- Renomear CN exige `ModifyDNRequest`; alteração comum de atributos usa `LdapModification`.
- O vínculo local pode localizar usuários nas pastas ativos, expirados e website.
- Excluir um usuário diretamente pela tela do AD deve, após a remoção LDAP, limpar `users.ad_username` de todos os cadastros locais vinculados, com comparação case-insensitive. Se essa reconciliação falhar, registre `LogError` e não devolva sucesso completo.
- O admin cria grupos globais de segurança e objetos de computador. Valide nomes/atributos no backend. Criar o objeto não ingressa a máquina física no domínio.
- Na edição de usuário AD, `mail` vazio pode usar o e-mail do cadastro local vinculado como valor inicial, identificado por **E-mail (será atualizado)**; a gravação no AD só ocorre ao salvar. Vencimento **Nunca** oculta o campo de data, e ações ficam visíveis diretamente somente quando a largura disponível comporta todos os botões.
- Computadores expõem e gerenciam associações diretas com os grupos da OU configurada. A seleção manual de grupo durante o vínculo de acesso deve incluir o objeto do computador no grupo, permitindo reutilizar a associação nas próximas operações.
- Em **Gerenciar acessos**, computadores já selecionados permanecem no topo; a ordenação escolhida por nome/descrição ou status é aplicada dentro dos grupos selecionado e não selecionado.
- A convenção automática é descrição de computador `SRV01_01` para grupo `ACESSO_SRV01-01`: sublinhado na descrição e hífen no grupo. Reconcilie somente computadores que correspondam integralmente a esse padrão; ignore os demais.
- Não crie, mova, habilite ou exclua objetos reais do AD apenas para testar sem autorização expressa.

## Segurança, SEO e telemetria

- Não exponha segredos de `appsettings`, ambiente, tokens, chaves ou payloads sensíveis.
- Sessões de cliente, confirmação de e-mail e recuperação de senha armazenam
  somente SHA-256 hexadecimal. Gere tokens brutos com no mínimo 32 bytes de
  `RandomNumberGenerator`, Base64 URL-safe sem padding; nunca use GUID, BCrypt
  ou criptografia reversível para esses segredos. O contrato temporário do
  cliente permanece `localStorage` + `X-Session-Token`, com sete dias e
  múltiplas sessões.
- Validação e logout de cliente calculam o hash antes do SQL e passam
  exclusivamente por `ClientSessionService`, sempre verificando expiração e
  `users.is_active`. Reset por link revoga todas as sessões; troca autenticada
  revoga as demais e rotaciona a atual na mesma transação PostgreSQL.
- Confirmação usa exclusivamente hashes em `email_confirmation_tokens` e pode
  manter vários links válidos. Prepare hash/claim em transação curta, libere
  conexão e locks antes do SMTP e marque sucesso ou falha sanitizada em nova
  transação. Falha/timeout não apaga o token nem consome a cota; confirmar
  qualquer link invalida todos os demais.
- `AdminToken` é somente o primeiro fator administrativo: nunca o devolva em API, cookie, HTML ou `localStorage`. O admin usa sessão aleatória curta em cookie `HttpOnly`/`Secure`/`SameSite=Strict`, CSRF nas mutações e TOTP obrigatório.
- O segredo TOTP fica exclusivamente no arquivo Data Protection definido por `AdminSecurity:TotpSecretPath` (atualmente `/var/lib/premierapi/admin-totp.protected`). Preserve modo `0600`, nunca registre chave, códigos ou URI `otpauth`, e inclua esse arquivo no mesmo conjunto de backup do key ring/certificado. Redefinição exige autorização e janela operacional.
- Preserve o key ring persistente do ASP.NET Data Protection e sua proteção por certificado em `DataProtectionConfiguration`; não volte a persistir chaves XML sem encryptor nem registre materiais criptográficos.
- A telemetria de produto continua first-party, allowlisted e independente do consentimento de marketing. A integração Meta Pixel/CAPI é opcional e só funciona após aceite específico: token permanece no backend, Pixel é carregado dinamicamente, eventos híbridos usam o mesmo `event_id` e o webhook usa a atribuição preservada no pedido. Nunca envie AnyDesk, servidor WYD, senha, conteúdo Pix, conversa ou mensagem de WhatsApp.
- Teste grátis exige sessão autenticada e registro único por usuário. O fluxo deve preservar a trilha `solicitado -> liberado -> utilizado` ou os encerramentos explícitos, impedir nova solicitação depois de utilizado enquanto o registro existir e nunca criar pedido, Pix, cliente Asaas, conta AD ou chamada à Evolution API.
- Usuário que possui ou já possuiu pedido pago não é elegível ao teste grátis. Considere tanto `orders.status = 'pago'` quanto `orders.canceled_was_paid = true`; o bloqueio deve existir na consulta, solicitação e liberação administrativa, além da ocultação visual. Link direto deve informar a inelegibilidade sem revelar dados do pedido.
- Em **Admin > Testes grátis**, a liberação manual só pode criar uma solicitação para usuário elegível que ainda apareça como `nao_solicitado`; o registro nasce como `liberado`, com data atual, `released_by` e evento de auditoria do tipo `liberado` atribuído ao Admin.
- Uma solicitação `recusado` não pode ser refeita nem reaberta pelo usuário: o painel deve ocultar/desabilitar a ação e a API deve manter o bloqueio. A exclusão administrativa exige confirmação, só é permitida nos estados `recusado` e `utilizado`, remove os eventos vinculados por cascata e faz o usuário voltar a `nao_solicitado`; nenhum outro estado pode ser excluído.
- Metadados técnicos do cadastro (IP, User-Agent, idioma, país aproximado, origem e canal) servem somente à segurança, validação operacional e trilha persistente de atividade da própria conta. Não os envie à telemetria anônima de produto nem os exponha fora das APIs administrativas autenticadas.
- Após um login válido, complete somente metadados técnicos de cadastro ainda nulos e marque o canal como `login_recovery` quando a origem original for desconhecida. Nunca sobrescreva valores previamente coletados e nunca bloqueie o login por falha nessa atualização auxiliar.
- A trilha persistente de atividade do usuário admite somente `cadastro`, `login` e `logout` explícito. Pode guardar metadados técnicos e identificação já disponíveis no cadastro, mas nunca senha, token de sessão ou credenciais. A exclusão do usuário deve remover esses eventos em cascata; não invente logout para fechamento de aba ou simples expiração.
- `phost.pro` é o host público canônico. `www.phost.pro` deve redirecionar permanentemente para `phost.pro`, preservando path e query; páginas públicas nunca declaram canonical com `www`. Os favicons oficiais são `/favicon.ico` e `/favicon-96x96.png`; não mova nem renomeie esses arquivos sem revisar todas as páginas públicas, o Google Search Console e as regras de bots da Cloudflare.
- Mantenha no sitemap apenas `/`, `/painel`, `/privacidade` e `/guia-wyd`. Rotas internas devem continuar fora do índice. Ao mudar CSP ou Cloudflare, preserve `robots.txt`, `sitemap.xml` e recursos públicos necessários. Não amplie essa lista sem nova autorização.
- HTML, fontes editáveis de CSS/JavaScript e demais arquivos mutáveis da aplicação mantêm `Cache-Control: no-store` no navegador; mídia pode continuar cacheável. Os assets gerados `/assets/build/public.<nome>.<hash>.css/js` e `/admin/assets/build/admin.<hash>.min.css/js` recebem cache imutável de um ano. Somente `/`, `/painel`, `/privacidade` e `/guia-wyd` admitem microcache de 60 segundos exclusivamente na Cloudflare, com `stale-while-revalidate=30`; outros intermediários continuam em `no-store`. Não amplie essa allowlist sem nova autorização.
- O HTML da allowlist deve ser idêntico para todos os visitantes, sem dados de sessão, personalização ou `Set-Cookie`. Se `/painel` passar a renderizar dados do usuário no servidor, remova-o do microcache antes de publicar. `/confirmar` e `/recuperar-senha` permanecem sempre fora da allowlist.
- Nunca altere manualmente um asset gerado nem reutilize o mesmo hash para outro conteúdo. Ao mudar a política de limpeza, preserve ao menos a geração pública anterior durante a janela do microcache para que HTML antigo no edge não encontre 404.
- Todas as respostas `/api`, em especial login administrativo, chave TOTP e códigos de recuperação, também devem manter `no-store` no navegador e na CDN.
- A origem aceita porta 5000 somente do loopback e do proxy exato em `ReverseProxy:KnownProxy`. Preserve a regra correspondente do nftables e não confie em toda a rede `172.31.2.0/24`. Use `CF-Connecting-IP` somente depois de validar esse proxy.
- Preserve HSTS por aplicação sem `includeSubDomains` e sem `preload`; outros subdomínios da zona hospedam APIs independentes. Aumentar o escopo exige inventário e autorização.
- Preserve a CSP sem `'unsafe-inline'`, com `script-src-attr 'none'`, `style-src-attr 'none'`, `object-src 'none'`, `base-uri 'none'`, `form-action 'self'` e `frame-ancestors 'none'`. Não reintroduza scripts, estilos ou eventos inline; novas ações devem usar os registros declarativos externos. Execute `node tools/check-csp.mjs` e valide as 16 páginas com `node tools/check-csp-browser.mjs` no Chromium ao mudar frontend ou CSP.
- O runbook `docs/csp-tailwind-rollout.md` é a referência para testes manuais, implantação, monitoramento e rollback. Teste automatizado em loopback não comprova fluxos autenticados nem autoriza efeitos reais em Asaas, AD, e-mail, WhatsApp, banco ou serviço.
- Todas as páginas administrativas devem declarar estaticamente o mesmo shell, incluindo logo, link **Testes grátis** e logout. Enquanto a validação inicial de `/api/admin/session` responde, preserve o gate neutro. Depois disso, links internos usam rotas canônicas sem `.html` e substituem somente `#main > .content`, sem nova consulta de sessão ou recarga do shell; as APIs continuam validando a sessão no backend.
- No desktop, o menu lateral pode ser recolhido para 72 px e deve preservar os ícones, títulos acessíveis e a preferência local do operador. No mobile, esse recolhimento não substitui a gaveta; enquanto a gaveta estiver aberta, as ações da página devem permanecer ocultas e sem interação para não aparecerem sobre o menu.
- Nas tabelas de **Active Directory** e **Usuários**, mostre os botões de ação diretamente apenas quando a tabela couber no espaço disponível; ao detectar estouro, substitua-os pelo menu **Mais ações**. Recalcule no carregamento, no redimensionamento e ao recolher ou expandir o menu lateral.
- A manutenção disparada pelo Admin admite somente as operações fixas `publish` e `restart`, exige autenticação e confirmação, impede jobs concorrentes e deve executar fora do cgroup de `premierapi` para sobreviver ao reinício. `publish` executa `npm run assets:build` e build .NET `Release` sem restore, sem reiniciar se qualquer compilação falhar. Nunca aceite comandos, argumentos ou caminhos arbitrários vindos do frontend e nunca use `journalctl -f` dentro da requisição.
- Todos os segredos de `premierapi`, inclusive `Telegram__BotToken` e `Telegram__ChatId`, devem permanecer centralizados exclusivamente em `/etc/premierapi/premierapi.env`, externo ao repositório, pertencente a `root` e com modo `0600`. Tanto o drop-in de `premierapi` quanto `premierapi-startup-alert.service` devem referenciar somente esse arquivo e nunca conter valores em `Environment=`. Nunca imprima ou compartilhe `systemctl show premierapi -p Environment`; diagnósticos devem limitar as propriedades consultadas e usar `--validate-configuration`, que informa somente nomes de chaves inválidas. Migração e rotação exigem autorização e janela operacional.
- Toda exceção capturada e toda falha que impeça o efeito esperado devem usar `ILogger.LogError` ou `LogCritical`, incluindo o objeto `Exception` quando disponível, para que o provider encaminhe o evento ao Telegram. Nunca engula a exceção nem use apenas `Information`/`Warning` quando a operação não fez o que deveria; `Warning` fica reservado a condições recuperáveis cujo efeito principal foi concluído ou possui reconciliação automática explícita. Preserve a sanitização de segredos. `Telegram:MinimumLevel` deve ser no máximo `Error` (`Warning` é o padrão recomendado); `Critical` e `None` são inválidos porque ocultam erros comuns.
- E-mail não confirmado recebe no máximo dois reenvios automáticos: dia seguinte às 11:00 e outro dia às 19:00. Reenvio manual do admin não consome essa cota; confirmação manual continua explícita pelo checkbox.
- Se já houve envio de confirmação no dia atual, o reenvio manual exige confirmação explícita no admin; o backend deve preservar essa guarda, permitindo a continuação somente quando informada pelo operador.
- Confirmação de e-mail pelo link ou pelo checkbox administrativo invalida o token, cancela lembretes pendentes e usa a mesma notificação de sucesso ao cliente.
- A seleção manual de grupo de acesso para um computador sem grupo sugerido deve usar `Warning` e ser enviada ao Telegram. Falhas LDAP não podem ser descartadas silenciosamente: operações recuperáveis usam `Warning` e falhas que impedem a ação usam `Error`.

## Validação mínima

Antes de concluir alterações de código, execute:

```bash
npm run assets:build
for file in $(rg --files wwwroot tools -g '*.js' -g '*.mjs'); do node --check "$file"; done
node tools/check-csp.mjs
dotnet build -c Release --no-restore
git diff --check
```

Para alterações de CSP/frontend, inicie `chromedriver --port=9515 --allowed-ips=127.0.0.1` separadamente e execute também `node tools/check-csp-browser.mjs`. O teste usa somente loopback e fixtures sem efeitos externos.

Não existe suíte automatizada completa no momento. Testes externos com efeitos reais exigem pedido explícito do proprietário.
