# Runbook de implantação — Tailwind local e CSP estrita

Este documento registra o escopo, as evidências de validação, os testes ainda
necessários e o procedimento de implantação/rollback das alterações feitas na
branch `security/strict-csp-validation`.

## Escopo e separação dos commits

| Commit | Escopo | Pode ser avaliado isoladamente? |
| --- | --- | --- |
| `52fe549` | Compila Tailwind 3.4.19 localmente, remove o Play CDN e integra o build CSS à manutenção | Sim. A CSP anterior ainda aceita a aplicação enquanto o visual é validado |
| `9648368` | Remove código/estilo inline, ativa CSP estrita e adiciona verificadores estático e de navegador | Sim, desde que o commit do Tailwind local já esteja presente |

O segundo commit depende do primeiro. A CSP não permite reintroduzir o Play CDN
nem código inline, portanto a ordem de integração e implantação deve ser
Tailwind local primeiro e CSP estrita depois.

Não há migration, alteração de banco, rotação de segredo ou mudança de unit
systemd nesses dois commits. Chromium, ChromeDriver, Node.js e npm instalados no
host são ferramentas de build/teste e não fazem parte do processo da API em
runtime.

## O que mudou

### Tailwind

- Tailwind `3.4.19` está fixado em `package-lock.json`.
- `assets/tailwind.css` é a entrada e `wwwroot/css/tailwind.css` é o artefato
  versionado servido pela aplicação.
- `npm run css:build` recompila o CSS minificado; `npm run css:watch` atende ao
  desenvolvimento.
- O scanner considera `wwwroot/**/*.html` e `wwwroot/**/*.js`.
- As cinco páginas públicas deixaram de carregar `cdn.tailwindcss.com`.
- A manutenção de publicação recompila o CSS antes do build .NET e aborta se
  essa etapa falhar.

O Play CDN não deve voltar a ser usado em produção. Uma atualização para
Tailwind 4 é uma mudança maior e deve ficar em branch/commit próprios, com nova
comparação visual e revisão de compatibilidade de navegadores.

### CSP

- `script-src` e `style-src` não contêm `'unsafe-inline'`.
- `script-src-attr 'none'` e `style-src-attr 'none'` impedem eventos e estilos
  inline, inclusive quando inseridos dinamicamente.
- Scripts e estilos das páginas públicas foram movidos para arquivos externos
  da própria origem.
- Eventos estáticos usam `data-csp-*` e o registro delegado
  `wwwroot/js/csp-handlers.js`.
- Componentes gerados pelo admin usam `data-admin-*` e o registro delegado de
  `wwwroot/admin/assets/admin.js`.
- Permanecem permitidos apenas os provedores necessários: Cloudflare Turnstile,
  Chart.js no jsDelivr e Google Fonts.
- `object-src 'none'`, `base-uri 'none'`, `form-action 'self'` e
  `frame-ancestors 'none'` continuam como barreiras adicionais.

`tools/check-csp-browser.mjs` mantém uma cópia da política aplicada em
`Program.cs`. Qualquer mudança futura na CSP deve atualizar os dois locais no
mesmo commit, para que o teste represente a política real.

## Evidências já obtidas

Na branch foram executados:

- `npm run css:build` com sucesso;
- `npm audit` com zero vulnerabilidades conhecidas;
- build .NET 8 em Release com zero warnings e zero erros;
- verificação de sintaxe de todos os `.js` e `.mjs`;
- `node tools/check-csp.mjs`, com zero scripts, estilos, eventos ou templates
  dinâmicos incompatíveis e zero ações declarativas ausentes;
- teste das 15 páginas completas no Chromium, com a política exata de
  `Program.cs`: zero violações CSP e zero exceções JavaScript;
- abertura/fechamento dos modais principais e troca das telas de autenticação;
- comparação visual da landing em desktop e mobile após a migração do Tailwind.

O teste de navegador usa servidor somente em `127.0.0.1` e fixtures sem efeitos.
Ele comprova carregamento, integridade básica dos handlers e compatibilidade com
a CSP, mas não substitui os fluxos autenticados nem integrações reais.

## Validação automatizada antes de integrar

Execute no host de runtime, dentro do repositório:

```bash
npm ci
npm run css:build
npm audit
for file in $(rg --files wwwroot tools -g '*.js' -g '*.mjs'); do node --check "$file"; done
node tools/check-csp.mjs
dotnet build -c Release --no-restore
git diff --check
```

