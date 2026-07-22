# Runbook de implantação — frontend local, CSP estrita e admin otimizado

Este documento registra o escopo, as evidências de validação, os testes ainda
necessários e o procedimento de implantação/rollback das alterações integradas
à branch `development`.

## Escopo e separação dos commits

| Commit | Escopo | Pode ser avaliado isoladamente? |
| --- | --- | --- |
| `52fe549` | Compila Tailwind 3.4.19 localmente, remove o Play CDN e integra o build CSS à manutenção | Sim. A CSP anterior ainda aceita a aplicação enquanto o visual é validado |
| `9648368` | Remove código/estilo inline, ativa CSP estrita e adiciona verificadores estático e de navegador | Sim, desde que o commit do Tailwind local já esteja presente |
| `6130e17` | Uniformiza shell, logout, logo, rotas limpas e gate inicial das nove telas administrativas | Sim |
| `22aeb0b` | Serve Inter e Chart.js localmente, carrega gráficos somente no Dashboard e reduz a allowlist CSP | Sim |
| `fd966bf` | Troca somente o conteúdo central na navegação administrativa, preservando shell e sessão | Sim, depois do shell uniforme |
| `d7f429a` | Minifica e gera hash dos assets do admin, com cache imutável restrito aos nomes versionados | Sim; exige `npm ci` e `npm run assets:build` |
| `4e6acc5` | Faz a publicação do menu Manutenção gerar Tailwind e assets versionados do admin antes do build .NET | Sim |
| `89a1a9c` | Alinha o fallback sem JavaScript de `/admin` à rota canônica sem `.html` | Sim |
| `fd392fb` | Faz o botão Aplicar do Dashboard consultar a API com o período atualmente selecionado e adiciona teste de regressão | Sim |
| `8ce5e69` | Suaviza os pesos tipográficos do admin para remover o aspecto de halo no tema escuro e adiciona teste de regressão | Sim |
| `6df19be` | Centraliza verticalmente o resumo “Cockpit administrativo” sem alterar os controles de período | Sim, depois do ajuste tipográfico |

Os commits devem permanecer nessa ordem. A CSP não permite reintroduzir o Play
CDN nem código inline; a navegação interna pressupõe o shell uniforme, e os
artefatos com hash devem ser gerados somente depois das fontes do admin.

Não há migration, alteração de banco, rotação de segredo ou mudança de unit
systemd nesses commits. Chromium, ChromeDriver, Node.js e npm instalados no
host são ferramentas de build/teste e não fazem parte do processo da API em
runtime.

## O que mudou

### Tailwind

- Tailwind `3.4.19` está fixado em `package-lock.json`.
- `assets/tailwind.css` é a entrada e `wwwroot/css/tailwind.css` é o artefato
  versionado servido pela aplicação.
- `npm run css:build` recompila o CSS minificado; `npm run css:watch` atende ao
  desenvolvimento.
- O scanner considera `wwwroot/**/*.html` e `wwwroot/**/*.js`, excluindo o
  Chart.js de terceiros e os arquivos gerados do admin para evitar classes
  incidentais e crescimento do CSS público.
- As cinco páginas públicas deixaram de carregar `cdn.tailwindcss.com`.
- A manutenção de publicação recompila o CSS antes do build .NET e aborta se
  essa etapa falhar.

O Play CDN não deve voltar a ser usado em produção. Uma atualização para
Tailwind 4 é uma mudança maior e deve ficar em branch/commit próprios, com nova
comparação visual e revisão de compatibilidade de navegadores.

### Assets e navegação do admin

- Inter variável `5.3.0` e Chart.js `4.4.0` ficam em
  `wwwroot/admin/assets/fonts` e `wwwroot/admin/assets/vendor`, acompanhados das
  respectivas licenças.
- Chart.js é solicitado somente quando o Dashboard é aberto; as demais telas
  não transferem os aproximadamente 205 KB da biblioteca.
