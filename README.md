# PremierAPI

> Atualizacao do painel admin: o painel administrativo agora fica em `wwwroot/admin/`, com telas HTML estaticas separadas para Dashboard, Financeiro, CRM, Pedidos, Usuarios e Active Directory. O `admin.html` permanece como entrada compativel/redirecionamento. O admin permanece em HTML estatico, CSS nativo e Vanilla JavaScript; nao usa Tailwind sem permissao explicita.

Plataforma de gerenciamento e automação para venda e gestão de slots/acessos (WYD) integrada com Active Directory e faturamento automático via Asaas (PIX).

> [!IMPORTANT]
> **🤖 ASSISTENTES DE IA:** Consultem obrigatoriamente o arquivo [rules.md](rules.md) na raiz do projeto antes de propor arquiteturas, adicionar pacotes, ou realizar alterações estruturais. Ele contém o mapa de arquivos e os princípios do sistema.

## 🚀 Visão Geral

O **PremierAPI** atua como um sistema de *Backend for Frontend* (BFF), orquestrando o painel administrativo (`wwwroot/admin/`) e o painel do cliente (`painel.html`). Ele automatiza a criação de contas no Windows Server (Active Directory), gerencia licenças e acessos a computadores e realiza cobranças de assinaturas de forma automatizada por meio da API da Asaas.

## 🛠 Tecnologias Utilizadas

- **Backend:** C# / .NET 8 (ASP.NET Core Web API)
- **Banco de Dados:** PostgreSQL (Dapper para micro-ORM)
- **Integrações Externas:** 
  - Asaas API (Geração de PIX, Webhooks, Reembolso)
  - Active Directory / LDAP (Criação de usuários, troca de senha, grupos)
- **Frontend (Integrado em `wwwroot/`):** HTML5, Vanilla JavaScript, CSS Nativo UI (no admin) e **Tailwind CSS** via CDN (usado extensamente no Painel do Cliente, Login e Recuperação de senha).
- **Autenticação:** JWT (JSON Web Tokens)

## 📦 Estrutura do Projeto

```text
/PremierAPI
│
├── Controllers/              # Controladores da API (Admin, Auth, Checkout, Webhook, Analytics)
├── Services/                 # Regras de Negócio e Background Workers
│   ├── ActiveDirectoryService.cs    # Fronteira LDAP/LDAPS baseada em Novell.Directory.Ldap
│   ├── AdAccountProvisioningService.cs # Criação, vínculo e renovação do usuário AD após pagamento
│   ├── AdAccountProvisioningWorker.cs  # Reconcilia provisionamentos pagos que falharam temporariamente
│   ├── AdPasswordProtectionService.cs  # Protege a senha somente enquanto a criação no AD está pendente
│   ├── AdOrderExpirationWorker.cs   # Arquiva no AD licenças vencidas às 01:00 do dia seguinte
│   ├── EmailConfirmationReminderWorker.cs # Executa até dois reenvios automáticos de confirmação
│   ├── PricingRules.cs               # Fonte única das regras comerciais e cotações
│   └── DatabaseInitializer.cs       # Seed e criação de tabelas automáticas no PostgreSQL
│
├── wwwroot/                  # Aplicação Frontend
│   ├── admin.html            # Entrada compatível que redireciona para /admin/dashboard.html
│   ├── admin/                # Painel Administrativo separado por tela
│   │   ├── dashboard.html
│   │   ├── financeiro.html
│   │   ├── crm.html
│   │   ├── pedidos.html
│   │   ├── usuarios.html
│   │   ├── active-directory.html
│   │   ├── logs.html         # Eventos da execução atual, com filtros e atualização automática
│   │   ├── assets/           # CSS e JavaScript compartilhados do admin
│   │   └── partials/         # Modais compartilhados
│   ├── painel.html           # Dashboard do cliente (minha conta, meus pedidos)
│   ├── vid/
│   │   └── comofunciona.mp4  # Vídeo demonstrativo da landing e do modal de ajuda
│   └── recuperar-senha.html  # Fluxo de redefinição de senha
│
├── appsettings.json          # Configurações do ambiente (Asaas, Postgres, AD, JWT)
├── Program.cs                # Entry point da aplicação e Injeção de Dependências
└── restart.sh                # Script utilitário para reiniciar e fazer build da API no servidor
```

