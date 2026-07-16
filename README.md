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
│   ├── ActiveDirectoryService.cs    # Manipulação do AD (LDAP) via System.DirectoryServices
│   ├── AdOrderExpirationWorker.cs   # Background job que verifica e expira licenças não pagas
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

## 💳 Fluxo de Compra e Automação

1. O usuário acessa o Painel e realiza o **Checkout** (compra de pacotes de slots).
2. O `CheckoutController` recalcula o valor no backend e gera no Asaas um QR Code **PIX** individual, de valor fixo, uso único e validade de 15 minutos. Não é solicitado CPF/CNPJ ao cliente.
3. Depois do pagamento, o Asaas cria o cliente e a cobrança correspondentes e envia o `pixQrCodeId` pela rota definida em `WebhookController`, que faz a conciliação com o pedido local. O webhook completa nome, e-mail e WhatsApp do cliente usando o cadastro do site, vincula-o ao grupo `PremierHost` e desativa todas as notificações de cobrança do cliente e do provedor para evitar comunicações e taxas desnecessárias.
4. Ao constatar o pagamento, o sistema automaticamente gera as permissões e aloca as configurações do **Active Directory**, liberando o acesso ao usuário contratante de acordo com o plano adquirido (dias corridos).
5. O `AdOrderExpirationWorker` roda em segundo plano desativando acessos de licenças expiradas.

Esse fluxo requer ao menos uma chave Pix ativa na conta Asaas do ambiente utilizado. O QR Code guarda o valor calculado pelo servidor; o pagador não pode escolher ou alterar esse valor.

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

O vídeo compartilhado por essas duas telas fica em `wwwroot/vid/comofunciona.mp4`. As páginas devem referenciá-lo como `/vid/comofunciona.mp4` para funcionar no domínio principal, em `www` e em ambientes de teste sem conflito com a CSP.

### Indexação pública

O arquivo `wwwroot/sitemap.xml` anuncia ao Google somente as URLs públicas canônicas `/`, `/painel` e `/privacidade`, e `wwwroot/robots.txt` referencia esse sitemap. Rotas administrativas, APIs, confirmação de e-mail e recuperação de senha não devem aparecer nos resultados de busca. Ao restringir bots conhecidos na Cloudflare, mantenha `/sitemap.xml` e `/robots.txt` liberados juntamente com as três páginas públicas e seus recursos de `/img/` e `/vid/`.

## Product Analytics first-party

O frontend utiliza `wwwroot/analytics.js` e o endpoint `POST /api/analytics/events` para medir o funil de contratação sem depender de cookies ou plataformas externas. A tabela `product_analytics_events` é criada de forma idempotente pelo `DatabaseInitializer.cs`, recebe somente eventos e propriedades permitidos e remove registros com mais de 13 meses na inicialização.

O funil acompanha landing, simulador, autenticação, cadastro, tentativa de checkout, Pix gerado, pagamento recebido e acesso entregue. Eventos de pagamento e entrega são confirmados pelo backend. E-mail, WhatsApp, AnyDesk, senha, payload Pix e dados bancários não devem ser enviados como propriedades de analytics.

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
- **Pedidos, Usuarios e Active Directory:** gestao operacional existente, incluindo pedidos manuais, cancelamentos, marcacao de pagamento, entrega e controle de acessos no AD. As listagens permitem ordenar os resultados por campo; no celular, as tabelas viram cartões verticais que preservam todos os dados. O ID do Asaas permanece recolhido até o operador solicitar sua exibição. O vínculo de um cadastro local aceita contas AD das pastas de usuários ativos, expirados e website.

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