- Após a primeira validação de sessão, o menu busca o HTML da rota canônica e
  substitui somente `#main > .content`. O shell, os modais, o JavaScript e a
  sessão permanecem carregados; voltar/avançar do navegador continua suportado.
- `tools/build-admin-assets.mjs`, com esbuild `0.28.1` fixado, minifica as fontes
  `admin.css/js`, calcula SHA-256 abreviado e atualiza as nove páginas para
  `admin.<hash>.min.css/js`.
- Somente esses dois nomes com hash recebem cache público de um ano e
  `immutable`. HTML, APIs, fontes editáveis, Chart.js e demais arquivos de
  aplicação continuam sem armazenamento.
- No Dashboard, **Aplicar** lê o valor atual do seletor antes de recarregar os
  dados e envia também as datas quando o período é personalizado.
- Os pesos do texto administrativo ficam limitados aos níveis regulares e
  médios usados no tema escuro; o resumo do cockpit é centralizado sem mover os
  controles de período.
- O JavaScript compartilhado permanece em uma fonte única porque as telas usam
  estado, ações declarativas e modais comuns. Como o shell agora o carrega uma
  única vez, dividi-lo por tela teria maior risco de regressão e pouco ganho
  adicional; o Chart.js, que é o bloco pesado e isolável, já foi separado.

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
- O único provedor frontend externo permitido é o Cloudflare Turnstile;
  Chart.js e Inter são servidos pela própria aplicação.
- `object-src 'none'`, `base-uri 'none'`, `form-action 'self'` e
  `frame-ancestors 'none'` continuam como barreiras adicionais.

`tools/check-csp-browser.mjs` mantém uma cópia da política aplicada em
`Program.cs`. Qualquer mudança futura na CSP deve atualizar os dois locais no
mesmo commit, para que o teste represente a política real.

## Evidências já obtidas

Na linha integrada à `development` foram executados:

- `npm run assets:build` repetido com saída determinística;
- `npm audit` com zero vulnerabilidades conhecidas;
- build .NET 8 em Release com zero warnings e zero erros;
- verificação de sintaxe de todos os `.js` e `.mjs`;
- `node tools/check-csp.mjs`, com zero scripts, estilos, eventos ou templates
  dinâmicos incompatíveis e zero ações declarativas ausentes;
- teste das 15 páginas completas no Chromium, com a política exata de
  `Program.cs`: zero violações CSP e zero exceções JavaScript;
- abertura/fechamento dos modais principais, troca das telas de autenticação,
  fonte/Chart.js locais e navegação administrativa sem segunda carga de shell
  ou sessão;
- seleção de outro período no Dashboard seguida de **Aplicar**, confirmando a
  nova consulta à API, além da verificação automatizada dos pesos tipográficos;
- comparação visual do peso dos textos e do alinhamento vertical do resumo
  “Cockpit administrativo” no tema escuro;
- comparação visual da landing em desktop e mobile após a migração do Tailwind.

O teste de navegador usa servidor somente em `127.0.0.1` e fixtures sem efeitos.
Ele comprova carregamento, integridade básica dos handlers e compatibilidade com
a CSP, mas não substitui os fluxos autenticados nem integrações reais.

## Validação automatizada antes de integrar

Execute no host de runtime, dentro do repositório:

```bash
npm ci
npm run assets:build
npm audit
for file in $(rg --files wwwroot tools -g '*.js' -g '*.mjs'); do node --check "$file"; done
node tools/check-csp.mjs
dotnet build -c Release --no-restore
git diff --check
```

Depois do build, revise o diff de `wwwroot/css/tailwind.css`, das nove páginas
administrativas e de `wwwroot/admin/assets/build/`. Mudanças de hash devem
corresponder a mudanças nas fontes; uma alteração inesperada precisa ser
entendida antes do commit.

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
ADMIN_SESSION_GATE_FAILURES=0
ADMIN_SHELL_FAILURES=0
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
  No Dashboard, selecione pelo menos **Hoje**, **Últimos 7 dias** e
  **Personalizado**, pressione **Aplicar** e confirme o rótulo e as datas
  devolvidas. Confira também que o resumo “Cockpit administrativo” está
  centralizado verticalmente e que os textos não apresentam halo ou negrito
  excessivo.
