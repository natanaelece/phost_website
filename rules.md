# Regras de IA e Contexto do Repositorio (AI Rules)

**AVISO AOS AGENTES DE IA (AI ASSISTANTS):** A leitura deste arquivo e OBRIGATORIA antes de iniciar qualquer modificacao no projeto, sob instrucao direta do dono do repositorio.

## 1. Passo 0 - Contexto Geral
Sempre inicie lendo o arquivo README.md na raiz do projeto. Ele contem detalhes essenciais sobre a infraestrutura (LXC, Debian, Postgres local), fluxo de integracao (Asaas + Active Directory) e a finalidade do negocio (Venda de slots/computadores e liberacao automatica via AD).

## 2. Principios de Desenvolvimento
- O Frontend e Vanilla Javascript + HTML estatico servido na pasta wwwroot/. Nao invente frameworks complexos (React, Vue) nem introduza NPM.
- O painel do cliente (index.html e painel.html) usa TailwindCSS via CDN.
- O painel administrativo fica em `wwwroot/admin/` com telas HTML estaticas separadas, CSS Nativo/Puro compartilhado e Vanilla JavaScript compartilhado. Nao insira Tailwind nele sem permissao explicita. O `wwwroot/admin.html` existe apenas como entrada compativel/redirecionamento.
- A API e Stateless. Funcoes de sessao sao baseadas em JWT.
- O banco de dados relacional e orquestrado por Dapper (queries SQL puras), sem Entity Framework. Mantenha essa arquitetura de micro-ORM de alta performance. O arquivo DatabaseInitializer.cs e o responsavel por manter a estrutura do schema.
- As queries do Dapper devem usar explicitamente aliases (AS NomePropriedade) para fazer o mapeamento correto do snake_case do banco para o PascalCase do C#.
- O usuário da aplicação (premierhost_app) é o dono das tabelas. Alterações estruturais (DDL) feitas pelo DatabaseInitializer.cs rodam de forma nativa e automática na inicialização.
- O banco PostgreSQL deve ser criado em UTF8 para suportar emojis e Unicode. O encoding de um banco existente nao deve ser alterado via automacao: PostgreSQL exige dump, criacao de novo banco UTF8 e restore planejado. O DatabaseInitializer.cs pode diagnosticar/avisar, mas nunca deve recriar ou dropar o banco automaticamente.

## 3. Mapa de Arquivos e Responsabilidades (Directory Map)

### /Controllers (API Endpoints)
- AdminController.cs: Centro de comando administrativo. Gerencia usuarios (local e AD), cancelamentos de pedidos, listagem de estatisticas, cockpit financeiro/CRM e acoes coercitivas. O endpoint `GET /api/admin/dashboard` aceita periodos fixos e personalizados (`period`, `start`, `end`) e alimenta Dashboard, Financeiro e CRM do admin.
- AuthController.cs: Fluxo de autenticacao. Realiza o cadastro do usuario, criacao paralela da conta no Active Directory (tratando duplicacoes), Login via JWT e recuperacao de senha.
- CheckoutController.cs: Interface com o Asaas. Processa a escolha do cliente e gera faturas PIX dinamicas.
- WebhookController.cs: Ponto vital. Recebe os gatilhos (webhooks) do Asaas. Quando um PIX e pago, este arquivo e responsavel por aprovar o pedido e disparar a logica que concede acesso ao usuario no Active Directory.
- ProfileController.cs: Edicao de dados triviais do perfil do cliente.

### /Services (Logica de Negocios e Background)
- ActiveDirectoryService.cs: O UNICO responsavel por conversar com o Windows Server (LDAP). Contem metodos para localizar, criar, deletar usuarios, definir senhas, editar (LdapModification) e mover eles entre OUs. Lembre-se: Suas buscas (GetUserDn) usam ScopeSub, logo varrem toda a estrutura partindo do BaseDn. Exige LDAPS (criptografia). Atente-se as limitacoes do LDAP no Linux: "O usuario nao pode alterar a senha" exige acesso ACL que e impossivel; e para renomear o Nome Completo (CN) e mandatorio utilizar ModifyDNRequest, e nao Modify regular.
- AdOrderExpirationWorker.cs: Job assincrono (Worker) que roda de 1 em 1 hora. Varre o banco de dados e expulsa/bloqueia do AD usuarios cujos planos expiraram e nao foram renovados.
- DatabaseInitializer.cs: Garante que o PostgreSQL tenha as tabelas formatadas corretamente. Se adicionar uma coluna nova, faca por aqui.
- TelegramLogger.cs: Servico global de monitoramento. Intercepta logs de erro (Nivel Error) e os encaminha diretamente para o Telegram do administrador.

### /wwwroot (Frontend)
- index.html: Landing page comercial, planos, teste gratuito, video demonstrativo, formulario de cadastro (validacao forte anti-fake), login e redefinicao.
- painel.html: SPA do usuario final e simulador publico de precos. Visitantes podem calcular sem login; login e exigido para gerar PIX, acessar indicacoes, perfil, historico e credenciais. O topo possui modais nativos de Como usar e Duvidas frequentes.
- vid/comofunciona.mp4: Video demonstrativo compartilhado pela landing e pelo modal Como usar. Referencie-o por URL da propria origem (`/vid/comofunciona.mp4`). Ao adicionar midia externa, revise explicitamente a diretiva `media-src` da CSP em Program.cs.
- admin.html: Entrada compativel que redireciona para `/admin/dashboard.html`.
- admin/: Painel Administrativo dividido em telas HTML estaticas (`dashboard.html`, `financeiro.html`, `crm.html`, `pedidos.html`, `usuarios.html`, `active-directory.html`), com UI customizada, modais nativos, fetch direto e CSS/JS compartilhados em `admin/assets/`. Mantenha CSS nativo/puro e reaproveite o endpoint `/api/admin/dashboard` para metricas de receita, pedidos, clientes, vencimentos e fila operacional.
