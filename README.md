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
├── Controllers/              # Controladores da API (Admin, Auth, Checkout, Webhook)
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
2. O `CheckoutController` chama a API do Asaas e gera um QR Code **PIX**.
3. O Asaas confirma o pagamento através da rota definida em `WebhookController`.
4. Ao constatar o pagamento, o sistema automaticamente gera as permissões e aloca as configurações do **Active Directory**, liberando o acesso ao usuário contratante de acordo com o plano adquirido (dias corridos).
5. O `AdOrderExpirationWorker` roda em segundo plano desativando acessos de licenças expiradas.

## 🔐 Segurança

- As rotas da pasta `wwwroot/` servem HTML de maneira estática. As requisições à API de clientes são feitas em tempo real e protegidas via JWT Header `Authorization: Bearer <token>`.
- Hashes de senha utilizando Bcrypt (via `BCrypt.Net-Next`).
- **Painel Admin:** A autenticação administrativa (`ValidateAdmin`) não depende mais do banco de dados (estateless). O login é feito com validação estrita baseada nas chaves de ambiente secretas do servidor (`AdminToken`).
- Proteção nativa no formulário de acesso com **Cloudflare Turnstile** anti-bot.

## 🌐 Detalhes de Infraestrutura e Ambiente
- **Ambiente de Hospedagem:** Container LXC dentro do Proxmox VE (Rodando Debian 12).
- **PostgreSQL:** Rodando localmente no mesmo container LXC, de forma dedicada. 
- **Usuário da Aplicação:** `premierhost_app` (Usuário proprietário/owner de todas as tabelas na base `premierhost`).
- *Nota de Operação:* Como o `premierhost_app` é o dono das tabelas, a aplicação tem permissão nativa para executar comandos estruturais (DDL), permitindo que rotinas como o `DatabaseInitializer.cs` criem índices (`CREATE INDEX IF NOT EXISTS`) e atualizem o esquema automaticamente na inicialização sem bloqueios de segurança. O superusuário `postgres` fica restrito a manutenções globais de infraestrutura via DBeaver.

## Painel Administrativo

O painel administrativo fica em `wwwroot/admin/` e usa HTML estatico, CSS nativo e Vanilla JavaScript. Cada area principal tem seu proprio `.html`, enquanto `admin/assets/admin.css`, `admin/assets/admin.js` e `admin/partials/modals.html` concentram estilos, logica compartilhada e modais. Ele nao usa framework frontend e, por regra do projeto, nao deve receber Tailwind sem permissao explicita.

Principais areas do painel:

- **Dashboard:** cockpit executivo com receita por periodo, ticket medio, conversao, MRR estimado, licencas ativas, clientes ativos, fila operacional, ranking de clientes e pedidos recentes.
- **Financeiro:** analise de receita paga, total historico, pedidos manuais, conversao, receita por plano, tipo de pedido e status dos pedidos.
- **CRM:** visao de clientes ativos, licencas vencendo, entregas pendentes, novos usuarios, proximos vencimentos, acoes recomendadas e ranking de clientes.
- **Pedidos, Usuarios e Active Directory:** gestao operacional existente, incluindo pedidos manuais, cancelamentos, marcacao de pagamento, entrega e controle de acessos no AD.

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