## ⚙️ Pré-requisitos e Instalação

1. **SDK .NET 8** instalado no servidor.
2. **PostgreSQL** rodando e acessível.
3. Servidor Windows (ou permissão de delegação LDAP) para acesso ao **Active Directory**.
4. Chaves da API do **Asaas** (Produção e Sandbox).

O servidor também possui **Node.js 18** somente para validar a sintaxe dos JavaScripts estáticos. O projeto não usa NPM, `package.json`, bundler ou framework frontend.

### Passo a Passo

1. **Clonar o Repositório** e navegar até a pasta do projeto.
2. **Configuração de Ambiente:** Copie e configure o arquivo `appsettings.json` com suas variáveis de banco de dados, chaves do Asaas e Active Directory. **Nota Importante:** É obrigatório definir as Variáveis de Ambiente `AdminEmail` e `AdminToken` no servidor (LXC/Docker) ou no appsettings para habilitar e proteger o acesso ao Painel Admin.
3. **Restaurar e Compilar:**
   ```bash
   dotnet restore
   dotnet build
   ```
4. **Execução:**
   ```bash
   # Você pode utilizar o script de bash caso esteja rodando sob um ambiente Unix/Linux:
   ./restart.sh
   # Ou rodar via dotnet diretamente:
   dotnet run
   ```

A aplicação fará a criação e atualização automática da estrutura de tabelas do banco de dados graças ao `DatabaseInitializer.cs`.

As automações usam as seções de configuração `AdProvisioning`, `AdExpiration` e `EmailConfirmationReminders`. Os intervalos definem apenas a frequência de reconciliação; o horário comercial continua autoritativo no fuso configurado. O padrão é tentar provisionamentos a cada 5 minutos, verificar expirações a cada minuto e procurar confirmações pendentes a cada 5 minutos.

## 💳 Fluxo de Compra e Automação

1. O usuário acessa o Painel e realiza o **Checkout** (compra de pacotes de slots).
2. O `CheckoutController` recalcula o valor no backend e gera no Asaas um QR Code **PIX** individual, de valor fixo, uso único e validade de 15 minutos. Não é solicitado CPF/CNPJ ao cliente.
3. Depois do pagamento, o Asaas cria o cliente e a cobrança correspondentes e envia o `pixQrCodeId` pela rota definida em `WebhookController`, que faz a conciliação com o pedido local. O webhook completa nome, e-mail e WhatsApp do cliente usando o cadastro do site, vincula-o ao grupo `PremierHost` e desativa todas as notificações de cobrança do cliente e do provedor para evitar comunicações e taxas desnecessárias.
4. Somente após o pedido ficar `pago`, o `AdAccountProvisioningService` cria e vincula o usuário no **Active Directory**. O cadastro inicial no site é exclusivamente local e nunca cria conta AD.
5. Uma conta nova recebe nome de usuário único e usa a mesma senha vigente do cadastro no site. O hash BCrypt continua sendo a autenticação local; em paralelo, a senha original fica transitoriamente criptografada pelo ASP.NET Data Protection na tabela `pending_ad_credentials`. O provisionador descriptografa apenas em memória e apaga o registro assim que cria e vincula a conta no AD. O e-mail informa somente o nome de usuário e orienta usar a senha do site.
6. Se o AD estiver offline ou a criação falhar, o pedido permanece pago, a credencial protegida permanece disponível e o `AdAccountProvisioningWorker` tenta reconciliar novamente com intervalo progressivo. Login, redefinição e alteração de senha anteriores ao vínculo atualizam essa credencial. Cada falha usa `LogError` e segue para o Telegram pela infraestrutura de logging existente. Renovações reativam a conta existente e atualizam o maior vencimento pago sem redefinir a senha.
7. A licença vale até o fim da data de vencimento exibida. O atributo `accountExpires` do AD vence à meia-noite seguinte. Às 01:00, no fuso `America/Sao_Paulo`, o `AdOrderExpirationWorker` desativa a conta e a move para a OU de inativos configurada em `ActiveDirectory:ExpiredUsersOu`, salvo quando existe outro pedido pago ainda ativo.

