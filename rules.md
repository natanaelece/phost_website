# Regras de IA e contexto do repositório

Leitura obrigatória, nesta ordem, antes de alterar o projeto: `README.md`, este `rules.md` e `.agents/AGENTS.md`. Eles registram decisões de produto e invariantes que não devem ser redescobertos a cada chat.

## Arquitetura que deve ser preservada

- Backend ASP.NET Core/.NET 8, API stateless com JWT, PostgreSQL e Dapper. Não introduza Entity Framework.
- Use aliases explícitos (`AS NomePropriedade`) ao mapear `snake_case` do PostgreSQL para PascalCase do C#.
- Alterações idempotentes de schema pertencem a `Services/DatabaseInitializer.cs`. Nunca recrie ou apague automaticamente o banco para corrigir encoding; migração para UTF-8 exige dump/restore planejado.
- Frontend em HTML estático e Vanilla JavaScript. NPM é permitido somente para o build fixado do Tailwind; não introduza bundler, React ou Vue. Node.js 18 também é usado nas checagens de sintaxe.
- As páginas públicas usam Tailwind 3.4 compilado em `wwwroot/css/tailwind.css`; não reintroduza o Play CDN. O admin fica em `wwwroot/admin/`, usa CSS nativo/Vanilla JS compartilhado e não deve receber Tailwind sem autorização. `wwwroot/admin.html` é só redirecionamento compatível.
- Preserve cores, componentes e linguagem visual existentes. Tabelas responsivas viram cartões sem ocultar dados. Ordenação usa cabeçalho clicável com uma única seta na coluna ativa.

## Regras comerciais e pedidos

- `Services/PricingRules.cs` é a única fonte para limites, preços, descontos e arredondamentos. Frontends e controladores consomem `GET /api/checkout/pricing-rules` e `POST /api/checkout/pricing-quote`; não espalhe constantes comerciais.
- Pedido criado no admin usa `created_manually=true`, começa `pendente` e não é automaticamente `paid_manually`. O cliente pode gerar PIX no próprio pedido; o QR expirado pode ser renovado. Marcar como pago é uma ação administrativa separada.
- Preserve a validação do servidor WYD no backend e a regra de apenas um pedido pendente por cliente.

## Invariantes do Asaas

- O checkout usa `/v3/pix/qrCodes/static`: QR individual, valor fixo e validade curta, sem exigir CPF/CNPJ. Não reverta para cobrança dinâmica sem autorização expressa.
- Preserve a descrição histórica do QR: `Licença ({periodo}) - AnyDesk: {id}`.
- A identidade confiável é `payment.pixQrCodeId` ligado a `orders.asaas_pix_qr_code_id`. `description.StartsWith("Licença")` é apenas compatibilidade com cobranças dinâmicas antigas.
- Não tente reescrever a descrição após `PAYMENT_RECEIVED`. Não faça cobranças, reembolsos ou alterações reais em clientes Asaas durante testes sem autorização.

## Active Directory

- `Services/ActiveDirectoryService.cs` é a única fronteira LDAP e deve usar LDAPS. Buscas partindo do `BaseDn` usam escopo de subárvore.
- Cadastro no site é somente local. Criação automática de usuário AD ocorre exclusivamente quando um pedido passa para `pago`, por meio de `AdAccountProvisioningService`; falhas são conciliadas pelo worker e reportadas via logging/Telegram.
- Pedido pago de cadastro já vinculado por `ad_username` reutiliza a conta existente; nunca crie uma segunda conta AD nesse caso.
- A senha inicial AD é a mesma senha vigente do cadastro local. Como BCrypt não é reversível, a senha em texto claro existe somente durante a requisição e fica transitoriamente protegida pelo ASP.NET Data Protection em `pending_ad_credentials` até a criação no AD; nunca registre ou envie essa senha por e-mail. Após vincular o usuário AD, apague obrigatoriamente a credencial transitória. Login e redefinições anteriores ao provisionamento devem atualizá-la.
- A data comercial de vencimento é `orders.created_at::date + orders.days`. O `accountExpires` do AD deve expirar à meia-noite imediatamente posterior a essa data. Somente às 01:00, no fuso configurado, a automação desativa a conta e a move para a OU de inativos definida em `ActiveDirectory:ExpiredUsersOu`, exceto se houver outro pedido pago ativo.
- Renomear CN exige `ModifyDNRequest`; alteração comum de atributos usa `LdapModification`.
- O vínculo local pode localizar usuários nas pastas ativos, expirados e website.
- O admin cria grupos globais de segurança e objetos de computador. Valide nomes/atributos no backend. Criar o objeto não ingressa a máquina física no domínio.
- Computadores expõem e gerenciam associações diretas com os grupos da OU configurada. A seleção manual de grupo durante o vínculo de acesso deve incluir o objeto do computador no grupo, permitindo reutilizar a associação nas próximas operações.
- A convenção automática é descrição de computador `SRV01_01` para grupo `ACESSO_SRV01-01`: sublinhado na descrição e hífen no grupo. Reconcilie somente computadores que correspondam integralmente a esse padrão; ignore os demais.
- Não crie, mova, habilite ou exclua objetos reais do AD apenas para testar sem autorização expressa.

