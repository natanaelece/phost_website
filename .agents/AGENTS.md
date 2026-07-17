# PremierAPI — início rápido para agentes

Leia integralmente `README.md` e `rules.md` antes de investigar ou editar. Este arquivo é um índice curto; em caso de dúvida, as decisões detalhadas estão no README.

## Mapa rápido

- `Controllers/`: APIs administrativas, autenticação, checkout, perfil, webhook e analytics.
- `Services/PricingRules.cs`: autoridade única de preços, limites, descontos e arredondamentos.
- `Services/ActiveDirectoryService.cs`: única fronteira LDAP/LDAPS.
- `Services/DatabaseInitializer.cs`: evolução idempotente do schema PostgreSQL.
- `wwwroot/index.html` e `wwwroot/painel.html`: cliente em HTML/Vanilla JS com Tailwind CDN.
- `wwwroot/admin/`: telas administrativas separadas, CSS nativo e JS compartilhado.

## Não redescubra nem reverta

- Sem NPM ou framework frontend. Node 18 é ferramenta de validação, não dependência do projeto.
- QR Pix é estático para evitar CPF/CNPJ. Concilie compras atuais por `pixQrCodeId`; mantenha o fallback legado pela descrição `Licença`.
- Pedido administrativo nasce pendente com `created_manually`, não pago. O cliente pode gerar ou renovar o QR depois.
- Regras comerciais não podem ser copiadas para JS/controladores: use `PricingRules` e os endpoints de regras/cotação.
- Ordenação das tabelas mostra somente uma seta na coluna ativa; mobile deve preservar todas as informações em cartões.
- Usuário local pode ser vinculado às OUs/pastas de ativos, expirados e website.
- Cadastro público nunca cria usuário AD. O provisionamento e vínculo acontecem somente após pedido pago; falhas ficam para reconciliação automática e são reportadas pelo logger/Telegram.
- A conta AD usa a mesma senha do site. Até o primeiro pagamento, ela fica reversivelmente protegida em `pending_ad_credentials`; apague o registro logo após criar e vincular a conta AD. Nunca envie ou registre a senha.
- O vencimento comercial inclui todo o dia exibido: `accountExpires` vence à meia-noite seguinte; às 01:00 a conta sem outra licença ativa é desativada e movida para a OU configurada de inativos.
- Confirmação de e-mail admite no máximo dois lembretes automáticos em dias distintos (11:00 e 19:00); o admin pode reenviar à parte ou confirmar manualmente.
- Reenvio manual no mesmo dia exige confirmação explícita do operador, com a guarda aplicada também no backend.
- Confirmação pelo link ou pelo admin invalida o token, cancela lembretes e envia a mesma notificação de sucesso ao cliente.
- A seleção manual de grupo para computador sem sugestão e todas as falhas LDAP recuperáveis usam ao menos `Warning`, acionando Telegram; falhas que impedem a ação usam `Error`. O admin possui uma tela de Logs em memória, sanitizada, para a execução atual.
- Criar computador no AD cria somente o objeto; não ingressa a máquina no domínio.
- A aba Computadores mostra e gerencia grupos diretos. Ao selecionar manualmente um grupo durante o vínculo de acesso, associe também o objeto do computador ao grupo para persistir a escolha.
- Analytics é first-party e não guarda PII. Não há Google Analytics nem Meta Pixel.
- Indexação pública: somente `/`, `/painel` e `/privacidade`; preserve `robots.txt` e `sitemap.xml`.

## Segurança operacional

Não faça chamadas com efeitos reais no Asaas nem mutações no AD para testar sem autorização expressa. Não exponha segredos, tokens ou configurações privadas em logs, diffs ou respostas.
O key ring do Data Protection é persistente e protegido por certificado; preserve `DataProtectionConfiguration` e nunca versione `.data-protection-keys`.

## Checagens rápidas

```bash
dotnet build --no-restore
for file in $(rg --files wwwroot -g '*.js'); do node --check "$file"; done
node -e 'const fs=require("fs"),vm=require("vm");for(const file of process.argv.slice(1)){const html=fs.readFileSync(file,"utf8");const re=/<script(?![^>]*\bsrc=)[^>]*>([\s\S]*?)<\/script>/gi;let m,i=0;while((m=re.exec(html))){i++;new vm.Script(m[1],{filename:file+"#inline-"+i});}}' $(rg --files wwwroot -g '*.html')
git diff --check
```