Esse fluxo requer ao menos uma chave Pix ativa na conta Asaas do ambiente utilizado. O QR Code guarda o valor calculado pelo servidor; o pagador não pode escolher ou alterar esse valor.

### Regras comerciais centralizadas

`Services/PricingRules.cs` é a única fonte das quantidades, preços, descontos e arredondamentos comerciais. O backend expõe `GET /api/checkout/pricing-rules` para montar os controles e `POST /api/checkout/pricing-quote` para calcular o total autoritativo. Não replique valores em controladores ou JavaScript.

O simulador e o pedido manual usam as mesmas regras: o padrão é 1 slot; diária começa no menor pacote permitido (3 computadores por 3 dias, atualmente R$ 51 após a regra de arredondamento), semanal no menor valor (R$ 35) e mensal no menor valor com desconto (R$ 105). O total do pedido manual continua editável pelo operador, mas seu preenchimento e recálculo partem sempre da cotação do servidor.

### Pedido criado manualmente

Um pedido criado no admin é identificado por `orders.created_manually` e nasce como `pendente`, separado do conceito `paid_manually`. Ele exige os mesmos dados operacionais do checkout e respeita a regra de um pedido pendente por cliente, mas não cria pagamento fictício nem marca o pedido como pago.

O cliente visualiza esse pedido pendente no painel e pode usar **Gerar PIX**. O endpoint anexa ao próprio pedido um QR estático calculado pelas regras atuais; quando o QR de um pedido manual expira, ele pode ser gerado novamente. No admin, o operador pode marcar o pagamento manualmente ou excluir o rascunho enquanto ainda não existe QR. Depois que o QR existe, cancelamentos seguem o fluxo seguro de conciliação com o Asaas. A coluna e seus ajustes de schema são mantidos pelo `DatabaseInitializer.cs`.

Ao cancelar, `orders.canceled_was_paid` registra se o pedido estava efetivamente pago antes da mudança de status. Pedidos pendentes cancelados aparecem apenas como **Cancelado**; os indicadores **Reembolsado** e **Sem reembolso** são exclusivos de pedidos que estavam pagos.

### Compatibilidade dos webhooks Asaas

- O texto historico enviado ao criar uma cobranca dinamica era `Licença ({periodo}) - AnyDesk: {id}`. Preserve esse formato em qualquer fluxo dinamico legado.
- O QR Code estatico continua recebendo esse texto no campo `description`, mas o Asaas cria a cobranca somente depois do pagamento e pode atribuir a ela a descricao automatica de Pix recebido. Portanto, a descricao presente no evento da cobranca nao e um identificador confiavel para esse fluxo.
- Webhooks que compartilham a mesma conta Asaas devem reconhecer compras atuais da PremierHost pelo `payment.pixQrCodeId`, conciliando esse valor com `orders.asaas_pix_qr_code_id`. A descricao iniciada por `Licença` serve apenas como compatibilidade com cobrancas dinamicas antigas.
- Nao tente corrigir a descricao depois do `PAYMENT_RECEIVED`: a API do Asaas restringe a alteracao de cobrancas ja recebidas e o outro webhook ja pode ter recebido o evento original.
- Se uma integracao externa exigir obrigatoriamente que a descricao da propria cobranca comece com `Licença`, ela precisa ser adaptada para `pixQrCodeId` ou o checkout teria de voltar para cobranca dinamica, que em producao exige CPF/CNPJ. A decisao atual do projeto e manter o QR estatico para nao solicitar documento ao cliente.

## 🔐 Segurança