## Segurança, SEO e telemetria

- Não exponha segredos de `appsettings`, ambiente, tokens, chaves ou payloads sensíveis.
- `AdminToken` é somente o primeiro fator administrativo: nunca o devolva em API, cookie, HTML ou `localStorage`. O admin usa sessão aleatória curta em cookie `HttpOnly`/`Secure`/`SameSite=Strict`, CSRF nas mutações e TOTP obrigatório.
- O segredo TOTP fica exclusivamente no arquivo Data Protection definido por `AdminSecurity:TotpSecretPath` (atualmente `/var/lib/premierapi/admin-totp.protected`). Preserve modo `0600`, nunca registre chave, códigos ou URI `otpauth`, e inclua esse arquivo no mesmo conjunto de backup do key ring/certificado. Redefinição exige autorização e janela operacional.
- Preserve o key ring persistente do ASP.NET Data Protection e sua proteção por certificado em `DataProtectionConfiguration`; não volte a persistir chaves XML sem encryptor nem registre materiais criptográficos.
- A telemetria é first-party e allowlisted. Nunca envie e-mail, WhatsApp, AnyDesk, senha, conteúdo Pix ou dados bancários. Não há GA4 nem Meta Pixel atualmente.
- Teste grátis exige sessão autenticada e registro único por usuário. O fluxo deve preservar a trilha `solicitado -> liberado -> utilizado` ou os encerramentos explícitos, impedir nova solicitação depois de utilizado enquanto o registro existir e nunca criar pedido, Pix, cliente Asaas, conta AD ou chamada à Evolution API.
- Usuário que possui ou já possuiu pedido pago não é elegível ao teste grátis. Considere tanto `orders.status = 'pago'` quanto `orders.canceled_was_paid = true`; o bloqueio deve existir na consulta, solicitação e liberação administrativa, além da ocultação visual. Link direto deve informar a inelegibilidade sem revelar dados do pedido.
- Em **Admin > Testes grátis**, a liberação manual só pode criar uma solicitação para usuário elegível que ainda apareça como `nao_solicitado`; o registro nasce como `liberado`, com data atual, `released_by` e evento de auditoria do tipo `liberado` atribuído ao Admin.
- Uma solicitação `recusado` não pode ser refeita nem reaberta pelo usuário: o painel deve ocultar/desabilitar a ação e a API deve manter o bloqueio. A exclusão administrativa exige confirmação, só é permitida nos estados `recusado` e `utilizado`, remove os eventos vinculados por cascata e faz o usuário voltar a `nao_solicitado`; nenhum outro estado pode ser excluído.
- Metadados técnicos do cadastro (IP, User-Agent, idioma, país aproximado, origem e canal) servem somente à segurança, validação operacional e trilha persistente de atividade da própria conta. Não os envie à telemetria anônima de produto nem os exponha fora das APIs administrativas autenticadas.
- Após um login válido, complete somente metadados técnicos de cadastro ainda nulos e marque o canal como `login_recovery` quando a origem original for desconhecida. Nunca sobrescreva valores previamente coletados e nunca bloqueie o login por falha nessa atualização auxiliar.
- A trilha persistente de atividade do usuário admite somente `cadastro`, `login` e `logout` explícito. Pode guardar metadados técnicos e identificação já disponíveis no cadastro, mas nunca senha, token de sessão ou credenciais. A exclusão do usuário deve remover esses eventos em cascata; não invente logout para fechamento de aba ou simples expiração.
- Mantenha no sitemap apenas `/`, `/painel` e `/privacidade`. Rotas internas devem continuar fora do índice. Ao mudar CSP ou Cloudflare, preserve `robots.txt`, `sitemap.xml` e recursos públicos necessários.
- HTML, CSS, JavaScript e demais arquivos que definem a aplicação devem manter `Cache-Control`, `CDN-Cache-Control` e `Cloudflare-CDN-Cache-Control` como `no-store`; mídia pode continuar cacheável. Não crie Cache Rule no edge que sobreponha essa política para arquivos de aplicação.
- Todas as respostas `/api`, em especial login administrativo, chave TOTP e códigos de recuperação, também devem manter `no-store` no navegador e na CDN.
- A origem aceita porta 5000 somente do loopback e do proxy exato em `ReverseProxy:KnownProxy`. Preserve a regra correspondente do nftables e não confie em toda a rede `172.31.2.0/24`. Use `CF-Connecting-IP` somente depois de validar esse proxy.
- Preserve HSTS por aplicação sem `includeSubDomains` e sem `preload`; outros subdomínios da zona hospedam APIs independentes. Aumentar o escopo exige inventário e autorização.
- Preserve a CSP sem `'unsafe-inline'`, com `script-src-attr 'none'`, `style-src-attr 'none'`, `object-src 'none'`, `base-uri 'none'`, `form-action 'self'` e `frame-ancestors 'none'`. Não reintroduza scripts, estilos ou eventos inline; novas ações devem usar os registros declarativos externos. Execute `node tools/check-csp.mjs` e valide as 15 páginas com `node tools/check-csp-browser.mjs` no Chromium ao mudar frontend ou CSP.
- A manutenção disparada pelo Admin admite somente as operações fixas `publish` e `restart`, exige autenticação e confirmação, impede jobs concorrentes e deve executar fora do cgroup de `premierapi` para sobreviver ao reinício. `publish` usa build `Release` sem restore e não reinicia se a compilação falhar. Nunca aceite comandos, argumentos ou caminhos arbitrários vindos do frontend e nunca use `journalctl -f` dentro da requisição.
- Segredos de `premierapi` devem permanecer exclusivamente em `/etc/premierapi/premierapi.env` e `/etc/premierapi/telegram-alerts.env`, externos ao repositório, propriedade de `root` e modo `0600`. O drop-in do systemd nunca pode conter valores em `Environment=`. Nunca imprima ou compartilhe `systemctl show premierapi -p Environment`; diagnósticos devem limitar as propriedades consultadas e usar `--validate-configuration`, que informa somente nomes de chaves inválidas. Migração e rotação exigem autorização e janela operacional.
- E-mail não confirmado recebe no máximo dois reenvios automáticos: dia seguinte às 11:00 e outro dia às 19:00. Reenvio manual do admin não consome essa cota; confirmação manual continua explícita pelo checkbox.
- Se já houve envio de confirmação no dia atual, o reenvio manual exige confirmação explícita no admin; o backend deve preservar essa guarda, permitindo a continuação somente quando informada pelo operador.
- Confirmação de e-mail pelo link ou pelo checkbox administrativo invalida o token, cancela lembretes pendentes e usa a mesma notificação de sucesso ao cliente.
- A seleção manual de grupo de acesso para um computador sem grupo sugerido deve usar `Warning` e ser enviada ao Telegram. Falhas LDAP não podem ser descartadas silenciosamente: operações recuperáveis usam `Warning` e falhas que impedem a ação usam `Error`.

## Validação mínima

Antes de concluir alterações de código, execute:

```bash
npm run css:build
dotnet build --no-restore
for file in $(rg --files wwwroot -g '*.js'); do node --check "$file"; done
node -e 'const fs=require("fs"),vm=require("vm");for(const file of process.argv.slice(1)){const html=fs.readFileSync(file,"utf8");const re=/<script(?![^>]*\bsrc=)[^>]*>([\s\S]*?)<\/script>/gi;let m,i=0;while((m=re.exec(html))){i++;new vm.Script(m[1],{filename:file+"#inline-"+i});}}' $(rg --files wwwroot -g '*.html')
node tools/check-csp.mjs
git diff --check
```

Para alterações de CSP/frontend, inicie `chromedriver --port=9515 --allowed-ips=127.0.0.1` separadamente e execute também `node tools/check-csp-browser.mjs`. O teste usa somente loopback e fixtures sem efeitos externos.

Não existe suíte automatizada completa no momento. Testes externos com efeitos reais exigem pedido explícito do proprietário.
