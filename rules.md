# Regras de IA e contexto do repositório

Leitura obrigatória, nesta ordem, antes de alterar o projeto: `README.md`, este `rules.md` e `.agents/AGENTS.md`. Eles registram decisões de produto e invariantes que não devem ser redescobertos a cada chat.

## Arquitetura que deve ser preservada

- Backend ASP.NET Core/.NET 8, API stateless com JWT, PostgreSQL e Dapper. Não introduza Entity Framework.
- Use aliases explícitos (`AS NomePropriedade`) ao mapear `snake_case` do PostgreSQL para PascalCase do C#.
- Alterações idempotentes de schema pertencem a `Services/DatabaseInitializer.cs`. Nunca recrie ou apague automaticamente o banco para corrigir encoding; migração para UTF-8 exige dump/restore planejado.
- Frontend em HTML estático e Vanilla JavaScript. Não introduza NPM, bundler, React ou Vue. Node.js 18 existe apenas para checagem de sintaxe.
- `index.html` e `painel.html` usam Tailwind via CDN. O admin fica em `wwwroot/admin/`, usa CSS nativo/Vanilla JS compartilhado e não deve receber Tailwind sem autorização. `wwwroot/admin.html` é só redirecionamento compatível.
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
- A senha inicial AD é a mesma senha vigente do cadastro local. Como BCrypt não é reversível, a senha em texto claro existe somente durante a requisição e fica transitoriamente protegida pelo ASP.NET Data Protection em `pending_ad_credentials` até a criação no AD; nunca registre ou envie essa senha por e-mail. Após vincular o usuário AD, apague obrigatoriamente a credencial transitória. Login e redefinições anteriores ao provisionamento devem atualizá-la.
- A data comercial de vencimento é `orders.created_at::date + orders.days`. O `accountExpires` do AD deve expirar à meia-noite imediatamente posterior a essa data. Somente às 01:00, no fuso configurado, a automação desativa a conta e a move para a OU de inativos definida em `ActiveDirectory:ExpiredUsersOu`, exceto se houver outro pedido pago ativo.
- Renomear CN exige `ModifyDNRequest`; alteração comum de atributos usa `LdapModification`.
- O vínculo local pode localizar usuários nas pastas ativos, expirados e website.
- O admin cria grupos globais de segurança e objetos de computador. Valide nomes/atributos no backend. Criar o objeto não ingressa a máquina física no domínio.
- Não crie, mova, habilite ou exclua objetos reais do AD apenas para testar sem autorização expressa.

## Segurança, SEO e telemetria

- Não exponha segredos de `appsettings`, ambiente, tokens, chaves ou payloads sensíveis.
- A telemetria é first-party e allowlisted. Nunca envie e-mail, WhatsApp, AnyDesk, senha, conteúdo Pix ou dados bancários. Não há GA4 nem Meta Pixel atualmente.
- Mantenha no sitemap apenas `/`, `/painel` e `/privacidade`. Rotas internas devem continuar fora do índice. Ao mudar CSP ou Cloudflare, preserve `robots.txt`, `sitemap.xml` e recursos públicos necessários.
- E-mail não confirmado recebe no máximo dois reenvios automáticos: dia seguinte às 11:00 e outro dia às 19:00. Reenvio manual do admin não consome essa cota; confirmação manual continua explícita pelo checkbox.

## Validação mínima

Antes de concluir alterações de código, execute:

```bash
dotnet build --no-restore
for file in $(rg --files wwwroot -g '*.js'); do node --check "$file"; done
node -e 'const fs=require("fs"),vm=require("vm");for(const file of process.argv.slice(1)){const html=fs.readFileSync(file,"utf8");const re=/<script(?![^>]*\bsrc=)[^>]*>([\s\S]*?)<\/script>/gi;let m,i=0;while((m=re.exec(html))){i++;new vm.Script(m[1],{filename:file+"#inline-"+i});}}' $(rg --files wwwroot -g '*.html')
git diff --check
```

Não existe suíte automatizada no momento. Testes externos com efeitos reais exigem pedido explícito do proprietário.