- As rotas da pasta `wwwroot/` servem HTML de maneira estática. As requisições à API de clientes são feitas em tempo real e protegidas via JWT Header `Authorization: Bearer <token>`.
- Hashes de senha utilizando Bcrypt (via `BCrypt.Net-Next`).
- **Painel Admin:** A autenticação administrativa (`ValidateAdmin`) não depende mais do banco de dados (estateless). O login é feito com validação estrita baseada nas chaves de ambiente secretas do servidor (`AdminToken`).
- Proteção nativa no formulário de acesso com **Cloudflare Turnstile** anti-bot.
- A Content Security Policy de `Program.cs` declara separadamente scripts, estilos, imagens, frames, conexões e mídia. Vídeos locais devem usar URL da própria origem, como `/vid/comofunciona.mp4`; novos provedores externos precisam ser adicionados explicitamente à diretiva `media-src`.

## 🌐 Landing page e Painel do Cliente

A landing page (`wwwroot/index.html`) apresenta o serviço de aluguel de máquinas físicas para WYD, benefícios, segurança, formas de acesso, planos diário/semanal/mensal e teste gratuito de 30 minutos solicitado pelo WhatsApp. Os valores exibidos são referências do cálculo implementado no painel; a contratação e o total definitivo continuam centralizados no simulador.

O cadastro da landing funciona como uma esteira de seis passos: nome, WhatsApp, e-mail, senha, indicação e aceite de privacidade. Quando preenchido, o código de indicação é validado no backend antes de avançar para o aceite; a validação também permanece no envio final. O Turnstile fica fixo na base do modal; erros só aparecem depois de interação ou tentativa de avanço. Após o cadastro, o formulário fecha e um modal de sucesso orienta a confirmação pelo link enviado por e-mail. Esse cadastro permanece apenas no PostgreSQL; nenhuma conta AD é criada antes da confirmação de um pagamento.

Se o e-mail continuar sem confirmação, o `EmailConfirmationReminderWorker` envia no máximo dois lembretes adicionais, sempre em dias diferentes do cadastro: o primeiro às 11:00 do dia seguinte e o segundo às 19:00 do outro dia, no fuso `America/Sao_Paulo`. Falhas SMTP são registradas e tentadas novamente sem consumir uma das duas entregas. Em **Admin > Usuários**, o operador pode reenviar manualmente sem consumir essa cota ou marcar o checkbox para confirmar o endereço manualmente. Quando já houve envio na data atual, o backend devolve a data e a interface exige confirmação explícita antes de permitir outro reenvio.

O painel (`wwwroot/painel.html`) permite consulta pública do simulador de preços. Links no formato abaixo abrem diretamente a área de cálculo e deixam o período correspondente selecionado:

```text
/painel?periodo=diaria#simular-planos
/painel?periodo=semanal#simular-planos
/painel?periodo=mensal#simular-planos
```

Sem autenticação, o visitante pode alterar computadores, instâncias e período para consultar o total. Login ou cadastro são exigidos para gerar PIX, acompanhar pedidos e credenciais, editar o perfil, usar indicações e consultar o histórico.

Antes de gerar o Pix, o cliente informa em até 50 caracteres o nome do servidor de WYD em que pretende jogar. O backend rejeita exclusivamente os nomes `wyd2` e `wyd 2` (ignorando maiúsculas, minúsculas e espaços externos), antes de qualquer chamada ao Asaas; nomes diferentes, inclusive `wyd nome 2`, permanecem permitidos. O valor informado fica salvo em `orders.wyd_server_name` e aparece na tela de Pedidos do admin. A coluna é criada ou atualizada automaticamente pelo `DatabaseInitializer.cs`.

O cabeçalho do painel possui dois modais locais:

- **Como usar:** explica simulação, autenticação, pagamento e liberação, além de exibir o vídeo demonstrativo.
- **Dúvidas frequentes:** responde questões comerciais e operacionais e oferece contato pelo WhatsApp.

Após autenticar e carregar o perfil, o painel exibe uma pequena boas-vindas uma vez por sessão, divulgando o canal oficial da Premier Host no WhatsApp. O usuário pode fechar pelo botão superior, escolher “Agora não” ou abrir o canal em uma nova aba; atualizar a página não repete o modal na mesma sessão.