Depois do build CSS, revise o diff de `wwwroot/css/tailwind.css`. Um arquivo
alterado é esperado quando classes foram adicionadas ou removidas; uma alteração
inesperada precisa ser entendida antes do commit.

Para o teste de navegador, inicie o driver em um terminal:

```bash
chromedriver --port=9515 --allowed-ips=127.0.0.1
```

E execute em outro terminal:

```bash
node tools/check-csp-browser.mjs
```

O resultado esperado é:

```text
CSP_BROWSER_PAGES=15
CSP_BROWSER_VIOLATIONS=0
CSP_BROWSER_RUNTIME_ERRORS=0
CSP_BROWSER_INTERACTION_FAILURES=0
```

Chromium e ChromeDriver devem possuir versões compatíveis. O servidor temporário
do teste e o driver devem escutar somente em loopback e ser encerrados depois.

## Testes manuais necessários

### Sem autenticação e sem efeitos externos

- Landing: navegação, menu mobile, vídeo, WhatsApp, abertura/fechamento do modal
  e troca entre login, cadastro e recuperação.
- Cadastro: avançar e voltar nas seis etapas, validações locais e renderização
  do Turnstile. Não concluir com um cadastro real se esse efeito não estiver no
  escopo da janela.
- Painel público: alternar diária/semanal/mensal, computadores, slots e dias;
  conferir valores, responsividade e mensagens de validação.
- Privacidade, confirmação sem token e recuperação sem token: conteúdo,
  navegação e estados de erro esperados.
- Layout em desktop e celular, especialmente menus, modais, sliders, tabelas e
  botões fixos.
- Console do navegador: não pode haver `Refused to ... because it violates the
  following Content Security Policy directive`, `Uncaught TypeError`,
  `ReferenceError` ou falhas 404 de `/css/` e `/js/`.

### Autenticados, mas reversíveis

Use contas de teste e dados que possam ser restaurados:

- Cliente: login/logout, edição de WhatsApp, alteração de senha, indicação,
  aplicação/remoção de cupom, histórico e reabertura de PIX pendente.
- Admin: primeiro fator, Turnstile, TOTP, logout e expiração da sessão.
- Dashboard/Financeiro/CRM: filtros, gráficos Chart.js, paginação e ordenação.
- Pedidos/Usuários/Testes grátis/Active Directory: abrir e fechar todos os
  modais, filtros, menus, tooltips, tabelas responsivas e confirmações, sem
  confirmar a ação destrutiva.
- Código de recuperação TOTP só deve ser testado em janela planejada: cada uso
  consome um código e exige atualizar o inventário guardado pelo proprietário.

### Com efeitos externos ou operacionais

Estes testes não são exigidos apenas para provar a CSP e precisam de autorização
específica, ambiente/dados controlados e critério de limpeza:

- concluir cadastro e confirmação de e-mail;
- redefinir senha de uma conta real;
- criar pedido, gerar ou pagar PIX, cancelar ou reembolsar no Asaas;
- enviar e-mail, WhatsApp ou acionar Evolution API;
- liberar/usar/excluir teste grátis;
- criar, alterar, mover, habilitar ou excluir usuário, grupo ou computador no AD;
- executar publicação/restart pelo painel administrativo.

Ao testar um efeito real, registre previamente a conta/objeto escolhido, o
resultado esperado e como desfazer ou reconciliar o efeito. Não use dados de
clientes como massa de teste.

## Sequência recomendada de implantação

Implantação e reinicialização exigem autorização e janela operacional.

1. Registre o commit atualmente implantado e confirme que o worktree está limpo.
2. Integre primeiro `52fe549` e execute `npm ci`, build CSS, build .NET e a
   comparação visual.
3. Publique/reinicie e faça o smoke test público do Tailwind local.
4. Integre `9648368`, repita os verificadores estático e Chromium e revise a CSP
   final de `Program.cs`.
5. Publique/reinicie novamente e execute os testes pós-implantação abaixo.
6. Integre commits posteriores de documentação sem alterar a ordem histórica
   dos dois commits funcionais.

Se a janela não permitir duas publicações, os commits podem ser integrados e
publicados juntos, mas a separação deve ser preservada no Git para diagnóstico e
rollback seletivo.

## Testes pós-implantação

