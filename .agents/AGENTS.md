# PremierAPI — início rápido para agentes

Leia integralmente `README.md` e `rules.md` antes de investigar ou editar. Este arquivo é um índice curto; em caso de dúvida, as decisões detalhadas estão no README.

## Mapa rápido

- `Controllers/`: APIs administrativas, autenticação, checkout, perfil, webhook e analytics.
- `Services/PricingRules.cs`: autoridade única de preços, limites, descontos e arredondamentos.
- `Services/ActiveDirectoryService.cs`: única fronteira LDAP/LDAPS.
- `Services/DatabaseInitializer.cs`: evolução idempotente do schema PostgreSQL.
- `wwwroot/index.html` e `wwwroot/painel.html`: cliente em HTML/Vanilla JS com Tailwind 3.4 compilado localmente.
- `wwwroot/admin/`: telas administrativas separadas, CSS nativo e JS compartilhado.

## Não redescubra nem reverta

- NPM existe somente para os builds fixados do Tailwind, dos assets do admin com esbuild e das cópias públicas com hash de conteúdo; não introduza framework frontend nem bundler em runtime. Node 18 também é ferramenta de validação.
- QR Pix é estático para evitar CPF/CNPJ. Concilie compras atuais por `pixQrCodeId`; mantenha o fallback legado pela descrição `Licença`.
- Pedido administrativo nasce pendente com `created_manually`, não pago. O cliente pode gerar ou renovar o QR depois.
- Regras comerciais não podem ser copiadas para JS/controladores: use `PricingRules` e os endpoints de regras/cotação.
- Ordenação das tabelas mostra somente uma seta na coluna ativa; mobile deve preservar todas as informações em cartões.
- Usuário local pode ser vinculado às OUs/pastas de ativos, expirados e website.
- Cadastro público nunca cria usuário AD. O provisionamento e vínculo acontecem somente após pedido pago; falhas ficam para reconciliação automática e são reportadas pelo logger/Telegram.
- Teste grátis exige sessão local, possui um registro por usuário e trilha de eventos, e nunca aciona pedidos, Pix, Asaas, AD ou Evolution API. Metadados técnicos de cadastro ficam restritos ao admin.
- Se `ad_username` já existe, o provisionamento pago reutiliza a conta vinculada e não cria outra.
- A conta AD usa a mesma senha do site. Até o primeiro pagamento, ela fica reversivelmente protegida em `pending_ad_credentials`; apague o registro logo após criar e vincular a conta AD. Nunca envie ou registre a senha.
- O vencimento comercial inclui todo o dia exibido: `accountExpires` vence à meia-noite seguinte; às 01:00 a conta sem outra licença ativa é desativada e movida para a OU configurada de inativos.
- Confirmação de e-mail admite no máximo dois lembretes automáticos em dias distintos (11:00 e 19:00); o admin pode reenviar à parte ou confirmar manualmente.
- Reenvio manual no mesmo dia exige confirmação explícita do operador, com a guarda aplicada também no backend.
- Confirmação pelo link ou pelo admin invalida o token, cancela lembretes e envia a mesma notificação de sucesso ao cliente.
- A seleção manual de grupo para computador sem sugestão e todas as falhas LDAP recuperáveis usam ao menos `Warning`, acionando Telegram; falhas que impedem a ação usam `Error`. O admin possui uma tela de Logs em memória, sanitizada, para a execução atual.
- Criar computador no AD cria somente o objeto; não ingressa a máquina no domínio.
- A aba Computadores mostra e gerencia grupos diretos. Ao selecionar manualmente um grupo durante o vínculo de acesso, associe também o objeto do computador ao grupo para persistir a escolha.
- A descrição convencional do computador `SRV01_01` corresponde ao grupo `ACESSO_SRV01-01`; somente computadores nesse padrão são reconciliados automaticamente com o grupo.
- Analytics é first-party e não guarda PII. Não há Google Analytics nem Meta Pixel.
- Indexação pública: somente `/`, `/painel` e `/privacidade`; preserve `robots.txt` e `sitemap.xml`.
- Arquivos mutáveis da aplicação mantêm `no-store` no navegador. Assets públicos e administrativos gerados com hash usam cache imutável de um ano. Somente `/`, `/painel` e `/privacidade` admitem microcache de 60 segundos na Cloudflare; APIs e demais rotas continuam `no-store`. Preserve os hashes e não amplie a allowlist por Cache Rule.
- A origem aceita a porta 5000 somente pelo loopback e pelo proxy exato configurado. Preserve a regra do nftables, valide o proxy antes de aceitar `CF-Connecting-IP` e mantenha o HSTS sem `includeSubDomains` e sem `preload`.
- `AdminToken` é apenas o primeiro fator do admin. O navegador recebe uma sessão aleatória curta em cookie `HttpOnly`/`Secure`/`SameSite=Strict`, com CSRF nas mutações, e o login exige TOTP.
- Tailwind 3.4, Inter e Chart.js são locais; nunca reintroduza seus CDNs. A CSP não aceita `'unsafe-inline'`: não crie scripts, estilos, atributos `on*` ou `style` inline. Consulte `docs/csp-tailwind-rollout.md` para testes, implantação e rollback.
- O shell do admin é uniforme e estático: logo, menu completo e logout existem em todas as telas. Preserve o gate neutro na validação inicial e a navegação interna que troca somente `.content`; ela não deve repetir `/api/admin/session` nem recarregar o shell.

## Segurança operacional

Não faça chamadas com efeitos reais no Asaas nem mutações no AD para testar sem autorização expressa. Não exponha segredos, tokens ou configurações privadas em logs, diffs ou respostas.
`premierapi` e `premierapi-startup-alert.service` carregam somente `/etc/premierapi/premierapi.env`, onde ficam centralizados todos os segredos, inclusive `Telegram__BotToken` e `Telegram__ChatId`. O arquivo pertence a `root`, mantém modo `0600` e nunca deve ter seus valores exibidos. Como o alerta externo usa o mesmo arquivo da aplicação, ele também falhará se `premierapi.env` estiver ausente ou tiver sintaxe inválida.
O key ring do Data Protection é persistente e protegido por certificado; preserve `DataProtectionConfiguration` e nunca versione `.data-protection-keys`.
O estado TOTP protegido fica em `/var/lib/premierapi/admin-totp.protected`, com arquivo `0600`. Nunca exponha chave, URI `otpauth` ou códigos de recuperação; faça backup dele junto do key ring e do certificado, pois o arquivo isolado não é recuperável.

## Checagens rápidas

```bash
npm run assets:build
for file in $(rg --files wwwroot tools -g '*.js' -g '*.mjs'); do node --check "$file"; done
node tools/check-csp.mjs
dotnet build -c Release --no-restore
git diff --check
```

Mudanças de frontend/CSP também exigem o teste Chromium descrito no runbook. O teste local não autoriza nem substitui validação planejada de efeitos reais.