O vídeo compartilhado por essas duas telas fica em `wwwroot/vid/comofunciona.mp4`. As páginas devem referenciá-lo como `/vid/comofunciona.mp4` para funcionar no domínio principal, em `www` e em ambientes de teste sem conflito com a CSP.

### Indexação pública

O arquivo `wwwroot/sitemap.xml` anuncia ao Google somente as URLs públicas canônicas `/`, `/painel` e `/privacidade`, e `wwwroot/robots.txt` referencia esse sitemap. Rotas administrativas, APIs, confirmação de e-mail e recuperação de senha não devem aparecer nos resultados de busca. Ao restringir bots conhecidos na Cloudflare, mantenha `/sitemap.xml` e `/robots.txt` liberados juntamente com as três páginas públicas e seus recursos de `/img/` e `/vid/`.

## Product Analytics first-party

O frontend utiliza `wwwroot/analytics.js` e o endpoint `POST /api/analytics/events` para medir o funil de contratação sem depender de cookies ou plataformas externas. A tabela `product_analytics_events` é criada de forma idempotente pelo `DatabaseInitializer.cs`, recebe somente eventos e propriedades permitidos e remove registros com mais de 13 meses na inicialização.

O funil acompanha landing, simulador, autenticação, cadastro, tentativa de checkout, Pix gerado, pagamento recebido e acesso entregue. Eventos de pagamento e entrega são confirmados pelo backend. E-mail, WhatsApp, AnyDesk, senha, payload Pix e dados bancários não devem ser enviados como propriedades de analytics.

Não há Google Analytics, Meta Pixel ou outro rastreador de terceiros instalado. Atualmente é registrada apenas a telemetria first-party permitida, incluindo a origem/referrer quando disponível; UTMs e identificadores de clique do Facebook/Google não são persistidos. Qualquer integração futura precisa de avaliação de privacidade, consentimento e CSP antes de ser adicionada.

As contagens por evento e por sessão aparecem no bloco **Funil de produto** do Dashboard administrativo e respeitam o filtro de período já existente.

## 🌐 Detalhes de Infraestrutura e Ambiente
- **Ambiente de Hospedagem:** Container LXC dentro do Proxmox VE (Rodando Debian 12).
- **PostgreSQL:** Rodando localmente no mesmo container LXC, de forma dedicada. 
- **Usuário da Aplicação:** `premierhost_app` (Usuário proprietário/owner de todas as tabelas na base `premierhost`).
- *Nota de Operação:* Como o `premierhost_app` é o dono das tabelas, a aplicação tem permissão nativa para executar comandos estruturais (DDL), permitindo que rotinas como o `DatabaseInitializer.cs` criem índices (`CREATE INDEX IF NOT EXISTS`) e atualizem o esquema automaticamente na inicialização sem bloqueios de segurança. O superusuário `postgres` fica restrito a manutenções globais de infraestrutura via DBeaver.


## Encoding UTF-8 do PostgreSQL

O banco precisa estar em `UTF8` para salvar emoji e caracteres Unicode de forma confiavel nas mensagens de WhatsApp e em qualquer texto livre. Confira o banco atual com:

```bash
psql -d premierhost -c "SELECT datname, pg_encoding_to_char(encoding) AS encoding, datcollate, datctype FROM pg_database WHERE datname = 'premierhost';"
```

Se aparecer `SQL_ASCII`, nao use `ALTER DATABASE`: PostgreSQL nao converte o encoding de um banco existente. O caminho seguro e criar um banco novo em UTF-8 e restaurar um dump nele durante uma janela de manutencao.

Checklist antes de migrar:

- Fazer backup logico e guardar uma copia fora do diretorio da aplicacao.
- Parar a API para evitar escrita durante o dump/restauracao.
- Confirmar owner, permissoes, extensoes usadas e connection string.
- Testar restauracao em um banco temporario antes de trocar producao.
- Conferir textos com acentos, emojis, templates de WhatsApp, login, pedidos e painel admin apos subir.

Fluxo recomendado, ajustando nomes/senhas conforme o ambiente:

```bash
# 1. Parar a aplicacao antes do dump final
systemctl stop premierapi

# 2. Backup do banco atual
pg_dump -Fc -d premierhost -f /var/backups/premierhost_before_utf8.dump

# 3. Criar banco novo em UTF-8 usando template0
createdb -T template0 -E UTF8 --lc-collate=C.UTF-8 --lc-ctype=C.UTF-8 -O premierhost_app premierhost_utf8

# 4. Restaurar no banco novo
pg_restore -d premierhost_utf8 --no-owner --role=premierhost_app /var/backups/premierhost_before_utf8.dump

# 5. Conferir encoding e dados principais
psql -d premierhost_utf8 -c "SHOW server_encoding;"
psql -d premierhost_utf8 -c "SELECT COUNT(*) FROM users; SELECT COUNT(*) FROM orders;"

# 6. Trocar a connection string para premierhost_utf8 e subir a API
systemctl start premierapi
```

Riscos principais: downtime durante a troca, falha de restore por objetos/permissoes, dados antigos com bytes invalidos vindos do `SQL_ASCII`, e connection string apontando para o banco antigo. Por isso, a troca deve ser testada em banco temporario antes de virar producao.

O `DatabaseInitializer.cs` valida o encoding na inicializacao e emite um aviso critico quando o banco nao esta em UTF-8, mas ele nao tenta migrar automaticamente por ser uma operacao estrutural e potencialmente destrutiva.

## Painel Administrativo

O painel administrativo fica em `wwwroot/admin/` e usa HTML estatico, CSS nativo e Vanilla JavaScript. Cada area principal tem seu proprio `.html`, enquanto `admin/assets/admin.css`, `admin/assets/admin.js` e `admin/partials/modals.html` concentram estilos, logica compartilhada e modais. Ele nao usa framework frontend e, por regra do projeto, nao deve receber Tailwind sem permissao explicita.

Principais areas do painel:

- **Dashboard:** cockpit executivo com receita por periodo, ticket medio, conversao, MRR estimado, licencas ativas, clientes ativos, fila operacional, ranking de clientes e pedidos recentes.
- **Financeiro:** analise de receita paga, total historico, pedidos manuais, conversao, receita por plano, tipo de pedido e status dos pedidos.
- **CRM:** visao de clientes ativos, licencas vencendo, entregas pendentes, novos usuarios, proximos vencimentos, acoes recomendadas e ranking de clientes.
- **Pedidos, Usuarios e Active Directory:** gestao operacional existente, incluindo pedidos manuais, cancelamentos, marcacao de pagamento, entrega, reenvio/confirmacao de e-mail e controle de acessos no AD. Cabeçalhos clicáveis alternam crescente/decrescente e exibem uma única seta somente na coluna ativa; ao trocar a coluna, a anterior volta ao estado neutro. Pedidos e usuários são ordenados no backend sobre o conjunto completo; dados do AD são ordenados na listagem carregada. No celular, as tabelas viram cartões verticais que preservam todos os dados. O ID do Asaas permanece recolhido até o operador solicitar sua exibição. O vínculo de um cadastro local aceita contas AD das pastas de usuários ativos, expirados e website.
- **Logs:** mostra até 2.000 eventos da execução atual mantidos em memória, com nível, horário, categoria, mensagem, exceção, busca, limite e atualização automática. A API oculta padrões comuns de credenciais antes de armazenar o texto; a lista reinicia junto com o processo e não substitui o histórico persistente do `journal`.

### Operações do Active Directory

Toda comunicação LDAP deve permanecer encapsulada em `Services/ActiveDirectoryService.cs` e usar LDAPS. A tela de Active Directory permite criar, editar, duplicar e excluir grupos globais de segurança e objetos de computador usando os mesmos atributos dos registros atuais. Grupos limitam e validam nome e descrição; computadores usam nome compatível com NetBIOS, `sAMAccountName` terminado em `$`, `dNSHostName`, sistema operacional e estado ativo/desativado. A listagem de computadores também mostra suas associações de grupo e permite adicioná-las ou removê-las pelo modal **Gerenciar**.