Confirme o redirecionamento HTTP sem seguir automaticamente a resposta:

```bash
curl -sS -o /dev/null -D - http://phost.pro/
```

O resultado deve redirecionar para `https://phost.pro/`. Em seguida, confira os
cabeçalhos HTTPS em `/`, `/painel`, `/privacidade` e uma rota administrativa:

```bash
curl -sS -o /dev/null -D - https://phost.pro/
curl -sS -o /dev/null -D - https://phost.pro/painel
curl -sS -o /dev/null -D - https://phost.pro/privacidade
curl -sS -o /dev/null -D - https://phost.pro/admin/dashboard.html
```

Verifique:

- `Strict-Transport-Security` com 180 dias, sem `includeSubDomains` e sem
  `preload`;
- CSP sem `'unsafe-inline'`, com `script-src-attr 'none'` e
  `style-src-attr 'none'`;
- `X-Frame-Options: DENY`;
- `X-Content-Type-Options: nosniff`;
- `Referrer-Policy: strict-origin-when-cross-origin`;
- arquivos HTML/CSS/JS e respostas `/api` com `no-store` também na CDN;
- `/css/tailwind.css`, CSS extraídos e scripts de `/js/` respondendo `200`.

HSTS só passa a ser obedecido pelo navegador depois de uma resposta HTTPS. Ele
não substitui o redirecionamento da primeira requisição HTTP. A configuração
atual protege apenas o host que emitiu o cabeçalho; não ampliar para
`includeSubDomains` ou `preload`, pois outros subdomínios hospedam APIs
independentes.

Depois dos cabeçalhos, repita em navegador real os testes manuais sem efeitos e
o login cliente/admin. Confira especialmente Turnstile, Google Fonts, Chart.js,
cupom, indicação, perfil e componentes dinâmicos do admin.

## O que monitorar

Nas primeiras horas após a implantação, observe:

- violações CSP e erros JavaScript no console do navegador;
- respostas 404/403/5xx para os novos arquivos CSS/JS;
- falhas de Turnstile ou gráficos vazios;
- botões que alteram o texto mas deixam de executar a ação seguinte;
- modais, tooltips, paginação, filtros e ordenação do admin;
- diferenças de layout em páginas pouco acessadas ou no celular;
- aumento de erros no log da aplicação, sem imprimir ambiente ou segredos.

Uma evolução útil, em commit separado, é adicionar coleta de relatórios CSP
(`report-to`/`report-uri`) em endpoint limitado e sem dados sensíveis. Outra é
servir Chart.js e fontes localmente, reduzindo a allowlist; o Turnstile continuará
dependendo dos domínios Cloudflare.

## Rollback

Não use `git reset --hard` no servidor. Faça rollback por commit de reversão e
republique com o mesmo processo de build.

- Problema apenas na CSP/handlers: reverta `9648368`. O Tailwind local pode
  permanecer e a política anterior volta a aceitar o frontend.
- Problema no Tailwind local: reverta primeiro a CSP e somente depois `52fe549`.
  Reverter o Tailwind mantendo a CSP estrita reintroduziria um CDN que a política
  não autoriza e pode deixar a interface sem estilos.
- Problema apenas documental: reverta somente o commit de documentação.

Essas alterações não exigem rollback de banco. Antes de reiniciar, valide a
configuração apenas pelos comandos seguros já documentados; nunca exponha o
conteúdo dos arquivos de ambiente ou o estado TOTP.

## Regras para mudanças futuras

- Não adicione `<script>`, `<style>`, atributos `on*` ou `style` inline.
- Registre ações estáticas em `csp-handlers.js` e ações de templates do admin em
  `adminDeclarativeActions`.
- Ao adicionar classe Tailwind, use a classe completa nos arquivos escaneados e
  execute `npm run css:build`; classes montadas por concatenação podem não entrar
  no CSS gerado.
- Ao adicionar um provedor externo, autorize somente a diretiva e o host
  indispensáveis, atualize o teste de navegador e documente a justificativa.
- Atualizações de Tailwind, Chart.js, Browserslist/caniuse ou outras dependências
  devem ficar em commits próprios. O aviso de `caniuse-lite` desatualizado é
  informativo e não justifica uma atualização automática misturada a segurança.
- Preserve o Tailwind local e mantenha o admin sem Tailwind, salvo decisão
  arquitetural explícita.