- Pedidos/Usuários/Testes grátis/Active Directory: abrir e fechar todos os
  modais, filtros, menus, tooltips, tabelas responsivas e confirmações, sem
  confirmar a ação destrutiva.
- Em um acesso direto, deve aparecer somente o estado neutro "Validando sessão
  administrativa..." até a resposta do servidor; o formulário de login não
  deve piscar. Depois disso, os links devem manter logo/menu/logout e trocar só
  o conteúdo, sem novo gate, novo download do shell ou novo `/api/admin/session`.
- Os links do menu devem permanecer nas rotas canônicas sem `.html`, sem resposta
  301 intermediária. Teste também voltar/avançar, acesso direto e recarga em cada
  rota. Alterações não salvas de WhatsApp devem continuar pedindo confirmação.
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
5. Integre, na ordem, `6130e17`, `22aeb0b`, `fd966bf`, `d7f429a`, `4e6acc5` e
   `89a1a9c`; execute `npm ci`, `npm run assets:build`, os verificadores e o
   teste Chromium.
6. Integre, na ordem, `fd392fb`, `8ce5e69` e `6df19be`; repita o build dos
   assets e o teste Chromium, com atenção ao filtro e à aparência do Dashboard.
7. Publique/reinicie novamente e execute os testes pós-implantação abaixo.
8. Integre commits posteriores de documentação sem alterar a ordem histórica.

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
curl -sS -o /dev/null -D - https://phost.pro/admin/dashboard
```

Verifique:

- `Strict-Transport-Security` com 180 dias, sem `includeSubDomains` e sem
  `preload`;
- CSP sem `'unsafe-inline'`, com `script-src-attr 'none'` e
  `style-src-attr 'none'`;
- `X-Frame-Options: DENY`;
- `X-Content-Type-Options: nosniff`;
- `Referrer-Policy: strict-origin-when-cross-origin`;
- APIs, rotas fora da allowlist pública e arquivos CSS/JS sem hash com
  `no-store` também na CDN;
- `/assets/build/public.<nome>.<hash>.css/js` com cache imutável de um ano;
- `/`, `/painel` e `/privacidade` com `no-store` no navegador e microcache de
  60 segundos exclusivamente na Cloudflare;
- `/admin/assets/build/admin.<hash>.min.css/js` com
  `public, max-age=31536000, immutable` no navegador e na CDN;
- `/css/tailwind.css`, CSS extraídos e scripts de `/js/` respondendo `200`.

HSTS só passa a ser obedecido pelo navegador depois de uma resposta HTTPS. Ele
não substitui o redirecionamento da primeira requisição HTTP. A configuração
atual protege apenas o host que emitiu o cabeçalho; não ampliar para
`includeSubDomains` ou `preload`, pois outros subdomínios hospedam APIs
independentes.

Depois dos cabeçalhos, repita em navegador real os testes manuais sem efeitos e
o login cliente/admin. Confira especialmente Turnstile, Inter local, Chart.js
local, cupom, indicação, perfil e componentes dinâmicos do admin.

### Estado observado em 21 de julho de 2026

- `premierapi` reiniciou e permaneceu `active/running`;
- o health check local em `/api/checkout/pricing-rules` respondeu `200`;
- as 15 rotas do verificador responderam `200` em HTTPS; `/painel` teve um
  timeout transitório durante a subida e depois respondeu `200` três vezes;
- HTTP redirecionou para HTTPS com `301`;
- HSTS, CSP, `X-Frame-Options`, `X-Content-Type-Options` e Referrer-Policy foram
  confirmados na resposta publicada;
- HTML administrativo respondeu com `no-store`, e o JavaScript com hash recebeu
  `public, max-age=31536000, immutable` tanto na origem quanto pela Cloudflare.

O processo levou aproximadamente 27 segundos para começar a escutar após o
restart. A unit atual executa `dotnet run --configuration Release`, que aplica o
perfil `http` de `Properties/launchSettings.json` e inicia o ASP.NET em ambiente
`Development`. Em produção, a unit deve usar `--no-launch-profile` ou executar o
DLL publicado com `ASPNETCORE_ENVIRONMENT=Production`. Alterar a unit ou os
arquivos protegidos requer autorização e janela operacional próprias.

## O que monitorar

Nas primeiras horas após a implantação, observe:

- violações CSP e erros JavaScript no console do navegador;
- respostas 404/403/5xx para os novos arquivos CSS/JS;
- HTML apontando para um hash inexistente depois de publicação parcial;
- assets com hash sem `immutable`, ou HTML/API cacheados indevidamente;
- falhas de Turnstile ou gráficos vazios;
- nova chamada a `/api/admin/session` ou novo download do JavaScript a cada
  clique no menu administrativo;
- botões que alteram o texto mas deixam de executar a ação seguinte;
- modais, tooltips, paginação, filtros e ordenação do admin;
- diferenças de layout em páginas pouco acessadas ou no celular;
- aumento de erros no log da aplicação, sem imprimir ambiente ou segredos.

Uma evolução útil, em commit separado, é adicionar coleta de relatórios CSP
(`report-to`/`report-uri`) em endpoint limitado e sem dados sensíveis. O
Turnstile continuará dependendo dos domínios Cloudflare.

## Rollback

Não use `git reset --hard` no servidor. Faça rollback por commit de reversão e
republique com o mesmo processo de build.

- Problema apenas na CSP/handlers: reverta `9648368`. O Tailwind local pode
  permanecer e a política anterior volta a aceitar o frontend.
- Problema no Tailwind local: reverta primeiro a CSP e somente depois `52fe549`.
  Reverter o Tailwind mantendo a CSP estrita reintroduziria um CDN que a política
  não autoriza e pode deixar a interface sem estilos.
- Problema em Chart/fonte locais: reverta `22aeb0b`; esse commit restaura também
  as permissões CSP externas correspondentes.
- Problema na navegação interna: reverta `fd966bf`; as rotas voltam a recarregar
  páginas completas, sem afetar autenticação ou banco.
- Problema em minificação/cache: reverta `d7f429a`; as páginas voltam a apontar
  para `admin.css/js` com `no-store`.
- Problema no filtro do Dashboard: reverta `fd392fb`; isso restaura o
  comportamento anterior, no qual o botão podia reutilizar o período já
  carregado.
- Problema somente na aparência dos textos: reverta primeiro `6df19be` e depois
  `8ce5e69`; esses commits não alteram APIs, autenticação nem banco.
- Problema apenas documental: reverta somente o commit de documentação.

Essas alterações não exigem rollback de banco. Antes de reiniciar, valide a
configuração apenas pelos comandos seguros já documentados; nunca exponha o
conteúdo dos arquivos de ambiente ou o estado TOTP.

## Regras para mudanças futuras

- Não adicione `<script>`, `<style>`, atributos `on*` ou `style` inline.
- Registre ações estáticas em `csp-handlers.js` e ações de templates do admin em
  `adminDeclarativeActions`.
- Ao adicionar classe Tailwind, use a classe completa nos arquivos escaneados e
  execute `npm run assets:build`; classes montadas por concatenação podem não entrar
  no CSS gerado.
- Ao adicionar um provedor externo, autorize somente a diretiva e o host
  indispensáveis, atualize o teste de navegador e documente a justificativa.
- Atualizações de Tailwind, Chart.js, Browserslist/caniuse ou outras dependências
  devem ficar em commits próprios. O aviso de `caniuse-lite` desatualizado é
  informativo e não justifica uma atualização automática misturada a segurança.
- Preserve Tailwind, Inter e Chart.js locais e mantenha o admin sem Tailwind,
  salvo decisão arquitetural explícita.