O provisionamento automático é idempotente e usa as colunas `orders.ad_provisioned_at`, `orders.ad_provisioning_attempts`, `orders.ad_provisioning_error` e `orders.ad_provisioning_next_attempt_at`. `users.ad_credentials_delivered_at` impede reenvio indevido do nome de usuário. A tabela `pending_ad_credentials` guarda exclusivamente a senha reversivelmente protegida enquanto o usuário ainda não possui vínculo AD; o registro é removido após a criação e nunca deve aparecer em logs, APIs ou e-mails. As chaves do ASP.NET Data Protection ficam no diretório persistente e ignorado pelo Git definido em `DataProtection:KeyRingPath` (padrão `.data-protection-keys` no content root) e são protegidas pelo certificado `key-encryption.pfx`. A senha do certificado usa `DataProtection:CertificatePassword` ou, por compatibilidade, deriva de `AdminToken`; mudanças nesses valores exigem preservar a senha anterior ou reemitir as credenciais pendentes por novo login. O diretório deve integrar o backup operacional e sobreviver a reinicializações/deploys. Migrações idempotentes ficam em `DatabaseInitializer.cs`; pedidos pagos anteriores à implantação são marcados como já conciliados para impedir criação retroativa em massa no AD.

Criar o objeto de computador no AD **não ingressa a máquina física no domínio**. O ingresso ainda precisa ser executado no próprio Windows com credenciais autorizadas. Nunca crie, mova ou exclua objetos reais do AD apenas para testar uma alteração sem autorização expressa.

Ao vincular um computador a um usuário, a descrição do computador pode sugerir automaticamente um grupo no padrão `ACESSO_SRV00-00`. Quando não há sugestão válida, a associação direta já existente entre computador e grupo é reutilizada. Se ainda não houver associação, a API solicita a seleção manual, o admin abre o modal e salva a escolha incluindo o próprio objeto de computador no grupo do AD; nas próximas operações esse vínculo persistido evita nova pergunta. Essa ocorrência usa `Warning`, aparece em **Admin > Logs** e também é enviada ao Telegram. Falhas LDAP recuperáveis usam `Warning`; falhas que impedem a operação usam `Error` e não devem ser descartadas silenciosamente.

## Validação antes de concluir alterações

Não existe atualmente um projeto de testes automatizados. Execute verificações proporcionais à mudança e, antes de commits de código, prefira o conjunto abaixo:

```bash
dotnet build --no-restore
for file in $(rg --files wwwroot -g '*.js'); do node --check "$file"; done
node -e 'const fs=require("fs"),vm=require("vm");for(const file of process.argv.slice(1)){const html=fs.readFileSync(file,"utf8");const re=/<script(?![^>]*\bsrc=)[^>]*>([\s\S]*?)<\/script>/gi;let m,i=0;while((m=re.exec(html))){i++;new vm.Script(m[1],{filename:file+"#inline-"+i});}}' $(rg --files wwwroot -g '*.html')
git diff --check
```

Essas verificações são locais. Não gere cobranças reais no Asaas e não altere objetos de produção no AD para validar código sem autorização explícita.

### Dashboard por periodo

O endpoint administrativo `GET /api/admin/dashboard` aceita filtros de periodo:

```text
/api/admin/dashboard?period=month
/api/admin/dashboard?period=today
/api/admin/dashboard?period=yesterday
/api/admin/dashboard?period=7d
/api/admin/dashboard?period=30d
/api/admin/dashboard?period=last_month
/api/admin/dashboard?period=quarter
/api/admin/dashboard?period=year
/api/admin/dashboard?period=custom&start=2026-07-01&end=2026-07-10
```

Esse endpoint retorna os blocos usados pelas telas Dashboard, Financeiro e CRM: estatisticas financeiras, serie de receita, status dos pedidos, planos, origem dos pedidos, top clientes, vencimentos proximos, fila operacional e pedidos recentes.

---
*Desenvolvido e mantido para a infraestrutura da PremierHost.*
